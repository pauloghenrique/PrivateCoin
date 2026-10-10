using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using PrivateCoin.Core;

internal static class PrivateCoinImmediateMigrationRegression
{
    private static int checks;
    public static int Main()
    {
        try
        {
            var genesis = new Blockchain(); var oldOperations = new List<Transaction>();
            for (int i = 0; i < 20; i++) oldOperations.Add(Creation("v8-wallet-" + i, i + 1));
            var oldBlock = new Block { ConsensusVersion = 8, Height = 1, PreviousHash = genesis.Blocks[0].Hash,
                TimestampUtcTicks = DateTime.UtcNow.Ticks, Transactions = oldOperations };
            LegacyConsensusFixture.Mine(oldBlock);
            var distribution = Creation("v9-wallet", 21); distribution.Kind = TransactionKind.WalletDistribution; distribution.Id = LegacyConsensusFixture.Id(distribution);
            var oldDistribution = new Block { ConsensusVersion = 9, Height = 2, PreviousHash = oldBlock.Hash,
                TimestampUtcTicks = distribution.TimestampUtcTicks, Transactions = new List<Transaction> { distribution } };
            LegacyConsensusFixture.Mine(oldDistribution);
            var chain = new Blockchain(genesis.Blocks.Concat(new[] { oldBlock, oldDistribution }));
            Check(chain.GetBalance(new[] { "v8-wallet-0" }) == Blockchain.WalletCreationReward && chain.GetBalance(new[] { "v9-wallet" }) == Blockchain.WalletCreationReward,
                "v8 batch rewards and historical v9 distribution blocks retain their balances");
            var receipt = chain.GetUncountedWalletCreations().Single();
            var pending = new List<Transaction> { receipt, Creation("v8-pending-wallet", 22) };
            chain.ValidatePendingTransactions(pending);
            Check(chain.Blocks.Count == 3 && chain.GetSpendableBalance(new[] { "v8-pending-wallet" }, pending) == 0,
                "pending legacy creations now wait for a block without requiring a migration block");
            Check(receipt.Outputs[0].Amount == 0 && chain.GetSpendableBalance(new[] { "v9-wallet" }, pending) == Blockchain.WalletCreationReward,
                "historical v9 receipts count once without duplicating their initial reward");
            Reject(() => chain.CreateWalletCreationTransaction("v8-wallet-0", pending), "historical v8 addresses cannot receive a second distribution");
            Reject(() => chain.CreateWalletCreationTransaction("v9-wallet", pending), "historical v9 addresses cannot receive a second distribution");
            while (pending.Count < 20) pending.Add(chain.CreateWalletCreationTransaction("v10-wallet-" + pending.Count, pending));
            foreach (var registration in pending) chain.AddBlock(new[] { registration });
            var restored = new Blockchain(Clone(chain.Blocks));
            Check(restored.IsValid() && restored.Blocks.Last().ConsensusVersion == Blockchain.ConsensusVersion && restored.GetUncountedWalletCreations().Count == 0,
                "historical v8/v9 operations extend into the current consensus and receipts stay counted after restart");
            Check(new BlockchainQueryApi(restored).GetSummary().InitialDistributionBlocksIssued == 40,
                "v8, v9 and v10 issuance is counted exactly once");
            var bad = Clone(chain.Blocks); bad.Last().ConsensusVersion = 8; LegacyConsensusFixture.Mine(bad.Last());
            Reject(() => new Blockchain(bad), "the chain cannot downgrade from historical v9 to v8");
            bad = Clone(chain.Blocks); bad[2].ConsensusVersion = 10; LegacyConsensusFixture.Mine(bad[2]);
            Reject(() => new Blockchain(bad), "v10 rejects an isolated wallet distribution block");
            var forged = Creation("v9-wallet", 99); var next = new List<Transaction> { forged };
            for (int i = 0; i < 19; i++) next.Add(Creation("forged-new-" + i, 100 + i));
            var duplicateBlock = new Block { ConsensusVersion = Blockchain.ConsensusVersion, Height = chain.Blocks.Count, PreviousHash = chain.Blocks.Last().Hash,
                TimestampUtcTicks = DateTime.UtcNow.Ticks, Transactions = next };
            LegacyConsensusFixture.Mine(duplicateBlock);
            Reject(() => new Blockchain(chain.Blocks.Concat(new[] { duplicateBlock })), "a mined complete batch cannot repeat a historical reward address");
            Console.WriteLine(checks + " migration checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static Transaction Creation(string address, long timestamp)
    {
        var tx = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = timestamp };
        tx.Outputs.Add(new TransactionOutput { Amount = Blockchain.WalletCreationReward, OneTimeAddress = address }); tx.Id = LegacyConsensusFixture.Id(tx); return tx;
    }
    private static List<Block> Clone(IEnumerable<Block> blocks)
    {
        var serializer = new DataContractSerializer(typeof(List<Block>));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, blocks.ToList()); stream.Position = 0; return (List<Block>)serializer.ReadObject(stream); }
    }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
}
