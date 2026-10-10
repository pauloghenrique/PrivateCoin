using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization.Json;
using PrivateCoin.Core;

internal static class PrivateCoinProofOfWorkRegression
{
    private static int checks;
    public static int Main()
    {
        try { Run(); Migration(); Console.WriteLine(checks + " hybrid consensus checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run()
    {
        Check(ProofOfWork.MeetsTarget(ProofOfWork.Target.ToString("x64", CultureInfo.InvariantCulture)), "target boundary is accepted");
        Check(!ProofOfWork.MeetsTarget((ProofOfWork.Target + 1).ToString("x64", CultureInfo.InvariantCulture)), "hash above the target is rejected");
        Check(!ProofOfWork.MeetsTarget("000ABC" + new string('0', 58)) && !ProofOfWork.MeetsTarget("000") && !ProofOfWork.MeetsTarget(null), "malformed proof hashes are rejected");
        Check(ProofOfWork.WorkPerBlock == new BigInteger(4096), "work comes from the required 12-bit target");
        using (var payer = new Wallet()) using (var recipient = new Wallet())
        using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain(); Fund(chain, payer.CreateReceiveAddress());
            string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
            Fund(chain, firstAddress); Fund(chain, secondAddress);
            LegacyConsensusFixture.SelfBlock(chain, first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, Blockchain.OneCoin, 0));
            LegacyConsensusFixture.SelfBlock(chain, second.CreateStakeLockTransaction(chain, new Transaction[0], secondAddress, 2 * Blockchain.OneCoin, 0));
            var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.OneCoin), second.CreateValidatorStake(secondAddress, 2 * Blockchain.OneCoin) };
            using (var batches = new ValidationBatchFixture(chain))
            {
                var common = Clone(chain.Blocks);
                Transaction tx = payer.CreateTransaction(chain, new Transaction[0], recipient.CreateReceiveAddress(), 12345, 7);
                Reject(() => chain.AddProofOfStakeBlock(new Transaction[0], validators), "empty consensus blocks are rejected");
                Reject(() => chain.AddProofOfStakeBlock(new[] { tx }, validators), "unapproved operations are rejected");
                Check(chain.Blocks.Count == common.Count && Blockchain.SelectValidationBatch(new[] { tx }).Count == 1,
                    "one operation is selected without adding a block until approval and mining");
                Block block = batches.Confirm(chain, new[] { tx }, validators);
                Check(chain.IsValid() && block.ConsensusVersion == Blockchain.ConsensusVersion && block.Transactions.Count == 2,
                    "one signed operation plus settlement produces a valid hybrid block");
                Check(block.Validators.Single(v => v.IsCreator).ValidatorId == ProofOfStake.SelectCreator(validators, block.PreviousHash, block.Height).ValidatorId,
                    "locked stake independently determines the block creator");
                Check(block.Validators.Count == 2 && block.TransactionValidations.Count == 1 && ProofOfWork.MeetsTarget(block.Hash),
                    "both signed stake proofs and proof of work are mandatory");
                Check(block.Transactions[0].Outputs.Sum(o => o.Amount) == ProofOfStake.GetBlockReward(block.Height) && block.Transactions.Skip(1).Sum(item => item.GetValidationFeeOutput().Amount) == block.Transactions.Skip(1).Sum(item => item.Fee),
                    "scheduled reward and all fees are conserved");
                Check(chain.GetConfirmations(tx.Id) == 1 && chain.GetConfirmationWork(tx.Id) == ProofOfWork.WorkPerBlock,
                    "inclusion gives one confirmation and its required work");
                Check(chain.GetConfirmations("unknown") == 0 && chain.GetConfirmationWork("unknown") == 0, "unknown transactions have no confirmations");
                Check(chain.ChainWork == chain.Blocks.Count * ProofOfWork.WorkPerBlock, "cumulative work is exact and includes genesis");
                var queries = new BlockchainQueryApi(chain); BlockchainSummary summary = queries.GetSummary();
                Check(summary.ChainWork == chain.ChainWork.ToString(CultureInfo.InvariantCulture) &&
                    summary.IssuedTokenAmount == chain.Blocks.Where(b => b.Validators == null || b.Validators.Count == 0).SelectMany(b => b.Transactions).Where(t => t.Inputs.Count == 0).Sum(t => t.Outputs.Sum(o => o.Amount)) + ProofOfStake.GetBlockReward(block.Height),
                    "the explorer exposes work and issuance without counting fees as emission");
                Check(queries.GetLedger(0, 100).Single(entry => entry.TransactionId == tx.Id).Confirmations == 1,
                    "ledger entries expose current confirmation counts");
                Check(new Blockchain(Clone(chain.Blocks)).IsValid(), "serialized hybrid history is independently validated");

                var invalidProof = Clone(chain.Blocks);
                do { invalidProof.Last().Nonce++; invalidProof.Last().Hash = Hash(invalidProof.Last()); }
                while (ProofOfWork.MeetsTarget(invalidProof.Last().Hash));
                Reject(() => new Blockchain(invalidProof), "correct signatures and hash cannot replace sufficient proof of work");
                var forgedStake = Clone(chain.Blocks);
                forgedStake.Last().Validators[0].VoteSignature = Convert.ToBase64String(new byte[256]);
                LegacyConsensusFixture.Mine(forgedStake.Last());
                Reject(() => new Blockchain(forgedStake), "a newly mined proof cannot authorize a forged stake vote");
                var malformed = Clone(chain.Blocks); malformed.Last().Transactions = null;
                Reject(() => new Blockchain(malformed), "malformed peer data is rejected");
                var lowerVersion = Clone(chain.Blocks); lowerVersion.Last().ConsensusVersion = 4;
                LegacyConsensusFixture.Mine(lowerVersion.Last());
                Reject(() => new Blockchain(lowerVersion), "a chain cannot downgrade from v11 to v4");
                Reject(() => chain.AddProofOfStakeBlock(block.Transactions.Skip(1), new[] { validators[0] }), "a miner cannot omit eligible locked stake");

                var luckyBlocks = Clone(chain.Blocks);
                do { LegacyConsensusFixture.Mine(luckyBlocks.Last()); } while (luckyBlocks.Last().Hash[3] != '0');
                Check(new Blockchain(luckyBlocks).ChainWork == chain.ChainWork, "extra lucky zeros give no extra work");
                batches.Confirm(chain, new Transaction[0], validators);
                batches.Confirm(chain, new Transaction[0], validators);
                Check(chain.GetConfirmations(tx.Id) == 3 && chain.GetConfirmationWork(tx.Id) == 3 * ProofOfWork.WorkPerBlock,
                    "confirmations and confirmation work grow with subsequent individual blocks");
                Check(!chain.TryReplaceChain(luckyBlocks), "a shorter fork with lucky zeros cannot defeat greater cumulative work");
                bool changed;
                Check(chain.TrySynchronizeChain(Clone(chain.Blocks), out changed) && !changed, "an identical validated chain completes synchronization");
                Check(!chain.TrySynchronizeChain(common, out changed) && !changed, "a stale valid prefix cannot complete synchronization");
                var fork = new Blockchain(common);
                for (int i = 0; i < 4; i++) batches.Confirm(fork, new Transaction[0], validators);
                Check(chain.TryReplaceChain(fork.Blocks), "a valid fork with greater work replaces confirmed history without a checkpoint");
                Check(chain.GetConfirmations(tx.Id) == 0 && chain.GetConfirmationWork(tx.Id) == 0 && chain.GetBalance(recipient.OwnedOneTimeAddresses) == 0,
                    "orphaned confirmations and balances reset after reorganization");
                chain.ValidatePendingTransactions(new[] { tx }); Check(true, "an orphaned valid transaction can return to the pending queue");
                Check(!new Blockchain(chain.Blocks).TryReplaceChain(luckyBlocks), "restart cannot resurrect a lower-work fork");
                var equalA = new Blockchain(common); batches.Confirm(equalA, new Transaction[0], validators);
                var equalB = new Blockchain(luckyBlocks);
                string winner = string.CompareOrdinal(equalA.Blocks.Last().Hash, equalB.Blocks.Last().Hash) < 0 ? equalA.Blocks.Last().Hash : equalB.Blocks.Last().Hash;
                equalA.TryReplaceChain(equalB.Blocks); equalB.TryReplaceChain(equalA.Blocks);
                Check(equalA.Blocks.Last().Hash == winner && equalB.Blocks.Last().Hash == winner, "equal-work forks converge using a deterministic tip hash");
            }
        }
    }

    private static void Migration()
    {
        using (var first = new Wallet()) using (var second = new Wallet())
        {
            var legacy = new Blockchain(); string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
            LegacyConsensusFixture.Fund(legacy, firstAddress); LegacyConsensusFixture.Fund(legacy, secondAddress);
            LegacyConsensusFixture.Lock(legacy, first.CreateStakeLockTransaction(legacy, new Transaction[0], firstAddress, Blockchain.OneCoin, 1));
            LegacyConsensusFixture.Lock(legacy, second.CreateStakeLockTransaction(legacy, new Transaction[0], secondAddress, Blockchain.OneCoin, 1));
            var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.OneCoin), second.CreateValidatorStake(secondAddress, Blockchain.OneCoin) };
            Check(new Blockchain(Clone(legacy.Blocks)).GetActiveValidators().Count == 2, "version-4 collateral restores with its original balances");
            using (var batches = new ValidationBatchFixture(legacy))
            {
                batches.Confirm(legacy, new Transaction[0], validators);
                Check(legacy.IsValid() && legacy.Blocks.Last().ConsensusVersion == Blockchain.ConsensusVersion, "legacy collateral can participate in the upgraded hybrid consensus");
                var unlock = first.CreateStakeUnlockTransaction(legacy, firstAddress, 0);
                LegacyConsensusFixture.SelfBlock(legacy, unlock);
                Check(new Blockchain(Clone(legacy.Blocks)).GetActiveValidators().Count == 1, "signed stake unlock remains valid after upgrade and restart");
            }
        }
    }

    private static string Hash(Block block) => (string)typeof(Block).GetMethod("CalculateHash", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(block, null);
    private static void Fund(Blockchain chain, string address) { LegacyConsensusFixture.Fund(chain, address); }
    private static List<Block> Clone(IEnumerable<Block> blocks)
    {
        var serializer = new DataContractJsonSerializer(typeof(List<Block>));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, blocks.ToList()); stream.Position = 0; return (List<Block>)serializer.ReadObject(stream); }
    }
    private static void Reject(Action operation, string label)
    {
        try { operation(); }
        catch (InvalidOperationException) { checks++; Console.WriteLine("PASS " + label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
}
