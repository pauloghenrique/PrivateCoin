using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using PrivateCoin.Core;

internal static class PrivateCoinImmediateMigrationRegression
{
    public static int Main()
    {
        try
        {
            var genesis = new Blockchain();
            var oldOperations = new List<Transaction>();
            for (int i = 0; i < 20; i++) oldOperations.Add(LegacyCreation("v8-wallet-" + i, i + 1));
            var oldBlock = new Block { ConsensusVersion = 8, Height = 1, PreviousHash = genesis.Blocks[0].Hash,
                TimestampUtcTicks = DateTime.UtcNow.Ticks, Transactions = oldOperations };
            LegacyConsensusFixture.Mine(oldBlock);
            var chain = new Blockchain(genesis.Blocks.Concat(new[] { oldBlock }));
            Check(chain.GetBalance(new[] { "v8-wallet-0" }) == Blockchain.WalletCreationReward, "historical v8 wallet batches retain their original rewards");
            var pending = LegacyCreation("v8-pending-wallet", 21);
            chain.ValidatePendingTransactions(new[] { pending });
            Check(chain.GetBalance(new[] { "v8-pending-wallet" }) == 0, "a historical pending wallet entry can be loaded without minting offline");
            var receipt = chain.CreateWalletCreationTransaction(pending.Outputs[0].OneTimeAddress, new Transaction[0]);
            chain.ValidatePendingTransactions(new[] { receipt });
            Check(chain.GetBalance(new[] { "v8-pending-wallet" }) == Blockchain.WalletCreationReward && receipt.Outputs[0].Amount == 0,
                "upgrading a pending v8 wallet gives its reward immediately and replaces it with a non-minting receipt");
            Reject(() => chain.ValidatePendingTransactions(new[] { pending }), "the old minting entry cannot be replayed after migration");
            Reject(() => chain.CreateWalletCreationTransaction("v8-wallet-0", new Transaction[0]), "a historical rewarded address cannot receive another reward");
            var restored = new Blockchain(Clone(chain.Blocks));
            Check(restored.IsValid() && restored.GetUncountedWalletCreations().Single().Id == receipt.Id &&
                new BlockchainQueryApi(restored).GetSummary().InitialDistributionBlocksIssued == 21,
                "v8 and v9 history restores with exactly 21 distributions and the same uncounted receipt");
            var forged = Clone(chain.Blocks); forged[1].ConsensusVersion = 9; LegacyConsensusFixture.Mine(forged[1]);
            forged.RemoveAt(2);
            Reject(() => new Blockchain(forged), "a v9 batch cannot reuse the v8 mint-in-batch format");
            forged = Clone(chain.Blocks); forged.Last().ConsensusVersion = 8; LegacyConsensusFixture.Mine(forged.Last());
            Reject(() => new Blockchain(forged), "legacy v8 cannot accept a v9 immediate distribution");
            forged = Clone(chain.Blocks);
            Transaction duplicate = forged.Last().Transactions[0]; duplicate.TimestampUtcTicks++; duplicate.Id = LegacyConsensusFixture.Id(duplicate);
            var duplicateBlock = new Block { ConsensusVersion = 9, Height = chain.Blocks.Count, PreviousHash = chain.Blocks.Last().Hash,
                TimestampUtcTicks = duplicate.TimestampUtcTicks, Transactions = new List<Transaction> { duplicate } };
            LegacyConsensusFixture.Mine(duplicateBlock);
            Reject(() => new Blockchain(chain.Blocks.Concat(new[] { duplicateBlock })), "a re-mined peer block cannot issue the same wallet address twice");
            Console.WriteLine("9 immediate migration checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static Transaction LegacyCreation(string address, long timestamp)
    {
        var tx = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = timestamp };
        tx.Outputs.Add(new TransactionOutput { Amount = Blockchain.WalletCreationReward, OneTimeAddress = address });
        tx.Id = LegacyConsensusFixture.Id(tx); return tx;
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
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
