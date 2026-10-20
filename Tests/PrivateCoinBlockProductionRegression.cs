using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using PrivateCoin.Core;

internal static class PrivateCoinBlockProductionRegression
{
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label) { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private static T Copy<T>(T value)
    {
        var serializer = new DataContractSerializer(typeof(T));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, value); stream.Position = 0; return (T)serializer.ReadObject(stream); }
    }
    private static Transaction[] Registrations(Blockchain chain, int count, string prefix)
    {
        var pending = new List<Transaction>();
        for (int i = 0; i < count; i++) pending.Add(chain.CreateWalletCreationTransaction(prefix + i, pending));
        return pending.ToArray();
    }
    private static void Schedule()
    {
        var scheduler = new BlockProductionScheduler(() => { throw new Exception("collection clock must not be consulted"); });
        Transaction[] ready = Enumerable.Range(0, 35).Select(i => new Transaction { Id = "ready-" + i, Fee = i + 1, TimestampUtcTicks = i }).ToArray();
        Check(scheduler.SelectBatch(new Transaction[0]).Count == 0 && scheduler.CandidateCount == 0, "an empty queue never produces a block");
        Check(scheduler.SelectBatch(ready.Take(1)).Count == 1 && scheduler.RemainingWait == TimeSpan.Zero, "one ready operation starts production immediately");
        Check(scheduler.SelectBatch(ready.Take(3)).Count == 3, "partial batches do not wait for more operations");
        Check(scheduler.SelectBatch(ready.Take(19)).Count == 19, "nineteen operations do not wait for the twentieth");
        Transaction[] full = scheduler.SelectBatch(ready).ToArray();
        Check(full.Length == 20 && scheduler.CandidateCount == 20, "a batch never exceeds twenty operations");
        Check(full.Select(tx => tx.Id).SequenceEqual(Blockchain.OrderByFeePriority(ready).Take(20).Select(tx => tx.Id)), "immediate batching retains fee priority");
        Transaction[] overflow = ready.Where(tx => full.All(included => included.Id != tx.Id)).ToArray();
        Check(scheduler.SelectBatch(overflow).Count == 15, "overflow can start its next batch immediately");
        Check(scheduler.SelectBatch(new Transaction[0]).Count == 0 && scheduler.RemainingWait == TimeSpan.Zero, "draining the queue creates no empty follow-up block");
        Check(scheduler.SelectBatch(ready.Take(2), batch => false).Count == 0 && scheduler.CandidateCount == 0, "removing the wait cannot bypass unavailable validators");
        Check(scheduler.SelectBatch(ready.Take(2), batch => batch.Count <= 1).Count == 1, "an eligible smaller batch starts without waiting for incompatible operations");
        Reject(() => scheduler.SelectBatch(new[] { ready[0], ready[0] }), "duplicate ready operations remain rejected");
        var parent = new Transaction { Id = "parent", Fee = 3, TimestampUtcTicks = long.MaxValue };
        var child = new Transaction { Id = "child", Fee = 4, TimestampUtcTicks = 1 };
        child.Inputs.Add(new TransactionInput { TransactionId = parent.Id, OutputIndex = 0 });
        Check(scheduler.SelectBatch(new[] { parent, child }).Select(tx => tx.Id).SequenceEqual(new[] { "parent", "child" }), "dependencies precede children even when child has higher priority");
        Check(scheduler.SelectBatch(new[] { parent, child }, batch => batch.All(tx => tx.Id != parent.Id)).Count == 0, "a child cannot bypass an ineligible prerequisite");
        Check(Blockchain.BlockWaitSeconds == 0 && ProofOfWork.TargetBlockSeconds == 10, "collection delay is zero while dynamic mining still targets ten seconds");
        var rules = new Blockchain().Blocks[0].Rules;
        Check(rules.BlockWaitSeconds == 0 && rules.BlockProductionRule.Contains("without a fixed collection wait"), "genesis declares immediate collection policy");
    }
    private static void Consensus()
    {
        var bootstrap = new Blockchain();
        Transaction[] registrations = Registrations(bootstrap, 21, "limit-");
        Reject(() => bootstrap.AddBlock(registrations), "the producer rejects more than twenty self-validated operations");
        Block fullBootstrap = bootstrap.AddBlock(registrations.Take(20));
        Check(fullBootstrap.ConsensusVersion == Blockchain.ConsensusVersion && fullBootstrap.Transactions.Count == 20 && bootstrap.IsValid(), "twenty registrations form a valid bootstrap block");
        bootstrap.AddBlock(registrations.Skip(20));
        Check(new Blockchain(Copy(bootstrap.Blocks.ToList())).IsValid(), "full and partial bootstrap blocks survive restoration");
        var v16History = Copy(bootstrap.Blocks.ToList());
        v16History.RemoveAt(v16History.Count - 1);
        v16History[1].ConsensusVersion = 16; LegacyConsensusFixture.Mine(v16History[1]);
        var migrated = new Blockchain(v16History);
        migrated.AddBlock(Registrations(migrated, 2, "v18-migration-"));
        Check(migrated.IsValid() && migrated.Blocks[1].ConsensusVersion == 16 && migrated.Blocks.Last().ConsensusVersion == Blockchain.ConsensusVersion,
            "historical full v16 batches extend into current consensus without changing original transaction hashes");
        var received = Copy(bootstrap.Blocks.ToList());
        received.RemoveAt(received.Count - 1); received.Last().Transactions.Add(registrations[20]);
        LegacyConsensusFixture.Mine(received.Last());
        Reject(() => new Blockchain(received), "a received and re-mined oversized block is rejected");
        received = Copy(bootstrap.Blocks.ToList());
        received[1].ConsensusVersion = 15; LegacyConsensusFixture.Mine(received[1]);
        Reject(() => new Blockchain(received.Take(2)), "historical v15 cannot reinterpret a twenty-operation block as valid");

        var v15 = new Blockchain(); Transaction[] old = Registrations(v15, 1, "v15-");
        Block oldBlock = v15.AddBlock(old); oldBlock.ConsensusVersion = 15; LegacyConsensusFixture.Mine(oldBlock);
        v15 = new Blockchain(Copy(v15.Blocks.ToList()));
        Check(v15.IsValid() && v15.Blocks.Last().ConsensusVersion == 15, "historical single-operation v15 bootstrap blocks remain valid");
        v15.AddBlock(Registrations(v15, 2, "current-"));
        Check(new Blockchain(Copy(v15.Blocks.ToList())).IsValid(), "v15 history extends with a multi-operation current-consensus block");
        Block downgrade = v15.AddBlock(Registrations(v15, 1, "downgrade-"));
        downgrade.ConsensusVersion = 15; LegacyConsensusFixture.Mine(downgrade);
        Reject(() => new Blockchain(Copy(v15.Blocks.ToList())), "a chain cannot return to v15 after activating current consensus");

        using (var payer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain(); string a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress();
            var funding = new List<Transaction>();
            foreach (string address in Enumerable.Range(0, 20).Select(i => payer.CreateReceiveAddress()).Concat(new[] { a, b }))
                funding.Add(chain.CreateWalletCreationTransaction(address, funding));
            chain.AddBlock(funding.Take(20)); chain.AddBlock(funding.Skip(20));
            Transaction lockA = first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 0);
            Transaction lockB = second.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 0);
            Check(chain.GetActiveValidators(new[] { lockA, lockB }).Count == 0, "a queued collateral batch activates no validators");
            chain.AddBlock(new[] { lockA, lockB });
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
            var operations = new List<Transaction>();
            string owner = payer.OwnedOneTimeAddresses.First();
            operations.Add(payer.CreateTokenTransaction(chain, operations, "Lote", "LOT", 0, 123, owner, 1));
            while (operations.Count < 20) operations.Add(payer.CreateTransaction(chain, operations, "recipient", 1, operations.Count + 1));
            var unapprovedScheduler = new BlockProductionScheduler(() => TimeSpan.FromSeconds(100));
            Check(chain.GetApprovedTransactions(operations).Count == 0 && unapprovedScheduler.SelectBatch(chain.GetApprovedTransactions(operations)).Count == 0,
                "twenty unapproved movements cannot trigger production");
            foreach (Transaction tx in operations) tx.TransactionApproval = chain.CreateTransactionApproval(tx, operations, validators.Take(1));
            Check(chain.GetApprovedTransactions(operations).Count == 20 && chain.GetTokenBalance(new[] { owner }, operations[0].Token.Id) == 0 && chain.GetBalance(new[] { "recipient" }) == 0,
                "twenty approvals release neither native balance nor token supply before a block");
            long beforeSupply = chain.GetBalancesByAddress().Values.Sum();
            Block block = chain.AddProofOfStakeBlock(chain.SelectApprovedValidationBatch(operations), validators);
            Check(block.Transactions.Count == 21 && block.TransactionValidations.Count == 20 && chain.IsValid(), "twenty approved operations share a signed block with one separate reward");
            Check(chain.GetBalance(new[] { "recipient" }) == 19 && chain.GetTokenBalance(new[] { owner }, operations[0].Token.Id) == 123 &&
                chain.GetBalancesByAddress().Values.Sum() == beforeSupply + ProofOfStake.GetBlockReward(block.Height), "the batch settles each balance and fee once and conserves supply");
            Check(new Blockchain(Copy(chain.Blocks.ToList())).IsValid(), "signed full batches restore with all original approvals");
            var transfer = payer.CreateTransaction(chain, new Transaction[0], "recipient", 7, 1);
            transfer.TransactionApproval = chain.CreateTransactionApproval(transfer, new[] { transfer }, validators.Take(1));
            Block partial = chain.AddProofOfStakeBlock(new[] { transfer }, validators);
            Check(partial.Transactions.Count == 2 && chain.IsValid() && chain.GetBalance(new[] { "recipient" }) == 26, "a single approved transfer also forms a valid partial consensus block");
            var unlockA = first.CreateStakeUnlockTransaction(chain, new Transaction[0], a, 0);
            var unlockB = second.CreateStakeUnlockTransaction(chain, new Transaction[0], b, 0);
            Check(chain.GetActiveValidators(new[] { unlockA, unlockB }).Count == 2, "queued collateral releases remain active until their batch is confirmed");
            chain.AddBlock(new[] { unlockA, unlockB });
            Check(chain.GetActiveValidators().Count == 0 && new Blockchain(Copy(chain.Blocks.ToList())).IsValid(), "a confirmed release batch deactivates its validators exactly once");
        }
    }
    public static int Main(string[] args)
    {
        try { Schedule(); if (!args.Contains("--scheduler-only")) Consensus(); Console.WriteLine(checks + " block production checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
