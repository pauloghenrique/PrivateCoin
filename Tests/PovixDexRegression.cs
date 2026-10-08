using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Povix.Dex.Models;
using Povix.Dex.Services;
using PrivateCoin.Core;

internal static class PovixDexRegression
{
    private static int checks;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private const string Password = "dex-test-password-only";
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Wait(Func<bool> ready, string name) { var timer = Stopwatch.StartNew(); while (!ready() && timer.ElapsedMilliseconds < 15000) Thread.Sleep(25); Check(ready(), name); }
    private static string[] PublicKeys(Wallet wallet) => wallet.ExportPrivateKeys().Select(xml => {
        using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); return rsa.ToXmlString(false); }
    }).ToArray();
    private static byte[] Random(int count) { byte[] value = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(value); return value; }
    private static byte[] Combined(params byte[][] parts) => parts.SelectMany(value => value).ToArray();
    private static object EncryptedWallet(Wallet wallet)
    {
        byte[] salt = Random(16), iv = Random(16), key;
        using (var derivation = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(Password), salt, 210000, HashAlgorithmName.SHA256)) key = derivation.GetBytes(64);
        byte[] cipher;
        using (var aes = Aes.Create()) {
            aes.Key = key.Take(32).ToArray(); aes.IV = iv;
            byte[] clear = Encoding.UTF8.GetBytes(Json.Serialize(new { Name = "Carteira de teste", PrivateKeys = wallet.ExportPrivateKeys() }));
            using (var encryptor = aes.CreateEncryptor()) cipher = encryptor.TransformFinalBlock(clear, 0, clear.Length);
        }
        byte[] tag; using (var hmac = new HMACSHA256(key.Skip(32).ToArray())) tag = hmac.ComputeHash(Combined(salt, iv, cipher));
        return new { format = "povix-dex-wallet-v1", iterations = 210000, salt = Convert.ToBase64String(salt), iv = Convert.ToBase64String(iv), data = Convert.ToBase64String(cipher), hmac = Convert.ToBase64String(tag) };
    }
    private static string[] DecryptWallet(Dictionary<string, object> record)
    {
        byte[] salt = Convert.FromBase64String((string)record["salt"]), iv = Convert.FromBase64String((string)record["iv"]), cipher = Convert.FromBase64String((string)record["data"]), key;
        using (var derivation = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(Password), salt, 210000, HashAlgorithmName.SHA256)) key = derivation.GetBytes(64);
        using (var hmac = new HMACSHA256(key.Skip(32).ToArray())) Check(hmac.ComputeHash(Combined(salt, iv, cipher)).SequenceEqual(Convert.FromBase64String((string)record["hmac"])), "browser backup HMAC interoperates with .NET");
        using (var aes = Aes.Create()) {
            aes.Key = key.Take(32).ToArray(); aes.IV = iv;
            using (var decryptor = aes.CreateDecryptor()) {
                var data = Json.DeserializeObject(Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length))) as Dictionary<string, object>;
                return ((object[])data["PrivateKeys"]).Cast<string>().ToArray();
            }
        }
    }
    public static int Main(string[] args)
    {
        try { Run(args[0], args[1]); Console.WriteLine(checks + " DEX checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run(string directory, string nodeExecutable)
    {
        Directory.CreateDirectory(directory);
        long atomic;
        Check(CreateTokenViewModel.TryParseSupply("92233720368,54775807", 8, out atomic) && atomic == long.MaxValue, "maximum supply converted exactly");
        Check(!CreateTokenViewModel.TryParseSupply("92233720368,54775808", 8, out atomic) &&
            !CreateTokenViewModel.TryParseSupply("1.001", 2, out atomic) &&
            !CreateTokenViewModel.TryParseSupply("0", 8, out atomic), "overflow, extra precision and zero rejected");
        using (var issuer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain((FinalityPolicy)null);
            Block reward;
            chain.TryAddWalletCreationReward(issuer.CreateReceiveAddress(), out reward);
            string destination = issuer.CreateReceiveAddress(), change = issuer.CreateReceiveAddress();
            string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
            chain.TryAddWalletCreationReward(firstAddress, out reward); chain.TryAddWalletCreationReward(secondAddress, out reward);
            chain.AddBlock(new[] { first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, Blockchain.OneCoin, 1) });
            chain.AddBlock(new[] { second.CreateStakeLockTransaction(chain, new Transaction[0], secondAddress, Blockchain.OneCoin, 1) });
            var policy = new FinalityPolicy(chain.Blocks.Count - 1, chain.Blocks.Last().Hash);
            chain = new Blockchain(chain.Blocks, policy);
            Check(chain.IsValid(), "existing test chain contains confirmed funds and validator collateral");
            var model = new CreateTokenViewModel { Name = "Aurora | edição 🌙", Symbol = "AUR", Decimals = 8, Supply = "92233720368,54775807", DestinationAddress = destination, FeePriority = 2 };
            int peerPort = Port(), dexPort = Port();
            using (var journal = new FinalityVoteJournal(Path.Combine(directory, "dex-validator-votes.journal")))
            using (var peer = new PeerNode(peerPort, false, null, policy))
            {
                Transaction received = null;
                peer.SynchronizationRequested += (s, e) => peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                peer.TransactionReceived += (s, e) => { if (e.Transaction.Kind == TransactionKind.TokenCreate) Interlocked.Exchange(ref received, e.Transaction); };
                peer.Start();
                var service = new TokenNetworkService(directory, dexPort, new string[0], policy);
                try
                {
                    bool blocked = false;
                    try { service.Prepare(model, PublicKeys(issuer), change, "regression"); } catch (InvalidOperationException) { blocked = true; }
                    Check(blocked, "offline creation blocked");
                    peer.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult();
                    peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().CanCreate, "DEX synchronizes through an existing peer");
                    object prepared = service.Prepare(model, PublicKeys(issuer), change, "regression");
                    var draft = Json.DeserializeObject(Json.Serialize(prepared)) as Dictionary<string, object>;
                    File.WriteAllText(Path.Combine(directory, "fixture.json"), Json.Serialize(new { wallet = EncryptedWallet(issuer), draft = prepared,
                        expected = new { Name = model.Name, Symbol = model.Symbol, Decimals = "8", Supply = model.Supply, DestinationAddress = destination },
                        networkId = Blockchain.NetworkId, changeAddress = change }));
                    var start = new ProcessStartInfo(nodeExecutable) { UseShellExecute = false };
                    start.Arguments = "Tests/PovixDexWalletRegression.js " + directory;
                    using (var process = Process.Start(start)) { process.WaitForExit(); Check(process.ExitCode == 0, "browser review and signing regression"); }
                    var result = Json.DeserializeObject(File.ReadAllText(Path.Combine(directory, "browser-result.json"))) as Dictionary<string, object>;
                    string[] signatures = ((object[])result["signatures"]).Cast<string>().ToArray();
                    bool wrongOwner = false;
                    try { service.SubmitAsync((string)draft["draftId"], signatures, "another-session").GetAwaiter().GetResult(); }
                    catch (InvalidOperationException) { wrongOwner = true; }
                    Check(wrongOwner, "draft belongs to its original session");
                    bool badSignature = false;
                    try { service.SubmitAsync((string)draft["draftId"], new[] { Convert.ToBase64String(new byte[256]) }, "regression").GetAwaiter().GetResult(); }
                    catch (Exception error) when (error is InvalidOperationException || error is CryptographicException) { badSignature = true; }
                    Check(badSignature, "invalid signature rejected before persistence");
                    string id = service.SubmitAsync((string)draft["draftId"], signatures, "regression").GetAwaiter().GetResult();
                    Wait(() => received != null && received.Id == id, "locally signed token creation propagates over P2P");
                    Check(service.GetRegistration(id).Status == "pending" && chain.GetTokens().Count == 0, "pending receipt does not claim blockchain confirmation");
                    Check(service.GetBalance(issuer.OwnedOneTimeAddresses.ToArray()) == 0, "pending funding output is reserved");
                    Check(service.SubmitAsync((string)draft["draftId"], signatures, "regression").GetAwaiter().GetResult() == id, "submission retry is idempotent");
                    string state = File.ReadAllText(Path.Combine(directory, "dex-network.json"));
                    Check(!state.Contains("RSAKeyValue") || (!state.Contains("<D>") && !state.Contains("<P>")), "persisted network state contains no private keys");
                    service.Dispose();
                    service = new TokenNetworkService(directory, dexPort, new string[0], policy);
                    Check(service.GetRegistration(id).Status == "pending" && !service.GetNetwork().CanCreate, "pending creation restored while synchronization is required again");
                    peer.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult();
                    peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().CanCreate, "restarted DEX resynchronizes");
                    chain.ValidatePendingTransactions(new[] { received });
                    var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.OneCoin), second.CreateValidatorStake(secondAddress, Blockchain.OneCoin) };
                    Block confirmed = chain.AddProofOfStakeBlock(new[] { received }, validators);
                    var coordinator = new FinalityCoordinator(policy, journal);
                    coordinator.Observe(chain, chain.Blocks, validators);
                    if (!coordinator.TryFinalize(chain)) throw new Exception("Validator quorum did not finalize the token block.");
                    Check(chain.IsValid() && chain.GetTokenBalance(new[] { destination }, (string)draft["tokenId"]) == long.MaxValue, "validators confirm exact token supply in a valid block");
                    peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetRegistration(id).Status == "confirmed", "receipt confirms only after receiving a validated block");
                    Check(service.GetRegistration(id).BlockHash == confirmed.Hash && service.GetRegistration(id).Confirmations == 1, "receipt exposes real block hash and confirmations");
                    string[] updatedKeys = DecryptWallet((Dictionary<string, object>)result["encryptedWallet"]);
                    using (var restored = Wallet.FromPrivateKeys(updatedKeys)) Check(restored.OwnedOneTimeAddresses.Contains((string)result["newAddress"]), "locally generated browser address restores in the Core");
                }
                finally { service.Dispose(); }
            }
        }
    }
}
