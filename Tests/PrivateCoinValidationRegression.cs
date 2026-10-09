using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using PrivateCoin.Core;

internal static class PrivateCoinValidationRegression
{
    private static int checks;
    private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;

    public static int Main()
    {
        try { Run(); Console.WriteLine(checks + " validation checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run()
    {
        using (var payer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain();
            string destination = payer.CreateReceiveAddress();
            for (int index = 0; index < 21; index++) Fund(chain, payer.CreateReceiveAddress());
            string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
            Fund(chain, firstAddress); Fund(chain, secondAddress);
            LegacyConsensusFixture.Lock(chain, first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, Blockchain.OneCoin, 1));
            LegacyConsensusFixture.Lock(chain, second.CreateStakeLockTransaction(chain, new Transaction[0], secondAddress, Blockchain.OneCoin, 1));
            var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.OneCoin), second.CreateValidatorStake(secondAddress, Blockchain.OneCoin) };
            var pending = new List<Transaction>();
            for (int index = 0; index < 21; index++)
                pending.Add(payer.CreateTransaction(chain, pending, destination, 1, index + 1));
            int height = chain.Blocks.Count;
            Reject(() => LegacyConsensusFixture.Confirm(chain, pending.Take(19), validators), "19 validations cannot produce a consensus block");
            Reject(() => LegacyConsensusFixture.Confirm(chain, pending, validators), "21 validations must be split into batches");
            Reject(() => chain.AddBlock(new[] { pending[0] }), "plain blocks cannot bypass transfer validation");
            Check(chain.Blocks.Count == height && Blockchain.SelectValidationBatch(pending.Take(19)).Count == 0,
                "partial batches stay pending without changing the chain");
            Transaction[] batch = Blockchain.SelectValidationBatch(pending).ToArray();
            List<Block> legacyBlocks = Clone(chain.Blocks);
            for (int index = 1; index < legacyBlocks.Count; index++)
            {
                legacyBlocks[index].ConsensusVersion = 0;
                legacyBlocks[index].PreviousHash = legacyBlocks[index - 1].Hash;
                typeof(Blockchain).GetMethod("Mine", HiddenStatic).Invoke(null, new object[] { legacyBlocks[index] });
            }
            var legacy = new Blockchain(legacyBlocks);
            LegacyConsensusFixture.Confirm(legacy, batch, validators);
            Check(legacy.IsValid(), "legacy history can be restored and extended with signed v4 validations");
            Check(batch.Length == 20 && batch.First().Fee == 21 && batch.Last().Fee == 2 && !batch.Any(tx => tx.Id == pending[0].Id),
                "the 20 highest fees are selected and the lowest fee stays queued");
            Transaction tieFirst = pending[0], tieSecond = pending[1];
            long oldFee = tieSecond.Fee; tieSecond.Fee = tieFirst.Fee;
            Check(Blockchain.OrderByFeePriority(new[] { tieSecond, tieFirst }).Select(tx => tx.Id).SequenceEqual(
                new[] { tieFirst, tieSecond }.OrderBy(tx => tx.TimestampUtcTicks).ThenBy(tx => tx.Id, StringComparer.Ordinal).Select(tx => tx.Id)),
                "equal fees use time and ID for deterministic priority");
            tieSecond.Fee = oldFee;
            Block block = LegacyConsensusFixture.Confirm(chain, batch, validators);
            Check(chain.IsValid() && block.TransactionValidations.Count == 20 && block.Transactions.Count == 21,
                "20 signed transaction validations produce one valid block");
            ValidatorStake creator = validators.Single(v => v.ValidatorId == block.Validators.Single(vote => vote.IsCreator).ValidatorId);
            var rewards = ProofOfStake.DistributeReward(block.Height, creator, validators.Where(v => v.ValidatorId != creator.ValidatorId));
            Check(block.Transactions[0].Outputs.Take(rewards.Count).Select(output => output.Amount).SequenceEqual(rewards.Select(share => share.Amount)) &&
                rewards.Sum(share => share.Amount) == ProofOfStake.GetBlockReward(block.Height), "the existing block reward and its split remain unchanged");
            for (int index = 0; index < 20; index++)
            {
                TransactionOutput fee = block.Transactions[0].Outputs[rewards.Count + index];
                Check(fee.Amount == batch[index].Fee && fee.OneTimeAddress == block.TransactionValidations[index].RewardAddress &&
                    block.TransactionValidations[index].TransactionId == batch[index].Id, "fee " + index + " belongs to its signed transaction validator");
            }
            Check(block.Transactions[0].Outputs.Sum(output => output.Amount) == ProofOfStake.GetBlockReward(block.Height) + batch.Sum(tx => tx.Fee),
                "fees are conserved independently of scheduled issuance");
            chain.ValidatePendingTransactions(new[] { pending[0] });
            Check(new Blockchain(Clone(chain.Blocks)).IsValid(), "serialized proofs and the remaining pending transaction survive restoration");
            Tamper(chain, validators, b => b.TransactionValidations[0].Signature = Convert.ToBase64String(new byte[256]), "forged transaction validation is rejected");
            Tamper(chain, validators, b => b.TransactionValidations[0].RewardAddress = destination, "redirected validation recipient is rejected");
            Tamper(chain, validators, b => b.TransactionValidations[0] = b.TransactionValidations[1], "duplicate validation cannot count twice");
            Tamper(chain, validators, b => b.TransactionValidations.RemoveAt(0), "missing transaction validation is rejected");
            Tamper(chain, validators, b => { b.Transactions[0].Outputs[0].Amount++; b.Transactions[0].Outputs[rewards.Count].Amount--; },
                "adding a fee to the block creator reward is rejected even when the total is conserved");
            Tamper(chain, validators, b => { b.Transactions.RemoveAt(20); b.TransactionValidations.RemoveAt(19); }, "a received block with 19 validations is rejected");
            Tamper(chain, validators, b => { var tx = b.Transactions[1]; b.Transactions[1] = b.Transactions[2]; b.Transactions[2] = tx; },
                "received transactions must follow fee priority");
            Tamper(chain, validators, b => { b.ConsensusVersion = 0; b.TransactionValidations = null; }, "a chain cannot downgrade after v4 activation");
            Tamper(chain, validators, b => b.ConsensusVersion = 5, "unsupported consensus versions are rejected");
            Check(ProofOfStake.ScheduledIssuance == ProofOfStake.MaximumSupply, "the complete emission schedule remains unchanged");
        }
    }

    private static void Tamper(Blockchain chain, ValidatorStake[] validators, Action<Block> change, string label)
    {
        List<Block> blocks = Clone(chain.Blocks);
        Block block = blocks.Last(); change(block);
        Transaction reward = block.Transactions[0];
        reward.Id = (string)typeof(Transaction).GetMethod("CalculateId", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(reward, null);
        string payload = (string)typeof(Blockchain).GetMethod("CreateVotePayload", HiddenStatic).Invoke(null, new object[] { block });
        foreach (BlockValidator vote in block.Validators)
            vote.VoteSignature = (string)typeof(ValidatorStake).GetMethod("CreateVote", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(validators.Single(v => v.ValidatorId == vote.ValidatorId), new object[] { payload });
        typeof(Blockchain).GetMethod("Mine", HiddenStatic).Invoke(null, new object[] { block });
        Reject(() => new Blockchain(blocks), label);
    }

    private static List<Block> Clone(IEnumerable<Block> blocks)
    {
        var serializer = new DataContractSerializer(typeof(List<Block>));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, blocks.ToList()); stream.Position = 0;
            return (List<Block>)serializer.ReadObject(stream);
        }
    }

    private static void Fund(Blockchain chain, string address) { LegacyConsensusFixture.Fund(chain, address); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
}
