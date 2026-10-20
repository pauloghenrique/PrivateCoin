using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using PrivateCoin.Core;
using Povix.Bridge;

internal static class BridgeNativeRegression {
    static readonly Transaction[] Empty = new Transaction[0]; static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static void Reject(Action action, string message) { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception("Accepted " + message); }
    static void Write<T>(string file, T value) { using (var stream = File.Create(file)) new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); }
    static T Copy<T>(T value) { using (var stream = new MemoryStream()) { var serializer = new DataContractJsonSerializer(typeof(T)); serializer.WriteObject(stream, value); stream.Position = 0; return (T)serializer.ReadObject(stream); } }
    static void Resign(Transaction tx, string[] privateKeys, bool foreignNetwork = false) {
        var keys = new Dictionary<string, RSACryptoServiceProvider>();
        try {
            foreach (string xml in privateKeys) { var rsa = new RSACryptoServiceProvider(); rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); keys[BridgeNative.Hash(rsa.ToXmlString(false))] = rsa; }
            string text = (string)typeof(Transaction).GetMethod("SigningPayload", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tx, null);
            byte[] payload = Encoding.UTF8.GetBytes(foreignNetwork ? text.Replace(Blockchain.NetworkId, "povix-other-network") : text);
            foreach (var proof in tx.TokenAuthorizations) proof.Signature = Convert.ToBase64String(keys[BridgeNative.Hash(proof.PublicKey)].SignData(payload, "SHA256"));
            tx.Id = LegacyConsensusFixture.Id(tx);
        } finally { foreach (var key in keys.Values) key.Dispose(); }
    }
    static void Confirm(string directory, Blockchain chain, Transaction tx) {
        if (Blockchain.IsSelfValidatedOperation(tx) && chain.CanCreateUnsignedBlock(new[] { tx })) { chain.AddBlock(new[] { tx }); return; }
        using (var founder = Wallet.FromPrivateKeys(BridgeNative.Read<string[]>(Path.Combine(directory, "founder.local.json"))))
        using (var batch = new ValidationBatchFixture(chain)) batch.Confirm(chain, new[] { tx }, new[] { founder.CreateValidatorStake(founder.OwnedOneTimeAddresses.First(), Blockchain.WalletCreationReward) });
    }
    static void Advance(Blockchain chain, string directory) {
        for (int i = 0; i < 11; i++) Confirm(directory, chain, chain.CreateWalletCreationTransaction(BridgeNative.Hash("usdt-confirmation-" + chain.Blocks.Count), Empty));
    }
    static void Setup(string directory) {
        Directory.CreateDirectory(directory); var wallets = Enumerable.Range(0, 5).Select(i => new Wallet()).ToArray();
        try {
            var addresses = wallets.Select(w => w.CreateReceiveAddress()).ToArray(); var chain = new Blockchain();
            var create = chain.CreateWalletCreationTransaction(addresses[4], Empty);
            var forged = Copy(create); forged.Outputs[1].Amount = 1; forged.Id = LegacyConsensusFixture.Id(forged);
            Reject(() => chain.ValidatePendingTransactions(new[] { forged }), "bootstrap cannot mint pUSDT without backing");
            forged = Copy(create); forged.Outputs.Add(new TransactionOutput { AssetId = new string('e', 64), Amount = 100000000, OneTimeAddress = addresses[4] }); forged.Id = LegacyConsensusFixture.Id(forged);
            Reject(() => chain.ValidatePendingTransactions(new[] { forged }), "bootstrap cannot restore old free stablecoin allocation");
            chain.AddBlock(new[] { create });
            Check(create.Outputs.Count == 3 && create.Outputs[2].Amount == 600 * Blockchain.OneCoin, "first wallet retains 600 locked POVIX");
            Check(create.Outputs[1].AssetId == InitialLaunch.PusdtTokenId && create.Outputs[1].Amount == 0, "automatic pool quote is pUSDT with zero reserve");
            Check(chain.GetTokens().Count == 1 && chain.GetTokens().Single().Supply == 0 && chain.GetTokens().Single().Symbol == "pUSDT", "only protocol pUSDT exists, with zero issuance");
            Check(chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Issued == 0 && !chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Token.Policy.Mintable, "unconfigured bridge cannot issue");
            Write(Path.Combine(directory, "founder.local.json"), wallets[4].ExportPrivateKeys().ToArray());
            var registration = chain.CreateWalletCreationTransaction(addresses[3], Empty);
            var freeReward = Copy(registration); freeReward.Outputs.Add(new TransactionOutput { AssetId = InitialLaunch.PusdtTokenId, Amount = 10000000000, OneTimeAddress = addresses[3] }); freeReward.Id = LegacyConsensusFixture.Id(freeReward);
            Reject(() => chain.ValidatePendingTransactions(new[] { freeReward }), "registration cannot grant a free stablecoin balance");
            Confirm(directory, chain, registration);
            Check(registration.Outputs.Count == 1 && registration.Outputs[0].Amount == 0 && chain.GetTokenBalance(new[] { addresses[3] }, InitialLaunch.PusdtTokenId) == 0, "later wallets receive no USDT, USDC or pUSDT");
            Write(Path.Combine(directory, "chain.json"), chain.Blocks.ToArray());
            Write(Path.Combine(directory, "admin-keys.local.json"), wallets.Take(3).SelectMany(w => w.ExportPrivateKeys()).ToArray());
            Write(Path.Combine(directory, "holder-keys.local.json"), wallets[3].ExportPrivateKeys().ToArray());
            Write(Path.Combine(directory, "native-config.json"), new NativeConfig { nativeNetwork = Blockchain.NetworkId, nativeToken = InitialLaunch.PusdtTokenId,
                nativeAdministrators = addresses.Take(3).ToArray(), nativeThreshold = 2, nativeConfirmations = 12, chainId = 31337, bridge = "0x" + new string('a', 40) });
            File.WriteAllText(Path.Combine(directory, "holder.txt"), addresses[3]); Console.WriteLine(checks + " native bootstrap checks passed");
        } finally { foreach (var wallet in wallets) wallet.Dispose(); }
    }
    public static void Main(string[] args) {
        string mode = args[0], directory = args[1]; if (mode == "setup") { Setup(directory); return; }
        var chain = BridgeNative.LoadChain(Path.Combine(directory, "chain.json")); var config = BridgeNative.Read<NativeConfig>(Path.Combine(directory, "config.json"));
        if (mode == "configure") {
            Reject(() => BridgeNative.Configure(chain, config, BridgeNative.Read<string[]>(Path.Combine(directory, "holder-keys.local.json"))), "only first wallet can bind the bridge");
            var tx = BridgeNative.Configure(chain, config, BridgeNative.Read<string[]>(Path.Combine(directory, "founder.local.json")));
            var weak = Copy(tx); weak.TokenCommand.BridgePolicy.RequiredSignatures = 1; weak.Id = LegacyConsensusFixture.Id(weak);
            Reject(() => chain.ValidatePendingTransactions(new[] { weak }), "configuration cannot choose a one-key federation");
            Confirm(directory, chain, tx);
            Check(Blockchain.GetValidatorBlockReward(1, new[] { tx }) == 0 && Blockchain.GetRewardedBlockCount(chain.Blocks) == 0, "bridge bootstrap does not alter validator reward budget");
            Reject(() => BridgeNative.Configure(chain, config, BridgeNative.Read<string[]>(Path.Combine(directory, "founder.local.json"))), "bridge reconfiguration");
            Check(chain.IsValid() && chain.GetTokenRuntime(config.nativeToken).Supply == 0 && chain.GetTokenRuntime(config.nativeToken).Token.Policy.BridgeBinding == NativeBridge.Binding(config.chainId, config.asset, config.bridge), "configuration binds vault without issuing tokens");
            Write(Path.Combine(directory, "chain.json"), chain.Blocks.ToArray()); return;
        }
        if (mode == "confirm") { Confirm(directory, chain, BridgeNative.Read<Transaction>(args[2])); Advance(chain, directory); Write(Path.Combine(directory, "chain.json"), chain.Blocks.ToArray()); return; }
        var holderKeys = BridgeNative.Read<string[]>(Path.Combine(directory, "holder-keys.local.json"));
        var burn = BridgeNative.PrepareOperation(chain, config, new ReleaseRequest { operation = "burn", amount = "50000000", fee = 0 }, holderKeys); Confirm(directory, chain, burn);
        var binding = BridgeNative.SignBinding(chain, new Binding { transactionId = burn.Id, outputIndex = 0, chainId = 31337, bridge = config.bridge, recipient = args[2] }, holderKeys);
        Reject(() => BridgeNative.VerifyBurn(chain, config, binding), "insufficient burn confirmations"); Advance(chain, directory);
        var evidence = BridgeNative.VerifyBurn(chain, config, binding); Check(evidence.amount == "50000000" && evidence.confirmations == 12, "exact burn amount and confirmations");
        string original = binding.recipient; binding.recipient = "0x" + new string('e', 40); Reject(() => BridgeNative.VerifyBurn(chain, config, binding), "recipient hijacking"); binding.recipient = original;
        string vault = binding.bridge; binding.bridge = "0x" + new string('d', 40); Reject(() => BridgeNative.VerifyBurn(chain, config, binding), "wrong bridge"); binding.bridge = vault;
        var signatures = binding.signatures; binding.signatures = null; Reject(() => BridgeNative.VerifyBurn(chain, config, binding), "missing holder signatures"); binding.signatures = signatures;
        int quorum = config.nativeThreshold; config.nativeThreshold = 1; Reject(() => BridgeNative.VerifyBurn(chain, config, binding), "unsafe native quorum"); config.nativeThreshold = quorum;
        Write(Path.Combine(directory, "chain.json"), chain.Blocks.ToArray()); Write(Path.Combine(directory, "binding.json"), binding);
        Check(chain.GetTokenRuntime(config.nativeToken).Supply == 250000000 && chain.IsValid(), "burn preserves externally backed supply");
        var cloned = BridgeNative.LoadChain(Path.Combine(directory, "chain.json")); Check(BridgeNative.VerifyBurn(cloned, config, binding).depositId == evidence.depositId, "snapshot retains burn id");
        var before = new Blockchain(chain.Blocks.Where(b => b.Height < chain.Blocks.Single(block => block.Transactions.Any(tx => tx.Id == burn.Id)).Height)); Reject(() => BridgeNative.VerifyBurn(before, config, binding), "orphaned burn");
        var mint = BridgeNative.PrepareOperation(chain, config, new ReleaseRequest { operation = "mint", amount = "1", recipient = File.ReadAllText(Path.Combine(directory, "holder.txt")), fee = 0 }, BridgeNative.Read<string[]>(Path.Combine(directory, "admin-keys.local.json")));
        var adminKeys = BridgeNative.Read<string[]>(Path.Combine(directory, "admin-keys.local.json"));
        var foreign = Copy(mint); Resign(foreign, adminKeys, true);
        Reject(() => chain.ValidatePendingTransactions(new[] { foreign }), "quorum signatures from another native network cannot replay");
        var altered = Copy(mint); altered.Outputs[0].Amount++; Resign(altered, adminKeys);
        Reject(() => chain.ValidatePendingTransactions(new[] { altered }), "signed mint cannot exceed its declared amount");
        var duplicate = Copy(mint); duplicate.TokenAuthorizations[1] = Copy(duplicate.TokenAuthorizations[0]); Resign(duplicate, adminKeys);
        Reject(() => chain.ValidatePendingTransactions(new[] { duplicate }), "valid duplicate administrator signatures cannot meet quorum");
        var missing = Copy(mint); missing.TokenAuthorizations.Clear(); missing.Id = LegacyConsensusFixture.Id(missing);
        Reject(() => chain.ValidatePendingTransactions(new[] { missing }), "unsigned free mint is rejected by consensus");
        Check(mint.Inputs.Count == 0 && mint.TokenAuthorizations.Count == 2 && mint.Fee == 0, "native issuance requires quorum and no free POVIX startup balance");
        Reject(() => BridgeNative.PrepareOperation(chain, config, new ReleaseRequest { operation = "mint", amount = "1", recipient = File.ReadAllText(Path.Combine(directory, "holder.txt")), fee = 0 }, BridgeNative.Read<string[]>(Path.Combine(directory, "admin-keys.local.json")).Take(1).ToArray()), "single-admin mint");
        using (var holder = Wallet.FromPrivateKeys(holderKeys)) {
            string identity = holder.OwnedOneTimeAddresses.First(); var purchase = holder.CreateInitialLaunchPurchase(chain, Empty, identity, identity, 1);
            chain.AddBlock(new[] { purchase }); Check(purchase.Outputs[1].AssetId == config.nativeToken && purchase.Outputs[1].Amount == 100000000 && purchase.Outputs[2].Amount == 600000000 - 1, "one pUSDT buys six gross locked POVIX minus fee");
            Check(chain.GetInitialLaunchStatus().Purchases == 1 && chain.GetInitialLaunchStatus().Remaining == InitialLaunch.ReserveSupply - InitialLaunch.PurchaseAmount && chain.IsValid(), "launch amounts and one-purchase rules remain valid");
        }
        Console.WriteLine(checks + " native bridge and launch checks passed");
    }
}
