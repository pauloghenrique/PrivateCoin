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
        try { Run(); Console.WriteLine(checks + " wallet validation checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run()
    {
        using (var payer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain();
            var registrations = new List<Transaction>();
            for (int i = 0; i < 18; i++) registrations.Add(chain.CreateWalletCreationTransaction(payer.CreateReceiveAddress(), registrations));
            string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
            registrations.Add(chain.CreateWalletCreationTransaction(firstAddress, registrations));
            Check(chain.Blocks.Count == 1 && chain.GetBalance(payer.OwnedOneTimeAddresses) == 0 &&
                Blockchain.SelectValidationBatch(registrations).Count == 0, "19 wallet registrations remain pending and issue no confirmed balance");
            Reject(() => chain.AddBlock(registrations), "19 registrations cannot form a block");
            registrations.Add(chain.CreateWalletCreationTransaction(secondAddress, registrations));
            Block bootstrap = chain.AddBlock(registrations);
            Check(chain.IsValid() && bootstrap.Transactions.Count == 20 && bootstrap.Validators == null && bootstrap.TransactionValidations == null,
                "20 wallet registrations form a block without locked-token validators");
            Check(chain.GetBalance(payer.OwnedOneTimeAddresses) == 18 * Blockchain.WalletCreationReward,
                "wallet rewards become spendable only after the complete batch");
            Check(new Blockchain(Clone(chain.Blocks)).IsValid(), "wallet-only batches survive independent restoration");
            var queries = new BlockchainQueryApi(chain);
            Check(queries.GetSummary().InitialDistributionBlocksIssued == 20 && queries.GetSummary().IssuedTokenAmount == 20 * Blockchain.WalletCreationReward &&
                queries.GetLedger(0, 30).All(entry => entry.Type == LedgerEntryType.InitialDistribution && entry.IssuedAmount == Blockchain.WalletCreationReward),
                "the explorer counts every wallet reward in a batch");
            Reject(() => chain.ValidatePendingTransactions(new[] { registrations[0] }), "confirmed wallet registration cannot be queued again");
            var tampered = Clone(chain.Blocks); tampered.Last().Transactions.RemoveAt(19); LegacyConsensusFixture.Mine(tampered.Last());
            Reject(() => new Blockchain(tampered), "received 19-registration block is rejected after re-mining");
            tampered = Clone(chain.Blocks); tampered.Last().Transactions[0].Kind = TransactionKind.Transfer;
            tampered.Last().Transactions[0].Id = LegacyConsensusFixture.Id(tampered.Last().Transactions[0]); LegacyConsensusFixture.Mine(tampered.Last());
            Reject(() => new Blockchain(tampered), "a no-input transfer cannot masquerade as a wallet registration");
            tampered = Clone(chain.Blocks); tampered.Last().ConsensusVersion = 7; LegacyConsensusFixture.Mine(tampered.Last());
            Reject(() => new Blockchain(tampered), "legacy consensus cannot accept new wallet registration operations");

            chain.AddBlock(new[] { first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, Blockchain.OneCoin, 1) });
            chain.AddBlock(new[] { second.CreateStakeLockTransaction(chain, new Transaction[0], secondAddress, Blockchain.OneCoin, 1) });
            var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.OneCoin), second.CreateValidatorStake(secondAddress, Blockchain.OneCoin) };
            var mixed = new List<Transaction>();
            string destination = payer.CreateReceiveAddress();
            for (int i = 0; i < 18; i++) mixed.Add(payer.CreateTransaction(chain, mixed, destination, 1, i + 1));
            mixed.Add(chain.CreateWalletCreationTransaction("new-wallet-a", mixed));
            Check(Blockchain.SelectValidationBatch(mixed).Count == 0, "18 transfers plus one wallet registration do not complete a block");
            mixed.Add(chain.CreateWalletCreationTransaction("new-wallet-b", mixed));
            Reject(() => chain.AddBlock(mixed), "mixed batches cannot bypass transfer validators");
            Reject(() => chain.AddProofOfStakeBlock(mixed, new ValidatorStake[0]), "transfers require globally locked validators");
            Block block = chain.AddProofOfStakeBlock(mixed, validators);
            Check(chain.IsValid() && block.Transactions.Count == 21 && block.TransactionValidations.Count == 18,
                "18 signed transfers and two fee-free wallet registrations complete one block");
            Check(block.TransactionValidations.All(p => mixed.Single(tx => tx.Id == p.TransactionId).Kind != TransactionKind.WalletCreate),
                "wallet registrations require no individual stake-validation proof");
            ValidatorStake creator = validators.Single(stake => stake.ValidatorId == block.Validators.Single(vote => vote.IsCreator).ValidatorId);
            var rewards = ProofOfStake.DistributeReward(block.Height, creator, validators.Where(v => v.ValidatorId != creator.ValidatorId));
            Check(block.Transactions[0].Outputs.Take(rewards.Count).Select(o => o.Amount).SequenceEqual(rewards.Select(r => r.Amount)),
                "the existing block reward and 30/70 split are unchanged");
            var transfers = Blockchain.OrderByFeePriority(mixed).Where(tx => tx.Kind != TransactionKind.WalletCreate).ToArray();
            for (int i = 0; i < transfers.Length; i++)
                Check(block.Transactions[0].Outputs[rewards.Count + i].Amount == transfers[i].Fee &&
                    block.Transactions[0].Outputs[rewards.Count + i].OneTimeAddress == block.TransactionValidations[i].RewardAddress,
                    "transfer fee " + i + " is paid to its signed validator");
            Check(chain.GetBalance(new[] { "new-wallet-a", "new-wallet-b" }) == 2 * Blockchain.WalletCreationReward &&
                new Blockchain(Clone(chain.Blocks)).IsValid(), "mixed wallet issuance and proofs survive restoration");
            Check(queries.GetSummary().InitialDistributionBlocksIssued == 22 && queries.GetSummary().IssuedTokenAmount == 22 * Blockchain.WalletCreationReward + ProofOfStake.GetBlockReward(block.Height) &&
                queries.GetLedger(0, 30).Count(entry => entry.Type == LedgerEntryType.InitialDistribution) >= 2,
                "the explorer includes wallet issuance inside a signed mixed block");
            var bad = chain.CreateWalletCreationTransaction("bad-wallet", new Transaction[0]);
            bad.Fee = 1; bad.Id = LegacyConsensusFixture.Id(bad);
            Reject(() => chain.ValidatePendingTransactions(new[] { bad }), "wallet registration cannot claim a transfer fee");
            var duplicated = Enumerable.Repeat(chain.CreateWalletCreationTransaction("duplicate-wallet", new Transaction[0]), 20).ToArray();
            Reject(() => chain.AddBlock(duplicated), "a duplicate registration cannot count 20 times");
            var method = typeof(Blockchain).GetMethod("ValidateWalletIssuance", BindingFlags.Static | BindingFlags.NonPublic);
            var lastReward = chain.CreateWalletCreationTransaction("last-reward", new Transaction[0]);
            object[] args = { lastReward, Blockchain.DistributionSupply - Blockchain.WalletCreationReward };
            method.Invoke(null, args);
            Check((long)args[1] == Blockchain.DistributionSupply, "the last promotional reward reaches exactly the supply limit");
            lastReward.Outputs[0].Amount = 0; lastReward.Id = LegacyConsensusFixture.Id(lastReward);
            args = new object[] { lastReward, Blockchain.DistributionSupply }; method.Invoke(null, args);
            chain.ValidatePendingTransactions(new Transaction[0]);
            Check((long)args[1] == Blockchain.DistributionSupply, "wallet registration continues without issuance after the promotional limit");
        }
        // Version 7 histories remain accepted before activation of version 8.
        using (var wallet = new Wallet())
        {
            var legacy = new Blockchain(); LegacyConsensusFixture.Fund(legacy, wallet.CreateReceiveAddress());
            var blocks = Clone(legacy.Blocks); blocks.Last().ConsensusVersion = 7; LegacyConsensusFixture.Mine(blocks.Last());
            var restored = new Blockchain(blocks); var pending = new List<Transaction>();
            for (int i = 0; i < 20; i++) pending.Add(restored.CreateWalletCreationTransaction("migration-" + i, pending));
            restored.AddBlock(pending);
            Check(restored.IsValid() && restored.Blocks.Last().ConsensusVersion == 8, "version 7 history can extend with version 8 wallet batches");
        }
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
