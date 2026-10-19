using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PrivateCoin.Core;
using PrivateCoin.Desktop;

internal static class PrivateCoinInitialLaunchRegression
{
    private static int checks;
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException || e.InnerException is ArgumentException) { Check(true, label); return; }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static T Copy<T>(T value)
    {
        using (var stream = new MemoryStream()) { var serializer = new DataContractJsonSerializer(typeof(T)); serializer.WriteObject(stream, value); stream.Position = 0; return (T)serializer.ReadObject(stream); }
    }
    private static string Hash(string value) { using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2"))); }
    private static string Public(Wallet wallet, string address) { foreach (var xml in wallet.ExportPrivateKeys()) using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); if (Hash(rsa.ToXmlString(false)) == address) return rsa.ToXmlString(false); } throw new Exception("Key missing"); }
    private static string Sign(Wallet wallet, string address, byte[] payload) { foreach (var xml in wallet.ExportPrivateKeys()) using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); if (Hash(rsa.ToXmlString(false)) == address) return Convert.ToBase64String(rsa.SignData(payload, "SHA256")); } throw new Exception("Key missing"); }
    private static string[] Keys(Wallet wallet) => wallet.OwnedOneTimeAddresses.Select(address => Public(wallet, address)).ToArray();
    private static Transaction Complete(UnsignedTokenExchange draft, Wallet wallet, string identity = null) => draft.Complete(draft.Sources.Take(draft.Kind == TransactionKind.LiquidityClose ? draft.Sources.Count : draft.UserInputCount).Select(item => Sign(wallet, item.Output.OneTimeAddress, draft.SigningPayload)), identity == null ? null : Sign(wallet, identity, draft.SigningPayload));
    private static void Resign(Transaction tx, Wallet wallet, int userCount)
    {
        var payload = Encoding.UTF8.GetBytes((string)typeof(Transaction).GetMethod("SigningPayload", Hidden).Invoke(tx, null));
        for (int i = 0; i < userCount; i++) tx.Inputs[i].Signature = Sign(wallet, Hash(tx.Inputs[i].PublicKey), payload);
        if (tx.InitialLaunchIdentity != null) tx.InitialLaunchIdentity.Signature = Sign(wallet, Hash(tx.InitialLaunchIdentity.PublicKey), payload);
        tx.Id = LegacyConsensusFixture.Id(tx);
    }
    private static Dictionary<string, UnspentOutput> Utxo(Blockchain chain) => (Dictionary<string, UnspentOutput>)typeof(Blockchain).GetMethod("BuildUtxo", Hidden).Invoke(chain, null);
    private static void Set(object value, string property, object next) => value.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetSetMethod(true).Invoke(value, new[] { next });
    private static void Apply(Transaction tx, Dictionary<string, UnspentOutput> utxo, int version = Blockchain.ConsensusVersion) => typeof(Blockchain).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { tx, utxo, false, false, version, 1000 });
    private static Dictionary<string, UnspentOutput> CloneUtxo(Dictionary<string, UnspentOutput> source)
    {
        var copy = new Dictionary<string, UnspentOutput>();
        foreach (var item in source) { var value = (UnspentOutput)typeof(object).GetMethod("MemberwiseClone", Hidden).Invoke(item.Value, null); Set(value, "Output", Copy(item.Value.Output)); copy.Add(item.Key, value); }
        return copy;
    }
    public static void Main(string[] args)
    {
        using (var provider = new Wallet()) using (var buyer = new Wallet()) using (var last = new Wallet()) using (var v1 = new Wallet()) using (var v2 = new Wallet())
        {
            string p = provider.CreateReceiveAddress(), b = buyer.CreateReceiveAddress(), l = last.CreateReceiveAddress(), a1 = v1.CreateReceiveAddress(), a2 = v2.CreateReceiveAddress();
            var chain = new Blockchain();
            string q = Hash(InitialLaunch.ProviderPublicKey);
            Check(!chain.GetInitialLaunchStatus().Created && chain.GetTokens().Count == 0, "no pool or funding tokens exist before the first confirmed wallet");
            var creation = chain.CreateWalletCreationTransaction(p, new Transaction[0]);
            Check(creation.Kind == TransactionKind.InitialLaunchCreate && !chain.GetInitialLaunchStatus().Created && creation.Inputs.Count == 0 && creation.Fee == 0,
                "wallet 1 prepares the automatic bootstrap without approval fees or a manual pool action");
            var bad = Copy(creation); bad.Outputs[2].Amount++; bad.Id = LegacyConsensusFixture.Id(bad);
            Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "cannot overissue the local funding tokens");
            bad = Copy(creation); bad.Outputs[1].AssetId = Hash("fake-USDC"); bad.Id = LegacyConsensusFixture.Id(bad);
            Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "same-symbol impostors cannot replace the protocol funding token");
            var skipped = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                Outputs = new List<TransactionOutput> { new TransactionOutput { Amount = Blockchain.WalletCreationReward, OneTimeAddress = p } } };
            skipped.Id = LegacyConsensusFixture.Id(skipped);
            Reject(() => chain.ValidatePendingTransactions(new[] { skipped }), "wallet 1 cannot omit the automatic pool creation");
            chain.AddBlock(new[] { creation });
            Check(chain.GetInitialLaunchStatus().Created && chain.GetInitialLaunchStatus().FundingAssetId == InitialLaunch.UsdcTokenId,
                "confirmation of wallet 1 automatically creates the native POVIX/local-USDC pool");
            var empty = chain.CreateWalletCreationTransaction(b, new Transaction[0]);
            Check(empty.Outputs[0].Amount == 0 && chain.GetBalance(new[] { p }) == 600 * Blockchain.OneCoin, "only the first wallet receives 600 POVIX; the second receives zero");
            var founderStake = provider.CreateValidatorStake(p, Blockchain.WalletCreationReward);
            chain.AddProofOfStakeBlock(new[] { empty }, new[] { founderStake });
            foreach (var address in new[] { l, a1, a2 })
            {
                var registration = chain.CreateWalletCreationTransaction(address, new Transaction[0]);
                Check(registration.Outputs[0].Amount == 0, "later wallets register without issuing additional POVIX");
                chain.AddProofOfStakeBlock(new[] { registration }, new[] { founderStake });
            }
            Reject(() => chain.CreateWalletCreationTransaction(p, new Transaction[0]), "the first wallet cannot register again for another reward");
            Check(creation.Outputs[4].InitialLaunchLocked && !chain.GetSpendableOutputs(new[] { p }, new Transaction[0]).Any(item => item.TransactionId == creation.Id) &&
                chain.GetInitialLaunchLockedBalance(new[] { p }) == Blockchain.WalletCreationReward,
                "first wallet receives 600 locked POVIX with no freely spendable initial reward");
            Check(chain.GetActiveValidators().Single().ValidatorId == p && chain.GetActiveValidators().Single().LockedAmount == Blockchain.WalletCreationReward,
                "first wallet automatically has 600 POVIX of collateral without publishing a separate staking transaction");
            var missingLock = Copy(creation); missingLock.Outputs[4].InitialLaunchLocked = false; missingLock.Id = LegacyConsensusFixture.Id(missingLock);
            Reject(() => Apply(missingLock, new Dictionary<string, UnspentOutput>()), "first wallet cannot omit its 600-POVIX lock");
            Reject(() => Apply(creation, new Dictionary<string, UnspentOutput>(), 26), "consensus v26 cannot accept the new first-wallet lock");
            var founderSpend = new Transaction { Kind = TransactionKind.Transfer, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = 1,
                Inputs = new List<TransactionInput> { new TransactionInput { TransactionId = creation.Id, OutputIndex = 4, PublicKey = Public(provider, p) } },
                Outputs = new List<TransactionOutput> { new TransactionOutput { Amount = Blockchain.WalletCreationReward - 1, OneTimeAddress = p } } };
            Resign(founderSpend, provider, 1);
            Reject(() => chain.ValidatePendingTransactions(new[] { founderSpend }), "first 600 POVIX cannot transfer before the pool target");
            var founderUnlock = Copy(founderSpend); founderUnlock.Kind = TransactionKind.StakeUnlock; founderUnlock.Fee = 0;
            founderUnlock.Outputs[0].Amount = Blockchain.WalletCreationReward; Resign(founderUnlock, provider, 1);
            Reject(() => chain.ValidatePendingTransactions(new[] { founderUnlock }), "first wallet cannot use StakeUnlock to bypass the pool target");
            chain.AddBlock(new[] { v1.CreateInitialLaunchPurchase(chain, new Transaction[0], a1, a1, 1) });
            var secondLaunch = v2.CreateInitialLaunchPurchase(chain, new Transaction[0], a2, a2, 1);
            var firstBuyerStake = v1.CreateValidatorStake(a1, InitialLaunch.PurchaseAmount - 1);
            secondLaunch.TransactionApproval = chain.CreateTransactionApproval(secondLaunch, new[] { secondLaunch }, new[] { firstBuyerStake });
            chain.AddProofOfStakeBlock(new[] { secondLaunch }, new[] { firstBuyerStake });
            var validators = new[] { provider.CreateValidatorStake(p, Blockchain.WalletCreationReward),
                v1.CreateValidatorStake(a1, InitialLaunch.PurchaseAmount - 1), v2.CreateValidatorStake(a2, InitialLaunch.PurchaseAmount - 1) };
            Block rewardBlock = null;
            for (int index = 0; index < 8; index++)
            {
                var bootstrap = chain.CreateWalletCreationTransaction(Hash("launch-reward-test-" + index), new Transaction[0]);
                rewardBlock = chain.AddProofOfStakeBlock(new[] { bootstrap }, validators);
            }
            Check(rewardBlock.Validators.Any(item => item.ValidatorId == p) &&
                chain.GetSpendableBalance(new[] { p }, new Transaction[0]) > 0 &&
                chain.GetSpendableBalance(new[] { a1 }, new Transaction[0]) > 0 &&
                chain.GetSpendableBalance(new[] { a2 }, new Transaction[0]) > 0,
                "first-wallet and purchased locked collateral both earn scheduled creator/confirmer rewards");
            var founderApproved = v1.CreateTransaction(chain, new Transaction[0], l, 1, 1);
            founderApproved.TransactionApproval = chain.CreateTransactionApproval(founderApproved, new[] { founderApproved }, new[] { validators[0] });
            Check(founderApproved.TransactionApproval != null && founderApproved.TransactionApproval.CollateralTransactionId == creation.Id &&
                founderApproved.TransactionApproval.CollateralOutputIndex == 4, "first-wallet collateral can approve unrelated transactions using its matching public key");
            var stolenApproval = Copy(founderApproved);
            stolenApproval.TransactionApproval.Proof.PublicKey = Public(v2, a2);
            stolenApproval.TransactionApproval.Proof.ValidatorId = a2;
            var approvalPayload = (string)typeof(Blockchain).GetMethod("CreateTransactionApprovalPayload", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { stolenApproval, stolenApproval.TransactionApproval });
            stolenApproval.TransactionApproval.Proof.Signature = Sign(v2, a2, Encoding.UTF8.GetBytes(approvalPayload));
            Check(!chain.HasValidTransactionApproval(stolenApproval, new[] { stolenApproval }),
                "a valid signature from another key cannot claim the first wallet's collateral");
            var founderReceiving = v1.CreateTransaction(chain, new Transaction[0], p, 1, 1);
            Check(chain.CreateTransactionApproval(founderReceiving, new[] { founderReceiving }, new[] { validators[0] }) == null,
                "first-wallet collateral cannot approve a transaction whose recipient is the first wallet");
            chain.AddProofOfStakeBlock(new[] { founderApproved }, validators);
            Check(chain.GetInitialLaunchLockedBalance(new[] { p }) == Blockchain.WalletCreationReward &&
                new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(), "rewards never consume the first 600 POVIX and the revealed validator key replays safely");
            using (var batch = new ValidationBatchFixture(chain))
            {
                foreach (var address in new[] { l })
                    batch.Confirm(chain, new[] { provider.CreateTransaction(chain, new Transaction[0], address, 6 * Blockchain.OneCoin, 1) }, validators);
                Check(chain.GetInitialLaunchStatus().Created && chain.GetInitialLaunchStatus().Remaining == InitialLaunch.ReserveSupply - 2 * InitialLaunch.PurchaseAmount, "initial reserve is reduced only by the two confirmed bootstrap purchases");
                Check(chain.GetTokens().Count == 2 && InitialLaunch.FundingTokens().All(token => chain.GetTokenRuntime(token.Id).Supply == (token.Id == InitialLaunch.UsdcTokenId ? 13 * InitialLaunch.WalletUsdcReward : InitialLaunch.FundingTokenSupply) && chain.GetTokenBalance(new[] { p }, token.Id) == (token.Id == InitialLaunch.UsdcTokenId ? InitialLaunch.WalletUsdcReward : token.Supply)), "USDC distributes 100 per registered identity while USDT issues one million");
                Check(chain.GetTokenBalances(new[] { p }).Count == 2, "local funding tokens appear in the wallet token catalogue");
                var duplicate = Copy(creation); duplicate.TimestampUtcTicks++; duplicate.Id = LegacyConsensusFixture.Id(duplicate);
                Reject(() => chain.ValidatePendingTransactions(new[] { duplicate }), "a second launch cannot mint more POVIX or funding tokens");
                foreach (var address in new[] { b, l }) batch.Confirm(chain, new[] { provider.CreateTokenTransferTransaction(chain, new Transaction[0], InitialLaunch.UsdcTokenId, address, 10 * InitialLaunch.FundingUnit, 1) }, validators);
                batch.Confirm(chain, new[] { provider.CreateTokenTransferTransaction(chain, new Transaction[0], InitialLaunch.UsdcTokenId, l, 10 * InitialLaunch.FundingUnit, 1) }, validators);
                batch.Confirm(chain, new[] { provider.CreateTransaction(chain, new Transaction[0], l, Blockchain.OneCoin, 1) }, validators);
                var status = chain.GetInitialLaunchStatus();
                var reserves = chain.GetUnspentOutputs(new[] { q }).Where(item => item.TransactionId == status.ReserveTransactionId && item.OutputIndex < 2).OrderBy(item => item.OutputIndex).ToArray();
                Check(reserves.Length == 2 && reserves[1].Output.Amount == 2 * InitialLaunch.FundingUnit && reserves.All(item => item.InitialLaunchCollecting), "funding reserve tracks the bootstrap purchases and stays paired and locked");
                Check(chain.GetBalance(new[] { b }) == 0, "launch buyer starts with zero POVIX");
                Check(WalletBalanceSummary.Read(chain, buyer, new Transaction[0]).LaunchLockText == string.Empty,
                    "Desktop has no launch lock notice before receiving a purchase");
                var purchase = buyer.CreateInitialLaunchPurchase(chain, new Transaction[0], b, b, 1);
                Check(purchase.Outputs[2].Amount == InitialLaunch.PurchaseAmount - purchase.Fee && purchase.Inputs.Count == 3,
                    "purchase funds only USDC and deducts fee from the six-POVIX receipt");
                bad = Copy(purchase); bad.Outputs[2].Amount = InitialLaunch.PurchaseAmount; Resign(bad, buyer, bad.Inputs.Count - 2);
                Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "purchase cannot omit the fee deduction");
                bad = Copy(purchase); bad.Outputs[1].Amount--; bad.Outputs.Last(item => item.AssetId == InitialLaunch.UsdcTokenId).Amount++; Resign(bad, buyer, bad.Inputs.Count - 2);
                Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "purchase must deposit exactly one local funding unit");
                bad = Copy(purchase); bad.Outputs[2].InitialLaunchLocked = false; Resign(bad, buyer, bad.Inputs.Count - 2);
                Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "purchased POVIX cannot omit their lock");
                bad = Copy(purchase); bad.Outputs[2].OneTimeAddress = l; Resign(bad, buyer, bad.Inputs.Count - 2);
                Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "buyer cannot redirect the purchase to another identity");
                bad = Copy(purchase); bad.InitialLaunchIdentity.Signature = Sign(provider, p, Encoding.UTF8.GetBytes((string)typeof(Transaction).GetMethod("SigningPayload", Hidden).Invoke(bad, null))); bad.Id = LegacyConsensusFixture.Id(bad);
                Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "funding signatures cannot substitute for wallet identity authorization");
                Check(!Blockchain.IsSelfValidatedOperation(purchase) && Blockchain.RequiresLockedTokenApproval(purchase), "later launch purchase requires a confirmed buyer approval");
                long nativeBefore = Utxo(chain).Values.Where(item => item.Output.AssetId == null).Sum(item => item.Output.Amount);
                var prePurchaseBlocks = Copy(chain.Blocks.ToArray());
                purchase.TransactionApproval = chain.CreateTransactionApproval(purchase, new[] { purchase }, new[] { firstBuyerStake });
                int purchaseHeight = chain.Blocks.Count;
                chain.AddProofOfStakeBlock(new[] { purchase }, new[] { firstBuyerStake });
                Check(Utxo(chain).Values.Where(item => item.Output.AssetId == null).Sum(item => item.Output.Amount) - nativeBefore == ProofOfStake.GetBlockReward(purchaseHeight), "launch fee is conserved and scheduled block reward is issued");
                Check(purchase.TransactionApproval != null && new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(), "single-holder approved launch block confirms and replays");
                Check(chain.GetInitialLaunchStatus().Remaining == InitialLaunch.ReserveSupply - 3 * InitialLaunch.PurchaseAmount && chain.GetInitialLaunchLockedBalance(new[] { b }) == InitialLaunch.PurchaseAmount - purchase.Fee, "first buyer owns six locked POVIX and sale reserve decreases by six");
                Check(!chain.GetSpendableOutputs(new[] { b }, new Transaction[0]).Any(item => item.TransactionId == purchase.Id && item.OutputIndex == 2), "locked purchase is excluded from spendable balances");
                Check(WalletBalanceSummary.Read(chain, buyer, new Transaction[0]).LaunchLocked == InitialLaunch.PurchaseAmount - purchase.Fee, "desktop displays locked launch balance separately");
                var desktopBalance = WalletBalanceSummary.Read(chain, buyer, new Transaction[0]);
                Check(desktopBalance.LaunchLockText.Contains("Bloqueado no lançamento") &&
                    desktopBalance.LaunchLockDescription.Contains("validação e criação de blocos") &&
                    desktopBalance.LaunchLockDescription.Contains("atingir a meta"),
                    "Desktop announces the received balance as already locked validator collateral until the pool target");
                var restored = new Blockchain(Copy(chain.Blocks.ToArray()));
                Check(restored.IsValid() && restored.GetInitialLaunchLockedBalance(new[] { b }) == InitialLaunch.PurchaseAmount - purchase.Fee, "serialization and full-chain replay preserve escrow, supply and wallet locks");
                Reject(() => chain.ValidatePendingTransactions(new[] { buyer.CreateInitialLaunchPurchase(chain, new Transaction[0], b, b, 1) }), "registered wallet cannot buy twice");
                var utxo = Utxo(chain);
                var locked = utxo[purchase.Id + ":2"];
                var spend = new Transaction { Kind = TransactionKind.Transfer, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = 1,
                    Inputs = new List<TransactionInput> { new TransactionInput { TransactionId = purchase.Id, OutputIndex = 2, PublicKey = Public(buyer, b) } },
                    Outputs = new List<TransactionOutput> { new TransactionOutput { Amount = locked.Output.Amount - 1, OneTimeAddress = b } } };
                Resign(spend, buyer, 1);
                Reject(() => chain.ValidatePendingTransactions(new[] { spend }), "valid ownership signature cannot spend locked purchased POVIX");
                reserves = chain.GetUnspentOutputs(new[] { q }).Where(item => item.TransactionId == purchase.Id && item.OutputIndex < 2).OrderBy(item => item.OutputIndex).ToArray();
                var swap = Complete(TokenExchange.PrepareSwap(chain, new Transaction[0], Keys(last), reserves[0], reserves[1], InitialLaunch.ProviderPublicKey, 1, 1, 1, l, 1), last);
                Reject(() => chain.ValidatePendingTransactions(new[] { swap }), "pool cannot negotiate before the launch target");
                var buyerStake = chain.GetActiveValidators().Single(item => item.ValidatorId == b);
                Check(buyerStake.LockedAmount == purchase.Outputs[2].Amount && buyerStake.RewardAddress == b,
                    "confirmed purchase automatically becomes validator collateral without a staking transaction");
                var localBuyerStake = buyer.CreateValidatorStake(b, buyerStake.LockedAmount);
                var allValidators = validators.Concat(new[] { localBuyerStake }).ToArray();
                var unrelated = provider.CreateTransaction(chain, new Transaction[0], l, Blockchain.OneCoin, 1);
                unrelated.TransactionApproval = chain.CreateTransactionApproval(unrelated, new[] { unrelated }, new[] { localBuyerStake });
                Check(unrelated.TransactionApproval != null && unrelated.TransactionApproval.CollateralTransactionId == purchase.Id &&
                    unrelated.TransactionApproval.CollateralOutputIndex == 2, "locked purchased POVIX can approve unrelated transfers");
                var validatorBlock = chain.AddProofOfStakeBlock(new[] { unrelated }, allValidators);
                Check(validatorBlock.Validators.Any(item => item.ValidatorId == b) &&
                    new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(), "launch buyer signs and confirms a real block that replays correctly");
                Check(chain.GetInitialLaunchLockedBalance(new[] { b }) == purchase.Outputs[2].Amount,
                    "validation and block rewards never unlock or consume the purchased collateral");
                var prematureUnlock = Copy(spend); prematureUnlock.Kind = TransactionKind.StakeUnlock; prematureUnlock.Fee = 0;
                prematureUnlock.Outputs[0].Amount = locked.Output.Amount; Resign(prematureUnlock, buyer, 1);
                Reject(() => chain.ValidatePendingTransactions(new[] { prematureUnlock }), "stake unlock cannot bypass the launch target lock");
                var globalStakes = typeof(Blockchain).GetMethod("StakesFromUtxo", BindingFlags.Static | BindingFlags.NonPublic);
                Check(!((ValidatorStake[])globalStakes.Invoke(null, new object[] { Utxo(chain), 25 })).Any(item => item.ValidatorId == b),
                    "legacy consensus does not activate launch collateral");
                var mixed = CloneUtxo(Utxo(chain));
                var manual = mixed.Values.First(item => item.ValidatorPublicKey == Public(v1, a1));
                manual = (UnspentOutput)typeof(object).GetMethod("MemberwiseClone", Hidden).Invoke(manual, null);
                Set(manual, "Output", Copy(manual.Output));
                Set(manual, "TransactionKind", TransactionKind.StakeLock);
                Set(manual, "ValidatorPublicKey", Public(buyer, b)); Set(manual, "ValidatorRewardAddress", b);
                Set(manual, "ValidatorOwnedAddresses", new[] { b });
                mixed["test-manual-buyer-stake"] = manual;
                var merged = ((ValidatorStake[])globalStakes.Invoke(null, new object[] { mixed, Blockchain.ConsensusVersion })).Where(item => item.ValidatorId == b).ToArray();
                Check(merged.Length == 1 && merged[0].LockedAmount == manual.Output.Amount + purchase.Outputs[2].Amount,
                    "manual and launch collateral combine into one validator without duplicated identities");
                // Boundary fixture: exercise the actual consensus transition from purchase 29949,
                // without mining 29948 unrelated registrations and purchases in every test run.
                var boundary = CloneUtxo(utxo);
                var stateItem = boundary.Values.Single(item => item.TransactionId == "@initial-launch-state");
                var state = (InitialLaunchStatus)typeof(UnspentOutput).GetProperty("InitialLaunchState", Hidden).GetValue(stateItem, null);
                state = (InitialLaunchStatus)typeof(object).GetMethod("MemberwiseClone", Hidden).Invoke(state, null);
                Set(state, "Purchases", InitialLaunch.MaximumPurchases - 1); Set(stateItem, "InitialLaunchState", state);
                var ra = boundary[purchase.Id + ":0"]; var rb = boundary[purchase.Id + ":1"];
                ra.Output.Amount = InitialLaunch.PrincipalSupply + InitialLaunch.PurchaseAmount;
                rb.Output.Amount = (InitialLaunch.MaximumPurchases - 1L) * InitialLaunch.FundingUnit;
                var finalDraft = TokenExchange.PrepareInitialLaunchPurchase(chain, new Transaction[0], Keys(last), ra, rb, InitialLaunch.ProviderPublicKey, Public(last, l), l, 1);
                var final = Complete(finalDraft, last, l);
                bad = Copy(final); bad.PoolParticipation.LockedShares--; Resign(bad, last, bad.Inputs.Count - 2);
                Reject(() => Apply(bad, CloneUtxo(boundary)), "last buyer cannot reduce the principal lock");
                Reject(() => Apply(final, CloneUtxo(boundary), 27), "v27 blocks cannot accept the v28 launch purchase");
                Apply(final, boundary);
                Check(boundary[final.Id + ":0"].Output.Amount == InitialLaunch.PrincipalSupply && boundary[final.Id + ":1"].Output.Amount == InitialLaunch.MaximumPurchases * InitialLaunch.FundingUnit && !boundary[final.Id + ":0"].InitialLaunchCollecting, "purchase 29950 atomically opens exactly 179700 POVIX against 29950 funding units");
                Check(!((ValidatorStake[])globalStakes.Invoke(null, new object[] { boundary, Blockchain.ConsensusVersion })).Any(item => item.ValidatorId == b),
                    "target activation automatically releases launch collateral from the active validator set");
                Apply(spend, CloneUtxo(boundary)); Check(true, "earlier purchase becomes spendable exactly at activation");
                Apply(founderSpend, CloneUtxo(boundary));
                Check(!((ValidatorStake[])globalStakes.Invoke(null, new object[] { boundary, Blockchain.ConsensusVersion })).Any(item => item.ValidatorId == p),
                    "pool target releases the first-wallet 600 POVIX and removes their automatic validator guarantee");
                Reject(() => Apply(Copy(final), CloneUtxo(boundary)), "no purchase is accepted after activation");
                var envelope = typeof(InitialLaunch).GetMethod("ValidateEnvelope", BindingFlags.NonPublic | BindingFlags.Static);
                foreach (var kind in new[] { TransactionKind.LiquidityPool, TransactionKind.LaunchCreate, TransactionKind.LiquidityAdd })
                {
                    var gated = new Transaction { Kind = kind };
                    Reject(() => envelope.Invoke(null, new object[] { gated, new Dictionary<string, UnspentOutput>(), Blockchain.ConsensusVersion }), "pool access rejects " + kind + " before automatic pool exists");
                    Reject(() => envelope.Invoke(null, new object[] { gated, Utxo(chain), Blockchain.ConsensusVersion }), "pool access rejects " + kind + " during principal launch");
                    foreach (int announcedVersion in new[] { 21, 28 })
                        Reject(() => envelope.Invoke(null, new object[] { gated, Utxo(chain), announcedVersion }), "announcing v" + announcedVersion + " cannot bypass principal target for " + kind);
                    envelope.Invoke(null, new object[] { gated, boundary, Blockchain.ConsensusVersion });
                    Check(true, "principal target releases " + kind + " for every wallet");
                }
                var activeA = boundary[final.Id + ":0"]; var activeB = boundary[final.Id + ":1"];
                var closeFee = boundary.Values.First(item => item.OutputIndex >= 0 && provider.OwnedOneTimeAddresses.Contains(item.Output.OneTimeAddress) && item.Output.AssetId == null && !item.IsPoolReserve);
                var close = new Transaction { Kind = TransactionKind.LiquidityClose, Fee = 1, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                    Inputs = new[] { closeFee, activeA, activeB }.Select(item => new TransactionInput { TransactionId = item.TransactionId, OutputIndex = item.OutputIndex,
                        PublicKey = item.IsPoolReserve ? InitialLaunch.ProviderPublicKey : Public(provider, item.Output.OneTimeAddress) }).ToList(),
                    Outputs = new List<TransactionOutput> { Copy(activeA.Output), Copy(activeB.Output), new TransactionOutput { Amount = closeFee.Output.Amount - 1, OneTimeAddress = p } } };
                Resign(close, provider, 1);
                Reject(() => Apply(close, CloneUtxo(boundary)), "consensus forbids closure of the permanently locked ownerless principal");
                var addDraft = TokenExchange.PrepareLiquidityChange(chain, new Transaction[0], Keys(last), activeA, activeB, InitialLaunch.ProviderPublicKey, l, 1000000, true, 1, final.Inputs.Select(input => input.TransactionId + ":" + input.OutputIndex));
                var add = Complete(addDraft, last); Apply(add, boundary);
                Check(boundary[add.Id + ":0"].PoolParticipation.LockedShares == InitialLaunch.PrincipalSupply, "additional liquidity cannot alter the locked principal shares");
                var swapA = boundary[add.Id + ":0"]; var swapB = boundary[add.Id + ":1"];
                var feeSource = boundary.Values.First(item => item.OutputIndex >= 0 && item.Output.OneTimeAddress == b && item.Output.AssetId == null && !item.IsPoolReserve);
                var tokenSource = boundary.Values.First(item => item.OutputIndex >= 0 && item.Output.OneTimeAddress == b && item.Output.AssetId == InitialLaunch.UsdcTokenId);
                long amount = InitialLaunch.FundingUnit;
                long receive = TokenExchange.Quote(swapB.Output.Amount, swapA.Output.Amount, amount);
                var activeSwap = new Transaction { Kind = TransactionKind.LiquiditySwap, Fee = 1, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                    PoolParticipation = swapA.PoolParticipation.Copy(), LiquiditySwap = new LiquiditySwapTerms { InputSide = 1, Amount = amount },
                    Inputs = new[] { feeSource, tokenSource, swapA, swapB }.Select(item => new TransactionInput { TransactionId = item.TransactionId, OutputIndex = item.OutputIndex,
                        PublicKey = item.IsPoolReserve ? InitialLaunch.ProviderPublicKey : Public(buyer, b) }).ToList(),
                    Outputs = new List<TransactionOutput> {
                        new TransactionOutput { Amount = swapA.Output.Amount - receive, OneTimeAddress = q },
                        new TransactionOutput { AssetId = InitialLaunch.UsdcTokenId, Amount = swapB.Output.Amount + amount, OneTimeAddress = q },
                        new TransactionOutput { Amount = receive, OneTimeAddress = b },
                        new TransactionOutput { Amount = feeSource.Output.Amount - 1, OneTimeAddress = b },
                        new TransactionOutput { AssetId = InitialLaunch.UsdcTokenId, Amount = tokenSource.Output.Amount - amount, OneTimeAddress = b } } };
                Resign(activeSwap, buyer, 2);
                if (args.Length > 0)
                {
                    var participation = new { totalSharesAtomic = swapA.PoolParticipation.Total.ToString(), lockedSharesAtomic = swapA.PoolParticipation.LockedShares.ToString(),
                        holders = swapA.PoolParticipation.Shares.Select(item => new { address = item.Key, sharesAtomic = item.Value.ToString() }).ToArray() };
                    var draft = new { networkId = Blockchain.NetworkId, consensusVersion = Blockchain.ConsensusVersion, operation = "swap", transactionKind = (int)activeSwap.Kind, draftId = new string('a', 32),
                        timestampUtcTicks = activeSwap.TimestampUtcTicks.ToString(), feeAtomic = "1", poolFeeBasisPoints = TokenExchange.PoolFeeBasisPoints,
                        providerAddress = q, changeAddress = b, poolId = creation.Id, reserveTransactionId = add.Id,
                        assetA = new { id = "povix", name = "POVIX", symbol = "POVIX", decimals = 8 },
                        assetB = new { id = InitialLaunch.UsdcTokenId, name = "USDC local POVIX (sem ponte)", symbol = "USDC", decimals = 6 },
                        reserveAAtomic = swapA.Output.Amount.ToString(), reserveBAtomic = swapB.Output.Amount.ToString(),
                        amountAtomic = amount.ToString(), minimumReceiveAtomic = "1", receiveAtomic = receive.ToString(), inputSide = 1,
                        inputBurnAtomic = "0", outputBurnAtomic = "0", userInputCount = 2, burnEnabled = false, allowUserLiquidity = true, participation,
                        expiresUtc = DateTime.UtcNow.AddMinutes(15).ToString("o"),
                        inputs = new[] { feeSource, tokenSource, swapA, swapB }.Select(item => new { transactionId = item.TransactionId, outputIndex = item.OutputIndex,
                            address = item.Output.OneTimeAddress, assetId = item.Output.AssetId ?? "povix", amountAtomic = item.Output.Amount.ToString() }).ToArray(),
                        outputs = activeSwap.Outputs.Select(item => new { assetId = item.AssetId ?? "povix", amountAtomic = item.Amount.ToString(), address = item.OneTimeAddress }).ToArray(),
                        signingPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes((string)typeof(Transaction).GetMethod("SigningPayload", Hidden).Invoke(activeSwap, null))) };
                    File.WriteAllText(args[0], new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(draft));
                    using (var stream = File.Create(args[0] + ".chain.json")) new DataContractJsonSerializer(typeof(Block[])).WriteObject(stream, chain.Blocks.ToArray());
                }
                bad = Copy(activeSwap); bad.PoolParticipation.LockedShares--; Resign(bad, buyer, 2);
                Reject(() => Apply(bad, CloneUtxo(boundary)), "a swap cannot dilute the permanent principal shares");
                Apply(activeSwap, boundary);
                Check((System.Numerics.BigInteger)activeSwap.Outputs[0].Amount * activeSwap.Outputs[1].Amount > (System.Numerics.BigInteger)swapA.Output.Amount * swapB.Output.Amount,
                    "active swaps retain the trading fee in the reserves for all liquidity shares");
                var removeA = boundary[activeSwap.Id + ":0"]; var removeB = boundary[activeSwap.Id + ":1"];
                long shares = 1000000;
                long amountA = TokenExchange.LiquidityAmount(removeA.Output.Amount, shares, removeA.PoolParticipation.Total, false);
                long amountB = TokenExchange.LiquidityAmount(removeB.Output.Amount, shares, removeA.PoolParticipation.Total, false);
                var removeFee = boundary.Values.First(item => item.OutputIndex >= 0 && item.Output.OneTimeAddress == l && item.Output.AssetId == null && !item.Output.InitialLaunchLocked && !item.IsPoolReserve);
                var remove = new Transaction { Kind = TransactionKind.LiquidityRemove, Fee = 1, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                    PoolParticipation = new PoolParticipation { LockedShares = InitialLaunch.PrincipalSupply }, LiquidityChange = new LiquidityChangeTerms { Address = l, Shares = shares },
                    Inputs = new[] { removeFee, removeA, removeB }.Select(item => new TransactionInput { TransactionId = item.TransactionId, OutputIndex = item.OutputIndex,
                        PublicKey = item.IsPoolReserve ? InitialLaunch.ProviderPublicKey : Public(last, l) }).ToList(),
                    Outputs = new List<TransactionOutput> {
                        new TransactionOutput { Amount = removeA.Output.Amount - amountA, OneTimeAddress = q },
                        new TransactionOutput { AssetId = InitialLaunch.UsdcTokenId, Amount = removeB.Output.Amount - amountB, OneTimeAddress = q },
                        new TransactionOutput { Amount = amountA, OneTimeAddress = l },
                        new TransactionOutput { AssetId = InitialLaunch.UsdcTokenId, Amount = amountB, OneTimeAddress = l },
                        new TransactionOutput { Amount = removeFee.Output.Amount - 1, OneTimeAddress = l } } };
                Resign(remove, last, 1); Apply(remove, boundary);
                Check(boundary[remove.Id + ":0"].PoolParticipation.Total == InitialLaunch.PrincipalSupply && boundary[remove.Id + ":0"].PoolParticipation.Shares.Count == 0,
                    "additional provider withdraws its entire position at the current reserves including accrued fees");
                var before = new Blockchain(prePurchaseBlocks);
                Check(before.GetInitialLaunchStatus().Purchases == 2 && before.GetInitialLaunchLockedBalance(new[] { b }) == 0 && !before.GetActiveValidators().Any(item => item.ValidatorId == b), "replay of the parent chain revokes the orphaned purchase and lock");
                Check(WalletBalanceSummary.Read(before, buyer, new Transaction[0]).LaunchLockText == string.Empty,
                    "Desktop clears the launch lock notice when the received purchase is removed by replay");
                var query = new BlockchainQueryApi(chain);
                Check(query.GetLedger(0, 1000).Single(entry => entry.TransactionId == creation.Id).IssuedAmount == InitialLaunch.ReserveSupply + Blockchain.WalletCreationReward && chain.Blocks[0].Rules.TotalSupplyAtomicUnits == Blockchain.MaximumSupply + ProofOfStake.MaximumSupply, "public emission accounting includes the launch reserve exactly once");
            }
        }
        Console.WriteLine(checks + " initial launch checks passed");
    }
}
