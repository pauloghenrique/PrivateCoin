using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Povix.Dex.Models;
using Povix.Dex.Services;
using PrivateCoin.Core;

internal static partial class PovixDexRegression
{
    public static void RunTokenPolicy(string directory, string node)
    {
        Directory.CreateDirectory(directory);
        using (var issuer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            string owner = issuer.CreateReceiveAddress(), coadmin = issuer.CreateReceiveAddress(), a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress();
            var chain = new Blockchain(); LegacyConsensusFixture.FundBatch(chain, owner, a, b);
            LegacyConsensusFixture.SelfBlock(chain, new[] { first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 0), second.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 0) });
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
            int dexPort = Port();
            using (var peer = new PeerNode(Port())) using (var batches = new ValidationBatchFixture(chain))
            {
                Transaction received = null;
                peer.SynchronizationRequested += (s, e) => peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                peer.TransactionReceived += (s, e) => Interlocked.Exchange(ref received, e.Transaction);
                peer.Start(); var service = OpenOfflineService(directory, dexPort);
                try
                {
                    peer.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult(); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().CanCreate, "policy DEX synchronizes");
                    var model = new CreateTokenViewModel { Name = "Opções", Symbol = "OPT", Supply = "90", Decimals = 0, DestinationAddress = owner,
                        UsePolicy = true, Mintable = true, MaximumSupply = "200", Burnable = true, Pausable = true,
                        AdministratorAddresses = owner + "\n" + coadmin, RequiredSignatures = 2, Description = "Descrição <texto> | : ◇", Website = "https://example.com/",
                        LogoUrl = "https://example.com/logo.png", SocialUrl = "https://example.com/social", VestingStartHeight = chain.Blocks.Count + 5, VestingIntervalBlocks = 2, VestingTranches = 3 };
                    var creation = service.Prepare(model, PublicKeys(issuer), owner, "policy-session");
                    var d = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(creation));
                    var expected = new { Name = model.Name, Symbol = model.Symbol, Supply = model.Supply, Decimals = "0", DestinationAddress = owner, FeeAtomic = d["feeAtomic"],
                        PolicyValues = new[] { "1", "200", "1", "1", owner + "," + coadmin, "2", model.Description, model.LogoUrl, model.Website, model.SocialUrl,
                            model.VestingStartHeight.ToString(), "2", "3", "public" } };
                    File.WriteAllText(Path.Combine(directory, "policy-fixture.json"), Json.Serialize(new { wallet = EncryptedWallet(issuer), draft = creation, expected,
                        networkId = Blockchain.NetworkId, changeAddress = owner, mode = "create", networkHeight = chain.Blocks.Last().Height }));
                    RunPolicyBrowser(node, directory);
                    var result = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(directory, "policy-result.json")));
                    var signatures = ((System.Collections.IEnumerable)result["signatures"]).Cast<string>().ToArray();
                    string createId = service.SubmitAsync((string)d["draftId"], signatures, "policy-session").GetAwaiter().GetResult();
                    Wait(() => received?.Id == createId, "policy creation propagates through P2P");
                    batches.Confirm(chain, new[] { received }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().Height == chain.Blocks.Last().Height, "creation block adopted by DEX");
                    string tokenId = (string)d["tokenId"];
                    var catalogue = Json.Deserialize<object[]>(Json.Serialize(service.GetManagementTokens()));
                    var token = (Dictionary<string, object>)catalogue.Single();
                    Check((string)token["supplyAtomic"] == "90" && (int)token["requiredSignatures"] == 2, "confirmed policy available for management");
                    var management = service.PrepareManagement(tokenId, "mint", "10", owner, 2, PublicKeys(issuer), owner, "policy-session");
                    d = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(management));
                    File.WriteAllText(Path.Combine(directory, "policy-fixture.json"), Json.Serialize(new { wallet = EncryptedWallet(issuer), draft = management,
                        expected = new { token, operation = "mint", amountAtomic = "10", destination = owner, changeAddress = owner, feeAtomic = d["feeAtomic"] },
                        networkId = Blockchain.NetworkId, changeAddress = owner, mode = "manage", networkHeight = chain.Blocks.Last().Height }));
                    RunPolicyBrowser(node, directory);
                    result = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(directory, "policy-result.json")));
                    signatures = ((System.Collections.IEnumerable)result["signatures"]).Cast<string>().ToArray();
                    var admins = ((System.Collections.IEnumerable)result["administratorSignatures"]).Cast<string>().ToArray();
                    bool rejected = false;
                    try { service.SubmitManagementAsync((string)d["draftId"], signatures, admins.Take(1).ToArray(), "policy-session").GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
                    Check(rejected, "service rejects incomplete co-signatures");
                    string mintId = service.SubmitManagementAsync((string)d["draftId"], signatures, admins, "policy-session").GetAwaiter().GetResult();
                    Check(service.SubmitManagementAsync((string)d["draftId"], signatures, admins, "policy-session").GetAwaiter().GetResult() == mintId, "management submission is idempotent");
                    rejected = false;
                    try { service.SubmitManagementAsync((string)d["draftId"], signatures, admins, "other-session").GetAwaiter().GetResult(); } catch (TokenOperationException) { rejected = true; }
                    Check(rejected, "accepted management draft remains session bound");
                    Wait(() => received?.Id == mintId, "mint propagates through P2P");
                    batches.Confirm(chain, new[] { received }, validators); peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().Height == chain.Blocks.Last().Height, "mint block adopted by DEX");
                    var status = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(service.GetManagementStatus(mintId)));
                    Check((string)status["status"] == "confirmed" && (int)status["confirmations"] > 0, "management receipt confirms only after block");
                    service.Dispose(); service = OpenOfflineService(directory, dexPort);
                    token = (Dictionary<string, object>)Json.Deserialize<object[]>(Json.Serialize(service.GetManagementTokens())).Single();
                    status = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(service.GetManagementStatus(mintId)));
                    Check((string)token["supplyAtomic"] == "100" && (string)status["status"] == "confirmed", "restart restores token supply and confirmed management receipt");
                }
                finally { service.Dispose(); }
            }
        }
    }
    private static void RunPolicyBrowser(string node, string directory)
    {
        using (var process = Process.Start(new ProcessStartInfo(node) { UseShellExecute = false, Arguments = "Tests/PovixDexTokenPolicyWalletRegression.js " + directory }))
        { process.WaitForExit(); Check(process.ExitCode == 0, "browser policy review and Web Crypto signatures"); }
    }
}
internal static class PovixDexTokenPolicyMain
{
    public static void Main(string[] args) { PovixDexRegression.RunTokenPolicy(args[0], args[1]); }
}
