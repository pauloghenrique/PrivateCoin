using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using PrivateCoin.Core;

internal static class PrivateCoinWalletValidationRegression
{
    private static int checks;
    public static int Main()
    {
        try { Run(); Console.WriteLine(checks + " immediate validation checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run()
    {
        using (var first = new Wallet()) using (var second = new Wallet()) using (var payer = new Wallet())
        {
            var chain = new Blockchain(); var pending = new List<Transaction>();
            string a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress(), c = payer.CreateReceiveAddress();
            pending.Add(chain.CreateWalletCreationTransaction(a, pending));
            Check(chain.Blocks.Count == 1 && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == Blockchain.WalletCreationReward,
                "wallet creation credits 6 POVIX immediately without creating any block");
            Check(pending[0].WalletDistributionId == null && pending[0].Outputs[0].Amount == Blockchain.WalletCreationReward,
                "the creation itself carries the initial distribution");
            Reject(() => chain.CreateWalletCreationTransaction(a, pending), "a pending address cannot receive the reward twice");
            var forged = Copy(pending[0]); forged.Outputs[0].Amount = 0; forged.Id = LegacyConsensusFixture.Id(forged);
            Reject(() => chain.ValidatePendingTransactions(new[] { forged }), "zero issuance is rejected before the promotional limit");
            forged = Copy(pending[0]); forged.Fee = 1; forged.Id = LegacyConsensusFixture.Id(forged);
            Reject(() => chain.ValidatePendingTransactions(new[] { forged }), "wallet creations cannot charge fees");
            pending.Add(chain.CreateWalletCreationTransaction(b, pending));
            pending.Add(chain.CreateWalletCreationTransaction(c, pending));
            Transaction firstLock = first.CreateStakeLockTransaction(chain, pending, a, Blockchain.OneCoin, 0);
            pending.Add(firstLock);
            Transaction secondLock = second.CreateStakeLockTransaction(chain, pending, b, 2 * Blockchain.OneCoin, 0);
            pending.Add(secondLock);
            chain.ValidatePendingTransactions(pending);
            Check(firstLock.ValidatorOwnedAddresses.Contains(firstLock.Outputs[1].OneTimeAddress),
                "the locked validator also declares ownership of its immediately available change");
            Check(chain.Blocks.Count == 1 && chain.GetActiveValidators(pending).Count == 2 && chain.GetActiveValidators().Count == 0,
                "collateral activates in the validated pending ledger without creating blocks");
            Check(chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 5 * Blockchain.OneCoin &&
                chain.GetSpendableBalance(second.OwnedOneTimeAddresses, pending) == 4 * Blockchain.OneCoin,
                "locked collateral cannot be spent and its change is immediately available");
            Reject(() => chain.AddBlock(new[] { firstLock }), "locking tokens cannot create a single-operation block");
            Transaction duplicateLock = first.CreateStakeLockTransaction(chain, pending, a, Blockchain.OneCoin, 0);
            Reject(() => chain.ValidatePendingTransactions(pending.Concat(new[] { duplicateLock })), "a validator cannot lock collateral twice");
            Transaction unlock = first.CreateStakeUnlockTransaction(chain, pending, a, 0);
            var unlocked = pending.Concat(new[] { unlock }).ToArray();
            chain.ValidatePendingTransactions(unlocked);
            Check(chain.Blocks.Count == 1 && chain.GetActiveValidators(unlocked).Count == 1 &&
                chain.GetSpendableBalance(first.OwnedOneTimeAddresses, unlocked) == Blockchain.WalletCreationReward,
                "unlocking pending collateral also validates without creating a block");
            Transaction payment = payer.CreateTransaction(chain, pending, "destination", Blockchain.OneCoin, 37);
            pending.Add(payment);
            Check(payment.Inputs[0].TransactionId == pending[2].Id, "an immediate transfer spends the pending initial reward");
            Check(chain.GetSpendableBalance(payer.OwnedOneTimeAddresses, pending) == 0 &&
                chain.GetSpendableBalance(new[] { "destination" }, pending) == 0,
                "pending transfers reserve inputs but do not release change or recipient funds");
            Reject(() => chain.ValidatePendingTransactions(pending.Concat(new[] { payment })), "a transfer cannot spend the reward twice");
            var reordered = pending.AsEnumerable().Reverse().ToArray();
            chain.ValidatePendingTransactions(reordered);
            Check(Blockchain.OrderByFeePriority(reordered).ToList().FindIndex(tx => tx.Id == pending[2].Id) <
                Blockchain.OrderByFeePriority(reordered).ToList().FindIndex(tx => tx.Id == payment.Id),
                "dependency order precedes fee priority when spending a new reward");
            var restored = new Blockchain(Copy(chain.Blocks.ToList())); var savedPending = Copy(pending);
            restored.ValidatePendingTransactions(savedPending);
            Check(restored.Blocks.Count == 1 && restored.GetActiveValidators(savedPending).Count == 2 &&
                restored.GetSpendableBalance(first.OwnedOneTimeAddresses, savedPending) == 5 * Blockchain.OneCoin,
                "serialized pending operations restore balances and validator collateral without blocks");
            while (pending.Count < 19) pending.Add(chain.CreateWalletCreationTransaction("wallet-" + pending.Count, pending));
            Check(Blockchain.SelectValidationBatch(pending).Count == 0 && chain.Blocks.Count == 1,
                "19 validated operations do not form a block");
            Reject(() => chain.AddBlock(pending), "a partial unsigned batch is rejected");
            pending.Add(chain.CreateWalletCreationTransaction("twentieth-wallet", pending));
            var batch = Blockchain.SelectValidationBatch(pending).ToArray();
            Check(batch.Length == 20, "wallet creation, locks and transfers each count toward 20");
            Reject(() => chain.AddBlock(batch), "a mixed transfer batch cannot bypass stake approval");
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, 2 * Blockchain.OneCoin) };
            Reject(() => chain.AddProofOfStakeBlock(batch, validators.Take(1)), "a block cannot omit eligible collateral");
            Transaction ownPayment = first.CreateTransaction(chain, pending, "validator-payment", 1, 1);
            Transaction[] ownBatch = batch.Where(tx => tx.Id != payment.Id).Concat(new[] { ownPayment }).ToArray();
            Reject(() => chain.AddProofOfStakeBlock(ownBatch, validators), "a validator spending collateral change cannot validate its own transfer block");
            Block block = chain.AddProofOfStakeBlock(batch, validators);
            Check(chain.Blocks.Count == 2 && chain.IsValid() && block.Transactions.Count == 21,
                "exactly 20 operations form the first valid block plus reward settlement");
            Check(block.TransactionValidations.Count == 1 && block.TransactionValidations[0].TransactionId == payment.Id,
                "only the transfer needs an individual proof from a staked validator");
            Check(block.Transactions[0].Outputs.Last().Amount == 37 && block.Transactions[0].Outputs.Last().OneTimeAddress == block.TransactionValidations[0].RewardAddress,
                "the transfer validator receives the chosen fee");
            ValidatorStake creator = validators.Single(v => v.ValidatorId == block.Validators.Single(record => record.IsCreator).ValidatorId);
            var rewards = ProofOfStake.DistributeReward(block.Height, creator, validators.Where(v => v.ValidatorId != creator.ValidatorId));
            Check(block.Transactions[0].Outputs.Take(rewards.Count).Select(o => o.Amount).SequenceEqual(rewards.Select(r => r.Amount)),
                "the scheduled block reward and its 30/70 distribution are unchanged");
            Check(chain.GetBalance(new[] { "destination" }) == Blockchain.OneCoin && new Blockchain(Copy(chain.Blocks.ToList())).IsValid(),
                "confirmed transfers and same-batch collateral restore independently");
            var summary = new BlockchainQueryApi(chain).GetSummary();
            Check(summary.InitialDistributionBlocksIssued == 17 && summary.IssuedTokenAmount == 17 * Blockchain.WalletCreationReward + ProofOfStake.GetBlockReward(block.Height),
                "initial rewards are counted exactly once when the batch is committed");
            Reject(() => chain.CreateWalletCreationTransaction(a, new Transaction[0]), "a confirmed address cannot receive another reward");
            Reject(() => chain.ValidatePendingTransactions(new[] { pending[0] }), "confirmed creations cannot remain in the pending ledger");
            var tampered = Copy(chain.Blocks.ToList()); tampered.Last().Transactions.RemoveAt(20); LegacyConsensusFixture.Mine(tampered.Last());
            Reject(() => new Blockchain(tampered), "received incomplete batches are rejected after re-mining");
            Reject(() => new Blockchain().ValidatePendingTransactions(new[] { payment }), "an orphaned initial reward cannot fund a transfer without its creation");
        }
        var bootstrap = new Blockchain(); var wallets = new List<Transaction>();
        for (int i = 0; i < 20; i++) wallets.Add(bootstrap.CreateWalletCreationTransaction("bootstrap-" + i, wallets));
        Check(bootstrap.Blocks.Count == 1, "20 queued creations do not themselves append a block");
        bootstrap.AddBlock(wallets);
        Check(bootstrap.Blocks.Count == 2 && bootstrap.IsValid() && new Blockchain(Copy(bootstrap.Blocks.ToList())).IsValid(),
            "a complete self-validated batch can bootstrap without locked-token validators");
        var method = typeof(Blockchain).GetMethod("ValidateWalletIssuance", BindingFlags.Static | BindingFlags.NonPublic);
        object[] args = { wallets[0], Blockchain.DistributionSupply - Blockchain.WalletCreationReward }; method.Invoke(null, args);
        Check((long)args[1] == Blockchain.DistributionSupply, "the last initial reward reaches the stipulated supply limit");
        var afterLimit = Copy(wallets[0]); afterLimit.Outputs[0].Amount = 0; afterLimit.Id = LegacyConsensusFixture.Id(afterLimit);
        args = new object[] { afterLimit, Blockchain.DistributionSupply }; method.Invoke(null, args);
        Check((long)args[1] == Blockchain.DistributionSupply, "wallet registrations issue no reward after the initial supply cap");
    }
    private static T Copy<T>(T value)
    {
        var serializer = new DataContractSerializer(typeof(T));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, value); stream.Position = 0; return (T)serializer.ReadObject(stream); }
    }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
}
