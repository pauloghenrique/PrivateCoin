using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using PrivateCoin.Core;
using Povix.Bridge;

internal static class MultiAssetNativeRegression {
    static int checks;
    static void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; }
    static T Copy<T>(T value) { using (var s = new MemoryStream()) { var json = new DataContractJsonSerializer(typeof(T)); json.WriteObject(s, value); s.Position = 0; return (T)json.ReadObject(s); } }
    static void Write<T>(string file, T value) { using (var s = File.Create(file)) new DataContractJsonSerializer(typeof(T)).WriteObject(s, value); }
    static void Reject(Action action) { try { action(); } catch (Exception e) { if (e is InvalidOperationException || e is ArgumentException) { checks++; return; } throw; } throw new Exception("Expected rejection"); }
    static void Sign(Transaction tx, string[] keys) {
        var signers = new Dictionary<string, RSACryptoServiceProvider>();
        try { foreach (var xml in keys) { var rsa = new RSACryptoServiceProvider(); rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); signers.Add(BridgeNative.Hash(rsa.ToXmlString(false)), rsa); }
            NativeBridge.Authorize(tx, (address, payload) => Convert.ToBase64String(signers[address].SignData(payload, "SHA256")));
        } finally { foreach (var rsa in signers.Values) rsa.Dispose(); }
    }
    static void Confirm(string dir, Blockchain chain, Transaction tx) {
        if (chain.CanCreateUnsignedBlock(new[] { tx })) { chain.AddBlock(new[] { tx }); return; }
        using (var founder = Wallet.FromPrivateKeys(BridgeNative.Read<string[]>(Path.Combine(dir, "founder.local.json"))))
        using (var batch = new ValidationBatchFixture(chain)) batch.Confirm(chain, new[] { tx }, new[] { founder.CreateValidatorStake(founder.OwnedOneTimeAddresses.First(), Blockchain.WalletCreationReward) });
    }
    public static void Main(string[] args) {
        var dir = args[1]; var chain = BridgeNative.LoadChain(Path.Combine(dir, "chain.json"));
        var config = BridgeNative.Read<NativeConfig>(Path.Combine(dir, "asset-config.json"));
        var adminKeys = BridgeNative.Read<string[]>(Path.Combine(dir, "admin-keys.local.json"));
        if (args[0] == "confirm-first") {
            var txs = BridgeNative.Read<Transaction[]>(Path.Combine(dir, "issuance.json"));
            var registration = txs[0];
            Check(txs.Length == 2 && registration.Token.Supply == 0 && NativeBridge.IsRegistration(registration), "zero-supply registration precedes first mint");
            var wrong = Copy(registration); wrong.Token.Supply = 1; Sign(wrong, adminKeys); Reject(() => chain.ValidatePendingTransactions(new[] { wrong }));
            wrong = Copy(registration); wrong.Token.Policy.BridgeBinding = NativeBridge.Binding(config.chainId, "0x" + new string('f', 40), config.bridge); Sign(wrong, adminKeys); Reject(() => chain.ValidatePendingTransactions(new[] { wrong }));
            wrong = Copy(registration); wrong.Token.Policy.Administrators.Reverse(); Sign(wrong, adminKeys); Reject(() => chain.ValidatePendingTransactions(new[] { wrong }));
            wrong = Copy(registration); wrong.TokenAuthorizations.RemoveAt(1); Sign(wrong, adminKeys); Reject(() => chain.ValidatePendingTransactions(new[] { wrong }));
            var primaryAsset = chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Token.Policy.BridgeBinding.Split(':')[1];
            wrong = Copy(registration); wrong.Token.Id = NativeBridge.RepresentationId(config.chainId, primaryAsset); wrong.TokenCommand.TokenId = wrong.Token.Id;
            wrong.Token.Name = "BNB " + primaryAsset; wrong.Token.Policy.BridgeBinding = NativeBridge.Binding(config.chainId, primaryAsset, config.bridge); Sign(wrong, adminKeys);
            Reject(() => chain.ValidatePendingTransactions(new[] { wrong }));
            Confirm(dir, chain, registration);
            Check(chain.GetTokenRuntime(config.nativeToken).Supply == 0 && chain.GetTokens().Any(t => t.Id == config.nativeToken), "registration alone issues nothing and appears in catalogue");
            Reject(() => chain.ValidatePendingTransactions(new[] { registration }));
            Confirm(dir, chain, txs[1]);
            Check(chain.GetTokenRuntime(config.nativeToken).Supply == 1250000 && chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Supply == 0, "six decimal asset mints exact amount without pUSDT credit");
            Check(chain.GetTokenBalances(new[] { File.ReadAllText(Path.Combine(dir, "holder.txt")) }).Any(t => t.Id == config.nativeToken), "balance listing includes bridge representation");
            Check(Blockchain.GetRewardedBlockCount(chain.Blocks) == 0 && chain.IsValid(), "registration and mint do not alter POVIX emission");
        } else if (args[0] == "burn") {
            var keys = BridgeNative.Read<string[]>(Path.Combine(dir, "holder-keys.local.json"));
            var tx = BridgeNative.PrepareOperation(chain, config, new ReleaseRequest { operation = "burn", amount = "250000", fee = 0 }, keys);
            Confirm(dir, chain, tx);
            for (var i = 0; i < 11; i++) Confirm(dir, chain, chain.CreateWalletCreationTransaction(BridgeNative.Hash("multi-confirm-" + chain.Blocks.Count), new Transaction[0]));
            var binding = BridgeNative.SignBinding(chain, new Binding { transactionId = tx.Id, outputIndex = 0, chainId = config.chainId, bridge = config.bridge, recipient = args[2] }, keys);
            var evidence = BridgeNative.VerifyBurn(chain, config, binding); Check(evidence.amount == "250000", "six decimal burn exact");
            Check(chain.GetTokenRuntime(config.nativeToken).Supply == 1000000, "burn reduces only this representation");
            Write(Path.Combine(dir, "multi-evidence.json"), evidence);
        }
        Write(Path.Combine(dir, "chain.json"), chain.Blocks.ToArray());
        var replay = BridgeNative.LoadChain(Path.Combine(dir, "chain.json")); Check(replay.IsValid(), "full chain replay");
        Console.WriteLine(checks + " multi-asset native checks passed");
    }
}
