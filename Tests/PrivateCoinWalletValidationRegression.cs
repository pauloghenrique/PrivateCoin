using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using PrivateCoin.Core;
internal static class PrivateCoinWalletValidationRegression
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static T Copy<T>(T value) { var serializer = new DataContractSerializer(typeof(T)); using (var stream = new MemoryStream()) { serializer.WriteObject(stream, value); stream.Position = 0; return (T)serializer.ReadObject(stream); } }
    static void Reject(Action action, string label) { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    public static int Main()
    {
        try
        {
            using (var first = new Wallet()) using (var second = new Wallet()) using (var payer = new Wallet())
            {
                var chain = new Blockchain(); var pending = new List<Transaction>();
                string a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress(), c = payer.CreateReceiveAddress();
                pending.Add(chain.CreateWalletCreationTransaction(a, pending));
                Check(chain.Blocks.Count == 1 && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 0 && chain.GetBalance(first.OwnedOneTimeAddresses) == 0,
                    "wallet distribution waits for its block before releasing the initial 6 POVIX");
                Reject(() => first.CreateStakeLockTransaction(chain, pending, a, Blockchain.OneCoin, 0), "pending initial rewards cannot activate validators");
                Reject(() => chain.CreateWalletCreationTransaction(a, pending), "pending addresses cannot receive the initial reward twice");
                var forged = Copy(pending[0]); forged.Outputs[0].Amount = 0; forged.Id = LegacyConsensusFixture.Id(forged);
                Reject(() => chain.ValidatePendingTransactions(new[] { forged }), "zero issuance is rejected before the supply cap");
                forged = Copy(pending[0]); forged.Fee = 1; forged.Id = LegacyConsensusFixture.Id(forged);
                Reject(() => chain.ValidatePendingTransactions(new[] { forged }), "wallet registrations cannot charge a fee");
                Check(chain.SelectApprovedValidationBatch(pending).Count == 1, "one registration is ready without waiting for other operations");
                Reject(() => chain.AddBlock(new Transaction[0]), "empty blocks are rejected");
                var another = chain.CreateWalletCreationTransaction(b, pending);
                Reject(() => chain.AddBlock(pending.Concat(new[] { another })), "two registrations cannot share a new block");
                Block funding = chain.AddBlock(pending);
                Check(chain.IsValid() && funding.Transactions.Count == 1 && chain.GetBalance(first.OwnedOneTimeAddresses) == Blockchain.WalletCreationReward,
                    "one registration forms its own valid bootstrap block without staked validators");
                LegacyConsensusFixture.FundBatch(chain, b, c);
                Reject(() => chain.CreateWalletCreationTransaction(a, new Transaction[0]), "confirmed addresses cannot receive another initial reward");
                var firstLock = first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 0);
                var secondLock = second.CreateStakeLockTransaction(chain, new Transaction[0], b, 2 * Blockchain.OneCoin, 0);
                pending = new List<Transaction> { firstLock, secondLock };
                Check(chain.GetActiveValidators(pending).Count == 0 && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 0,
                    "pending collateral reserves its input but activates no validator or spendable change");
                var restored = new Blockchain(Copy(chain.Blocks.ToList()));
                Check(restored.GetActiveValidators(Copy(pending)).Count == 0 && restored.GetSpendableBalance(first.OwnedOneTimeAddresses, Copy(pending)) == 0,
                    "restart preserves collateral reservations without activating pending locks");
                int beforeLocks = chain.Blocks.Count;
                LegacyConsensusFixture.SelfBlock(chain, pending);
                Check(chain.Blocks.Count == beforeLocks + 2 && chain.Blocks.Skip(beforeLocks).All(block => block.Transactions.Count == 1) && chain.IsValid() &&
                    chain.GetActiveValidators().Count == 2 && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, new Transaction[0]) == 5 * Blockchain.OneCoin,
                    "each collateral lock has its own block and releases only its confirmed change");
                var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, 2 * Blockchain.OneCoin) };
                using (var batches = new ValidationBatchFixture(chain))
                {
                    var payment = payer.CreateTransaction(chain, new Transaction[0], "destination", Blockchain.OneCoin, 37);
                    payment.TransactionApproval = chain.CreateTransactionApproval(payment, new[] { payment }, validators.Take(1));
                    Check(chain.HasValidTransactionApproval(payment, new[] { payment }) && chain.GetSpendableBalance(new[] { "destination" }, new[] { payment }) == 0 &&
                        chain.GetSpendableBalance(payer.OwnedOneTimeAddresses, new[] { payment }) == 0,
                        "approved transfers keep recipient outputs and change unavailable until their block");
                    Reject(() => chain.ValidatePendingTransactions(new[] { payment, payment }), "duplicate pending spends are rejected");
                    Block confirmed = batches.Confirm(chain, new[] { payment }, validators);
                    Check(chain.IsValid() && chain.GetBalance(new[] { "destination" }) == Blockchain.OneCoin && chain.GetConfirmations(payment.Id) == 1 &&
                        confirmed.Transactions.Count == 2 && confirmed.TransactionValidations.Count == 1,
                        "one transfer gets its own proof-of-stake block plus the scheduled reward");
                }
                var unlock = first.CreateStakeUnlockTransaction(chain, new Transaction[0], a, 0);
                long beforeUnlock = chain.GetSpendableBalance(first.OwnedOneTimeAddresses, new Transaction[0]);
                Check(chain.GetActiveValidators(new[] { unlock }).Count == 2 && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, new[] { unlock }) == beforeUnlock,
                    "queued unlock keeps collateral active and does not release locked tokens");
                Block unlockBlock = chain.AddBlock(new[] { unlock });
                Check(unlockBlock.Transactions.Count == 1 && chain.IsValid() && chain.GetActiveValidators().Count == 1 &&
                    chain.GetSpendableBalance(first.OwnedOneTimeAddresses, new Transaction[0]) == beforeUnlock + Blockchain.OneCoin,
                    "an individual unlock block deactivates the validator and releases exactly its collateral");
                var tampered = Copy(chain.Blocks.ToList()); tampered.Last().Transactions.Clear(); LegacyConsensusFixture.Mine(tampered.Last());
                Reject(() => new Blockchain(tampered), "received and re-mined empty blocks are rejected");
                Check(new Blockchain(Copy(chain.Blocks.ToList())).IsValid(), "all confirmed balances and collateral restore independently");
                var method = typeof(Blockchain).GetMethod("ValidateWalletIssuance", BindingFlags.Static | BindingFlags.NonPublic);
                object[] args = { funding.Transactions[0], Blockchain.DistributionSupply - Blockchain.WalletCreationReward }; method.Invoke(null, args);
                Check((long)args[1] == Blockchain.DistributionSupply, "the final promotional reward reaches the supply cap");
                var afterLimit = Copy(funding.Transactions[0]); afterLimit.Outputs[0].Amount = 0; afterLimit.Id = LegacyConsensusFixture.Id(afterLimit);
                args = new object[] { afterLimit, Blockchain.DistributionSupply }; method.Invoke(null, args);
                Check((long)args[1] == Blockchain.DistributionSupply, "wallet registrations issue no reward after the cap");
            }
            Console.WriteLine(checks + " individual wallet and collateral block checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
