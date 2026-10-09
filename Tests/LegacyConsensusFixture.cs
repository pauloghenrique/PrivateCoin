using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PrivateCoin.Core;

// Creates historical proofs and wallet funding fixtures for migration checks.
// Deliberately separate from the production block creation API.
internal static class LegacyConsensusFixture
{
    private const int ConsensusVersion = 4;
    private const int ValidationsPerBlock = 20;
    private static readonly object sync = new object();
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static object Call(string name, params object[] args) => typeof(Blockchain).GetMethod(name, Hidden).Invoke(null, args);
    public static string Id(Transaction tx) => (string)typeof(Transaction).GetMethod("CalculateId", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tx, null);
    private static string Vote(ValidatorStake stake, string payload) => (string)typeof(ValidatorStake).GetMethod("CreateVote", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(stake, new object[] { payload });
    public static void Mine(Block block) => Call("Mine", block);
    private static IEnumerable<Transaction> OrderByFeePriority(IEnumerable<Transaction> txs) => Blockchain.OrderByFeePriority(txs);
    private static IEnumerable<ValidatorStake> ExcludeTransactionParticipants(IEnumerable<ValidatorStake> stakes, IEnumerable<Transaction> txs) => (IEnumerable<ValidatorStake>)Call("ExcludeTransactionParticipants", stakes, txs);
    private static bool SameStakeSet(IEnumerable<ValidatorStake> left, IEnumerable<ValidatorStake> right) => (bool)Call("SameStakeSet", left, right);
    private static ValidatorStake SelectTransactionValidator(IEnumerable<ValidatorStake> stakes, Transaction tx, string parent, int height) => (ValidatorStake)Call("SelectTransactionValidator", stakes, tx, parent, height);
    private static string CreateTransactionValidationPayload(Transaction tx, string parent, int height, string address) => (string)Call("CreateTransactionValidationPayload", tx, parent, height, address);
    private static string CreateVotePayload(Block block) => (string)Call("CreateVotePayload", block);
    public static Block Fund(Blockchain chain, string address)
    {
        if (chain.Blocks.Last().ConsensusVersion >= 8)
        {
            var registrations = new List<Transaction>();
            registrations.Add(chain.CreateWalletCreationTransaction(address, registrations));
            while (registrations.Count < Blockchain.ValidationsPerBlock)
                registrations.Add(chain.CreateWalletCreationTransaction("wallet-fixture-" + Guid.NewGuid().ToString("N"), registrations));
            return chain.AddBlock(registrations);
        }
        var reward = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks };
        reward.Outputs.Add(new TransactionOutput { Amount = Blockchain.WalletCreationReward, OneTimeAddress = address });
        reward.Id = Id(reward);
        var block = new Block { ConsensusVersion = Math.Max(4, chain.Blocks.Last().ConsensusVersion), Height = chain.Blocks.Count,
            PreviousHash = chain.Blocks.Last().Hash, TimestampUtcTicks = reward.TimestampUtcTicks,
            Transactions = new List<Transaction> { reward } };
        Mine(block);
        if (!chain.TryReplaceChain(chain.Blocks.Concat(new[] { block }))) throw new Exception("Invalid legacy funding fixture");
        return block;
    }
    public static Block Lock(Blockchain chain, Transaction tx)
    {
        var block = new Block { ConsensusVersion = 4, Height = chain.Blocks.Count,
            PreviousHash = chain.Blocks.Last().Hash, TimestampUtcTicks = DateTime.UtcNow.Ticks,
            Transactions = new List<Transaction> { tx } };
        Mine(block); chain.TryReplaceChain(chain.Blocks.Concat(new[] { block })); return block;
    }
        public static Block Confirm(Blockchain chain, IEnumerable<Transaction> transactions, IEnumerable<ValidatorStake> validators)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            if (validators == null) throw new ArgumentNullException(nameof(validators));
            lock (sync)
            {
                var blocks = chain.Blocks.ToList();
                var pending = OrderByFeePriority(transactions).ToList();
                chain.ValidatePendingTransactions(pending);
                if (pending.Count != ValidationsPerBlock || pending.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != ValidationsPerBlock)
                    throw new InvalidOperationException("A consensus block requires exactly 20 distinct validated transactions.");
                ValidatorStake[] active = ExcludeTransactionParticipants(validators, pending).ToArray();
                ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(chain.GetActiveValidators(), pending).ToArray();
                if (!SameStakeSet(active, globallyEligible))
                    throw new InvalidOperationException("The proposed validators do not match the globally locked collateral set.");
                if (active.Length < 2) throw new InvalidOperationException("At least two active validators are required to create and confirm a block.");
                int height = blocks.Count;
                ValidatorStake creator = ProofOfStake.SelectCreator(active, blocks[blocks.Count - 1].Hash, height);
                ValidatorStake[] confirmers = active.Where(item => item.ValidatorId != creator.ValidatorId).ToArray();
                IReadOnlyList<ValidatorReward> rewards = ProofOfStake.DistributeReward(height, creator, confirmers);
                var validations = pending.Select(transaction =>
                {
                    ValidatorStake validator = SelectTransactionValidator(active, transaction, blocks[blocks.Count - 1].Hash, height);
                    return new TransactionValidation
                    {
                        TransactionId = transaction.Id, ValidatorId = validator.ValidatorId,
                        RewardAddress = validator.RewardAddress, PublicKey = validator.PublicKey,
                        Signature = Vote(validator, CreateTransactionValidationPayload(transaction, blocks[blocks.Count - 1].Hash, height, validator.RewardAddress))
                    };
                }).ToList();
                var reward = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks };
                foreach (ValidatorReward share in rewards)
                    reward.Outputs.Add(new TransactionOutput
                    {
                        Amount = share.Amount,
                        OneTimeAddress = share.RewardAddress
                    });
                for (int index = 0; index < pending.Count; index++)
                    reward.Outputs.Add(new TransactionOutput { Amount = pending[index].Fee, OneTimeAddress = validations[index].RewardAddress });
                reward.Id = Id(reward);
                var block = new Block
                {
                    ConsensusVersion = ConsensusVersion,
                    TransactionValidations = validations,
                    Height = height,
                    PreviousHash = blocks[blocks.Count - 1].Hash,
                    TimestampUtcTicks = reward.TimestampUtcTicks,
                    Transactions = new[] { reward }.Concat(pending).ToList(),
                    Validators = active.OrderBy(item => item.ValidatorId, StringComparer.Ordinal).Select(item => new BlockValidator
                    {
                        ValidatorId = item.ValidatorId,
                        RewardAddress = item.RewardAddress,
                        LockedAmount = item.LockedAmount,
                        IsCreator = item.ValidatorId == creator.ValidatorId,
                        OwnedAddresses = item.OwnedAddresses.OrderBy(address => address, StringComparer.Ordinal).ToList()
                    }).ToList()
                };
                string votePayload = CreateVotePayload(block);
                foreach (BlockValidator record in block.Validators)
                {
                    ValidatorStake validator = active.Single(item => item.ValidatorId == record.ValidatorId);
                    record.PublicKey = validator.PublicKey;
                    record.VoteSignature = Vote(validator, votePayload);
                }
                Mine(block);
                chain.TryReplaceChain(blocks.Concat(new[] { block }));
                return block;
            }
        }

}
