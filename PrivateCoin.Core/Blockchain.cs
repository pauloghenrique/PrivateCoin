using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Core
{
    public sealed class Blockchain
    {
        public const int DecimalPlaces = 8;
        public const long OneCoin = 100000000L;
        public const long WalletCreationReward = 600L * OneCoin;
        public const long DistributionSupply = 600L * OneCoin;
        public const int RewardedWalletLimit = (int)(DistributionSupply / WalletCreationReward);
        public const long MaximumSupply = DistributionSupply + InitialLaunch.ReserveSupply;
        // One atomic unit: 0.00000001 POVIX.
        public const long TransferFeeStep = 1L;
        public const long MaximumTransferFee = OneCoin;
        public const int ConsensusVersion = 31;
        private const int LaunchValidatorVersion = 26;
        private const int PoolReserveVersion = 17;
        private const int VariableBatchVersion = 16;
        private const int BlockConfirmationVersion = 15;
        private const int LegacyImmediateTransferVersion = 14;
        private const int ImmediateTokenVersion = 13;
        private const int ImmediateChangeVersion = 12;
        private const int ApprovalVersion = 11;
        private const int SelfValidatedVersion = 10;
        private const int LegacyImmediateVersion = 9;
        private const int LegacyWalletBatchVersion = 8;
        private const int LegacyHybridVersion = 7;
        private const int LegacySignedVersion = 4;
        public const int MaxOperationsPerBlock = 20;
        public const int BlockWaitSeconds = 10;
        private const int LegacyValidationsPerBlock = 20;
        private static readonly Block canonicalGenesis = BuildCanonicalGenesis();
        public static readonly string GenesisHash = canonicalGenesis.Hash;
        public static readonly string RulesHash = canonicalGenesis.Rules.CalculateHash();
        public static readonly string NetworkId = "povix-mainnet-rules-v1-" + GenesisHash;
        private const long GenesisTimestampUtcTicks = 639028224000000000L;
        private readonly object sync = new object();
        private readonly List<Block> blocks = new List<Block>();

        public Blockchain()
        {
            blocks.Add(CreateGenesisBlock());
        }

        /// <summary>Restores and validates an existing chain.</summary>
        public Blockchain(IEnumerable<Block> existingBlocks)
        {
            if (existingBlocks == null) throw new ArgumentNullException(nameof(existingBlocks));
            blocks.AddRange(existingBlocks);
            if (!IsValid()) throw new InvalidOperationException("The stored blockchain is invalid.");
        }

        public IReadOnlyList<Block> Blocks { get { lock (sync) return blocks.ToArray(); } }

        public Block AddBlock(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            lock (sync)
            {
                var pending = OrderByFeePriority(transactions).ToList();
                ValidateTransactions(pending);
                ValidateBatch(pending);
                if (pending.Any(item => !IsSelfValidatedOperation(item)))
                    throw new InvalidOperationException("Transfers and token operations require signed validator approvals.");
                var block = new Block { ConsensusVersion = ConsensusVersion, Height = blocks.Count, PreviousHash = blocks[blocks.Count - 1].Hash, TimestampUtcTicks = DateTime.UtcNow.Ticks, Transactions = pending };
                Mine(block);
                blocks.Add(block);
                return block;
            }
        }

        public Block AddProofOfStakeBlock(IEnumerable<Transaction> transactions, IEnumerable<ValidatorStake> validators)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            if (validators == null) throw new ArgumentNullException(nameof(validators));
            lock (sync)
            {
                var pending = OrderByFeePriority(transactions).ToList();
                ValidateTransactions(pending);
                ValidateBatch(pending);
                if (pending.Any(tx => IsFirstLaunchPurchase(tx) && tx.TransactionApproval == null)) throw new InvalidOperationException("A rewarded first launch purchase requires a signed validator approval.");
                ValidatorStake[] active = ExcludeTransactionParticipants(validators, pending).ToArray();
                ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(ConsensusValidators(pending, BuildUtxo(), blocks), pending).ToArray();
                if (!ValidValidatorSet(active, globallyEligible, IsLaunchValidationBatch(pending) || IsLaunchRegistrationBatch(pending, BuildUtxo())))
                    throw new InvalidOperationException("The proposed validators do not match the globally locked collateral set.");
                if (active.Length < GetMinimumValidatorsForTransactions(pending)) throw new InvalidOperationException("Not enough eligible validators for this block.");
                var replay = BuildUtxo();
                foreach (Transaction transaction in pending)
                {
                    if (RequiresLockedTokenApproval(transaction)) ValidateTransactionApproval(transaction, replay, blocks);
                    Apply(transaction, replay, false, RequiresLockedTokenApproval(transaction), ConsensusVersion, blocks.Count);
                }
                int height = blocks.Count;
                ValidatorStake creator = ProofOfStake.SelectCreator(active, blocks[blocks.Count - 1].Hash, height);
                ValidatorStake[] confirmers = active.Where(item => item.ValidatorId != creator.ValidatorId).ToArray();
                IReadOnlyList<ValidatorReward> rewards = ProofOfStake.DistributeReward(height, creator, confirmers);
                var validations = pending.Where(RequiresLockedTokenApproval).Select(transaction => transaction.TransactionApproval.Proof).ToList();
                var reward = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks };
                foreach (ValidatorReward share in rewards)
                    reward.Outputs.Add(new TransactionOutput
                    {
                        Amount = share.Amount,
                        OneTimeAddress = share.RewardAddress
                    });
                reward.Id = reward.CalculateId();
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
                    record.VoteSignature = validator.CreateVote(votePayload);
                }
                ValidateProofOfStakeBlock(block, block.PreviousHash, BuildUtxo(), blocks);
                Mine(block);
                blocks.Add(block);
                return block;
            }
        }

        /// <summary>Queues an initial distribution which becomes available after block confirmation.</summary>
        public Transaction CreateWalletCreationTransaction(string rewardAddress, IEnumerable<Transaction> pendingTransactions)
        {
            if (string.IsNullOrWhiteSpace(rewardAddress)) throw new ArgumentException("A wallet reward address is required.", nameof(rewardAddress));
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                Transaction[] pending = pendingTransactions.ToArray();
                ValidateTransactions(pending);
                if (WalletDistributionAddresses().Contains(rewardAddress) || pending.Any(tx => (tx.Kind == TransactionKind.WalletCreate || tx.Kind == TransactionKind.InitialLaunchCreate) &&
                    InitialLaunch.WalletAddress(tx) == rewardAddress))
                    throw new InvalidOperationException("This wallet address already received its initial distribution.");
                var registration = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = DateTime.UtcNow.Ticks };
                int queuedRewards = pending.Count(tx => (tx.Kind == TransactionKind.WalletCreate || tx.Kind == TransactionKind.InitialLaunchCreate) && tx.WalletDistributionId == null && InitialLaunch.WalletReward(tx) == WalletCreationReward);
                if (CountWalletCreationRewards() + queuedRewards == RewardedWalletLimit - 1)
                {
                    registration = InitialLaunch.CreateAutomatic(rewardAddress, registration.TimestampUtcTicks);
                    ValidateTransactions(pending.Concat(new[] { registration }).ToList());
                    return registration;
                }
                registration.Outputs.Add(new TransactionOutput { Amount = CountWalletCreationRewards() + queuedRewards < RewardedWalletLimit ? WalletCreationReward : 0, OneTimeAddress = rewardAddress });
                var registrationUtxo = BuildUtxo();
                foreach (var queued in OrderByFeePriority(pending))
                    Apply(queued, registrationUtxo, false, IsValidTransactionApproval(queued, registrationUtxo, blocks), ConsensusVersion, blocks.Count);
                long usdcReward = InitialLaunch.NextWalletUsdcReward(registrationUtxo);
                if (usdcReward > 0) registration.Outputs.Add(new TransactionOutput { AssetId = InitialLaunch.UsdcTokenId,
                    Amount = usdcReward, OneTimeAddress = rewardAddress });
                registration.Id = registration.CalculateId();
                ValidateTransactions(pending.Concat(new[] { registration }).ToList());
                return registration;
            }
        }

        private static Transaction CreateWalletCreationReceipt(Transaction distribution)
        {
            var receipt = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = distribution.TimestampUtcTicks,
                WalletDistributionId = distribution.Id };
            receipt.Outputs.Add(new TransactionOutput { Amount = 0, OneTimeAddress = distribution.Outputs[0].OneTimeAddress });
            receipt.Id = receipt.CalculateId();
            return receipt;
        }

        /// <summary>Reconstructs uncounted wallet receipts after restart or chain synchronization.</summary>
        public IReadOnlyList<Transaction> GetUncountedWalletCreations()
        {
            lock (sync)
            {
                var counted = new HashSet<string>(blocks.SelectMany(block => block.Transactions)
                    .Where(tx => tx.Kind == TransactionKind.WalletCreate && tx.WalletDistributionId != null).Select(tx => tx.WalletDistributionId), StringComparer.Ordinal);
                return blocks.SelectMany(block => block.Transactions).Where(tx => tx.Kind == TransactionKind.WalletDistribution && !counted.Contains(tx.Id))
                    .Select(CreateWalletCreationReceipt).ToArray();
            }
        }

        private HashSet<string> WalletDistributionAddresses()
        {
            return new HashSet<string>(blocks.Skip(1).SelectMany(block => block.Transactions.Where(tx =>
                tx.Kind == TransactionKind.InitialLaunchCreate || tx.Kind == TransactionKind.WalletDistribution || (tx.Kind == TransactionKind.WalletCreate && tx.WalletDistributionId == null) ||
                ((block.Validators == null || block.Validators.Count == 0) && IsWalletCreationReward(tx))))
                .Select(tx => InitialLaunch.WalletAddress(tx)), StringComparer.Ordinal);
        }

        private static void ValidateWalletReceipt(Transaction receipt, IDictionary<string, Transaction> distributions, ISet<string> counted)
        {
            Transaction distribution;
            if (receipt.WalletDistributionId == null || !distributions.TryGetValue(receipt.WalletDistributionId, out distribution) ||
                receipt.Id != CreateWalletCreationReceipt(distribution).Id || !counted.Add(distribution.Id))
                throw new InvalidOperationException("Invalid or already counted wallet distribution receipt.");
        }

        private int CountWalletCreationRewards()
        {
            return blocks.Skip(1).Sum(block => block.Transactions.Count(tx =>
                ((tx.Kind == TransactionKind.WalletCreate || tx.Kind == TransactionKind.WalletDistribution || tx.Kind == TransactionKind.InitialLaunchCreate) && InitialLaunch.WalletReward(tx) == WalletCreationReward) ||
                ((block.Validators == null || block.Validators.Count == 0) && IsWalletCreationReward(tx))));
        }

        public static bool IsSelfValidatedOperation(Transaction transaction)
        {
            return transaction != null && (transaction.Kind == TransactionKind.WalletCreate || transaction.Kind == TransactionKind.InitialLaunchCreate ||
                IsFirstLaunchPurchase(transaction) && transaction.TransactionApproval == null || transaction.Kind == TransactionKind.StakeLock || transaction.Kind == TransactionKind.StakeUnlock);
        }

        public static bool IsFirstLaunchPurchase(Transaction tx) => tx != null && tx.Kind == TransactionKind.InitialLaunchPurchase &&
            tx.Outputs != null && tx.Outputs.Count >= 3 && tx.Outputs[0].AssetId == null &&
            tx.Outputs[0].Amount == InitialLaunch.ReserveSupply - InitialLaunch.PurchaseAmount &&
            tx.Outputs[1].AssetId == InitialLaunch.UsdcTokenId && tx.Outputs[1].Amount == InitialLaunch.FundingUnit;

        private static bool IsLaunchValidationBatch(IEnumerable<Transaction> transactions)
        {
            var batch = transactions.ToArray();
            return batch.Length > 0 && batch.All(tx => tx.Kind == TransactionKind.InitialLaunchPurchase);
        }

        public static bool HasCompatibleValidatorRules(IEnumerable<Transaction> transactions)
        {
            var batch = transactions.ToArray();
            return !batch.Any(tx => tx.Kind == TransactionKind.InitialLaunchPurchase) || IsLaunchValidationBatch(batch);
        }

        public static int MinimumValidatorsForTransactions(IEnumerable<Transaction> transactions) => IsLaunchValidationBatch(transactions) ? 1 : 2;

        private static bool IsLaunchRegistrationBatch(IEnumerable<Transaction> transactions, IDictionary<string, UnspentOutput> utxo)
        {
            var state = InitialLaunch.State(utxo);
            if (state == null || !state.Created || state.Active) return false;
            var batch = transactions.ToArray();
            return batch.Length > 0 && batch.All(tx => tx.Kind == TransactionKind.WalletCreate && tx.WalletDistributionId == null);
        }

        public int GetMinimumValidatorsForTransactions(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            lock (sync)
            {
                var batch = transactions.ToArray();
                return IsLaunchRegistrationBatch(batch, BuildUtxo()) ? 1 : MinimumValidatorsForTransactions(batch);
            }
        }

        public IReadOnlyList<ValidatorStake> GetValidatorsForTransactions(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            lock (sync) return ConsensusValidators(transactions, BuildUtxo(), blocks);
        }

        private static ValidatorStake LaunchCollateral(UnspentOutput item, IDictionary<string, UnspentOutput> utxo, IList<Block> history)
        {
            if (item == null || !item.Output.InitialLaunchLocked || item.Output.AssetId != null ||
                (item.OutputIndex != 2 && item.OutputIndex != 4)) return null;
            var state = InitialLaunch.State(utxo);
            if (state == null || state.Active) return null;
            return LaunchCollateral(item, history.SelectMany(block => block.Transactions)
                .Where(tx => InitialLaunch.IsOperation(tx.Kind)).ToDictionary(tx => tx.Id, StringComparer.Ordinal));
        }

        private static ValidatorStake LaunchCollateral(UnspentOutput item, IDictionary<string, Transaction> origins)
        {
            if (item == null || !item.Output.InitialLaunchLocked || item.Output.AssetId != null || item.Output.Amount <= 0) return null;
            Transaction origin;
            if (!origins.TryGetValue(item.TransactionId, out origin)) return null;
            bool founder = item.OutputIndex == 4 && origin.Kind == TransactionKind.InitialLaunchCreate && item.Output.Amount == WalletCreationReward;
            if (!founder && !(item.OutputIndex == 2 && origin.Kind == TransactionKind.InitialLaunchPurchase)) return null;
            string key = founder ? item.ValidatorPublicKey : origin.InitialLaunchIdentity?.PublicKey;
            if (!founder && key == null || key != null && Crypto.Sha256(key) != item.Output.OneTimeAddress) return null;
            return new ValidatorStake(item.Output.OneTimeAddress, item.Output.OneTimeAddress, item.Output.Amount,
                new[] { item.Output.OneTimeAddress }, key, null);
        }

        private static ValidatorStake ApprovalCollateral(Transaction tx, UnspentOutput item,
            IDictionary<string, UnspentOutput> utxo, IList<Block> history, int version = ConsensusVersion)
        {
            if (tx.Kind == TransactionKind.InitialLaunchPurchase)
            {
                var source = LaunchCollateral(item, utxo, history);
                return source == null ? null : ConsensusValidators(new[] { tx }, utxo, history).SingleOrDefault(stake => stake.ValidatorId == source.ValidatorId);
            }
            if (item == null || !IsValidatorCollateral(item, utxo, version)) return null;
            return StakesFromUtxo(utxo, version).SingleOrDefault(stake => stake.ValidatorId == CollateralIdentity(item) &&
                stake.RewardAddress == item.ValidatorRewardAddress);
        }

        private static ValidatorStake[] ConsensusValidators(IEnumerable<Transaction> transactions,
            IDictionary<string, UnspentOutput> utxo, IList<Block> history)
        {
            if (!IsLaunchValidationBatch(transactions)) return StakesFromUtxo(utxo);
            var state = InitialLaunch.State(utxo);
            if (state == null || state.Active) return new ValidatorStake[0];
            var origins = history.SelectMany(block => block.Transactions).Where(tx => InitialLaunch.IsOperation(tx.Kind))
                .ToDictionary(tx => tx.Id, StringComparer.Ordinal);
            return utxo.Values.Select(item => LaunchCollateral(item, origins)).Where(item => item != null)
                .GroupBy(item => item.ValidatorId, StringComparer.Ordinal)
                .Select(group => new ValidatorStake(group.Key, group.First().RewardAddress, group.Sum(item => item.LockedAmount),
                    group.SelectMany(item => item.OwnedAddresses).Distinct(StringComparer.Ordinal),
                    group.Select(item => item.PublicKey).FirstOrDefault(key => key != null), null))
                .OrderBy(item => item.ValidatorId, StringComparer.Ordinal).ToArray();
        }

        private static bool ValidValidatorSet(ValidatorStake[] proposed, ValidatorStake[] eligible, bool launch)
        {
            if (!launch) return SameStakeSet(proposed, eligible);
            var known = eligible.ToDictionary(item => item.ValidatorId, StringComparer.Ordinal);
            return proposed.Length > 0 && proposed.Select(item => item.ValidatorId).Distinct().Count() == proposed.Length &&
                proposed.All(item => {
                    ValidatorStake valid;
                    return known.TryGetValue(item.ValidatorId, out valid) && (valid.PublicKey == null || valid.PublicKey == item.PublicKey) &&
                        valid.RewardAddress == item.RewardAddress && valid.LockedAmount == item.LockedAmount &&
                        valid.OwnedAddresses.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(item.OwnedAddresses.OrderBy(value => value, StringComparer.Ordinal));
                });
        }

        public static bool RequiresLockedTokenApproval(Transaction transaction)
        {
            return transaction != null && !IsSelfValidatedOperation(transaction) && transaction.Inputs.Count > 0 &&
                (transaction.Kind == TransactionKind.Transfer || transaction.Kind == TransactionKind.TokenCreate || transaction.Kind == TransactionKind.TokenTransfer || transaction.Kind == TransactionKind.TokenManage || TokenExchange.IsPoolOperation(transaction.Kind));
        }

        // Pending inputs are reserved, but no output, fee or collateral is confirmed here.
        private static Dictionary<string, UnspentOutput> BuildPendingUtxo(IDictionary<string, UnspentOutput> confirmed,
            IEnumerable<Transaction> transactions, IList<Block> history, string stopBefore = null)
        {
            var result = new Dictionary<string, UnspentOutput>(confirmed, StringComparer.Ordinal);
            foreach (Transaction transaction in OrderByFeePriority(transactions))
            {
                if (transaction.Id == stopBefore) break;
                foreach (TransactionInput input in transaction.Inputs) result.Remove(Key(input.TransactionId, input.OutputIndex));
            }
            return result;
        }

        // Preserve the original collateral rules when verifying already-mined v10-v14 blocks.
        private static Dictionary<string, UnspentOutput> BuildLegacyImmediateUtxo(IDictionary<string, UnspentOutput> confirmed,
            IEnumerable<Transaction> transactions, IList<Block> history, string stopBefore = null, bool releaseTokenChange = true, bool releaseTokenAssets = true, bool releaseTokenTransfers = true)
        {
            var result = new Dictionary<string, UnspentOutput>(confirmed, StringComparer.Ordinal);
            foreach (Transaction transaction in OrderByFeePriority(transactions))
            {
                if (transaction.Id == stopBefore) break;
                if (IsSelfValidatedOperation(transaction)) Apply(transaction, result, false);
                else
                {
                    bool approved = IsValidTransactionApproval(transaction, result, history);
                    foreach (TransactionInput input in transaction.Inputs) result.Remove(Key(input.TransactionId, input.OutputIndex));
                    if (approved)
                    {
                        AddValidationFee(transaction, result);
                        if ((releaseTokenChange && transaction.Kind == TransactionKind.TokenCreate) || (releaseTokenTransfers && transaction.Kind == TransactionKind.TokenTransfer))
                            for (int index = 0; index < transaction.Outputs.Count; index++)
                            {
                                TransactionOutput output = transaction.Outputs[index];
                                if (output.AssetId == null || releaseTokenAssets)
                                    result.Add(Key(transaction.Id, index), new UnspentOutput { TransactionId = transaction.Id,
                                        OutputIndex = index, Output = output, TransactionKind = transaction.Kind });
                            }
                    }
                }
            }
            return result;
        }

        public IReadOnlyList<ValidatorStake> GetActiveValidators(IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                var pending = pendingTransactions.ToList();
                ValidateTransactions(pending);
                return StakesFromUtxo(BuildUtxo());
            }
        }

        private static void ValidateBatch(IList<Transaction> transactions, int consensusVersion = ConsensusVersion)
        {
            if (consensusVersion >= 28 && !HasCompatibleValidatorRules(transactions))
                throw new InvalidOperationException("Validated launch purchases require a launch-only block.");
            int minimum = consensusVersion >= VariableBatchVersion ? 1 : consensusVersion == BlockConfirmationVersion ? 1 : LegacyValidationsPerBlock;
            int maximum = consensusVersion >= VariableBatchVersion ? MaxOperationsPerBlock : minimum;
            if (transactions.Count < minimum || transactions.Count > maximum ||
                transactions.Select(tx => tx.Id).Distinct(StringComparer.Ordinal).Count() != transactions.Count ||
                !transactions.Select(tx => tx.Id).SequenceEqual(OrderByFeePriority(transactions).Select(tx => tx.Id), StringComparer.Ordinal))
                throw new InvalidOperationException("A block requires " + minimum + " to " + maximum + " distinct operations in fee priority order.");
        }

        private static void ValidateWalletIssuance(Transaction transaction, ref long issued)
        {
            if ((transaction.Kind != TransactionKind.WalletCreate && transaction.Kind != TransactionKind.WalletDistribution && transaction.Kind != TransactionKind.InitialLaunchCreate) || transaction.WalletDistributionId != null) return;
            long expected = issued < DistributionSupply ? WalletCreationReward : 0;
            if (transaction.Kind == TransactionKind.WalletCreate && expected > 0 && issued == DistributionSupply - WalletCreationReward)
                throw new InvalidOperationException("The final rewarded registration must automatically create the initial pool.");
            if ((transaction.Kind == TransactionKind.InitialLaunchCreate ? transaction.Outputs.Count != 5 : transaction.Outputs.Count < 1 || transaction.Outputs.Count > 2) || InitialLaunch.WalletReward(transaction) != expected)
                throw new InvalidOperationException("Invalid wallet distribution amount.");
            issued = checked(issued + expected);
        }

        /// <summary>Validated cumulative work, including the canonical genesis block.</summary>
        public BigInteger ChainWork { get { lock (sync) return blocks.Aggregate(BigInteger.Zero, (sum, b) => sum + ProofOfWork.GetBlockWork(b)); } }

        public int GetConfirmations(string transactionId)
        {
            lock (sync)
            {
                Block block = blocks.FirstOrDefault(b => b.Transactions.Any(tx => tx.Id == transactionId));
                return block == null ? 0 : blocks.Count - block.Height;
            }
        }

        public BigInteger GetConfirmationWork(string transactionId)
        {
            lock (sync)
            {
                Block block = blocks.FirstOrDefault(b => b.Transactions.Any(tx => tx.Id == transactionId));
                return block == null ? BigInteger.Zero : blocks.Skip(block.Height)
                    .Aggregate(BigInteger.Zero, (sum, b) => sum + ProofOfWork.GetBlockWork(b));
            }
        }

        public bool TryReplaceChain(IEnumerable<Block> candidateBlocks)
        {
            bool changed;
            return TrySynchronizeChain(candidateBlocks, out changed) && changed;
        }

        /// <summary>Fully validates the candidate, then chooses greater work and a deterministic tip tie-break.</summary>
        public bool TrySynchronizeChain(IEnumerable<Block> candidateBlocks, out bool changed)
        {
            changed = false;
            if (candidateBlocks == null) throw new ArgumentNullException(nameof(candidateBlocks));
            var candidate = new Blockchain(candidateBlocks);
            Block[] replacement = candidate.Blocks.ToArray();
            BigInteger candidateWork = candidate.ChainWork;
            lock (sync)
            {
                bool identical = replacement.Length == blocks.Count && replacement.Last().Hash == blocks.Last().Hash;
                if (identical) return true;
                int comparison = candidateWork.CompareTo(ChainWork);
                if (comparison < 0 || (comparison == 0 && string.CompareOrdinal(replacement.Last().Hash, blocks.Last().Hash) >= 0)) return false;
                blocks.Clear();
                blocks.AddRange(replacement);
                changed = true;
                return true;
            }
        }

        /// <summary>
        /// Validates transactions against the current UTXO set without changing the chain.
        /// The transactions are evaluated in order, so outputs created by an earlier
        /// transaction in the batch may be consumed by a later one.
        /// </summary>
        public void ValidatePendingTransactions(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            lock (sync) ValidateTransactions(transactions.ToList());
        }

        /// <summary>Orders the validation queue by fee, then arrival time and id.</summary>
        public static IReadOnlyList<Transaction> OrderByFeePriority(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            var remaining = transactions.OrderByDescending(item => item == null ? long.MinValue : item.Fee)
                .ThenBy(item => item == null ? long.MaxValue : item.TimestampUtcTicks)
                .ThenBy(item => item == null ? null : item.Id, StringComparer.Ordinal).ToList();
            var result = new List<Transaction>();
            while (remaining.Count > 0)
            {
                var unresolved = new HashSet<string>(remaining.Where(item => item != null).Select(item => item.Id), StringComparer.Ordinal);
                Transaction ready = remaining.FirstOrDefault(item => item != null && !item.Inputs.Any(input => unresolved.Contains(input.TransactionId)) &&
                    (item.TransactionApproval == null || !unresolved.Contains(item.TransactionApproval.CollateralTransactionId)));
                if (ready == null) throw new InvalidOperationException("Missing operation or cyclic transaction dependencies.");
                result.Add(ready);
                remaining.Remove(ready);
            }
            return result.ToArray();
        }

        public static IReadOnlyList<Transaction> SelectValidationBatch(IEnumerable<Transaction> transactions)
        {
            return OrderByFeePriority(transactions).Take(MaxOperationsPerBlock).ToArray();
        }

        /// <summary>
        /// Calculates the system fee from queue congestion. Each pending transaction
        /// adds one fee step and the selected priority multiplier, up to the safety cap of one coin.
        /// </summary>
        public static long CalculateAutomaticFee(int queuedTransactionCount, int priorityMultiplier)
        {
            if (queuedTransactionCount < 0) throw new ArgumentOutOfRangeException(nameof(queuedTransactionCount));
            if (priorityMultiplier <= 0) throw new ArgumentOutOfRangeException(nameof(priorityMultiplier));
            long maximumSteps = MaximumTransferFee / TransferFeeStep;
            long congestionSteps = Math.Min(maximumSteps, (long)queuedTransactionCount + 1L);
            long steps = congestionSteps > maximumSteps / priorityMultiplier
                ? maximumSteps
                : congestionSteps * priorityMultiplier;
            return steps * TransferFeeStep;
        }

        public IReadOnlyList<UnspentOutput> GetUnspentOutputs(IEnumerable<string> addresses)
        {
            return GetUnspentOutputs(addresses, Enumerable.Empty<Transaction>());
        }

        /// <summary>
        /// Returns outputs that remain spendable after applying an ordered set of
        /// pending transactions to the current chain state.
        /// </summary>
        public IReadOnlyList<UnspentOutput> GetUnspentOutputs(IEnumerable<string> addresses, IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            var wanted = new HashSet<string>(addresses ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            lock (sync)
            {
                var utxo = BuildUtxo();
                foreach (Transaction transaction in OrderByFeePriority(pendingTransactions))
                    Apply(transaction, utxo, false, IsValidTransactionApproval(transaction, utxo, blocks), ConsensusVersion, blocks.Count);
                return utxo.Values.Where(x => wanted.Contains(x.Output.OneTimeAddress)).ToArray();
            }
        }

        /// <summary>
        /// Returns confirmed outputs, excluding inputs reserved by pending operations.
        /// All pending outputs remain unavailable until their block is confirmed.
        /// </summary>
        public IReadOnlyList<UnspentOutput> GetSpendableOutputs(IEnumerable<string> addresses, IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            var pending = pendingTransactions.ToList();
            var wanted = new HashSet<string>(addresses ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            lock (sync)
            {
                ValidateTransactions(pending);
                var reserved = new HashSet<string>(pending.SelectMany(transaction => transaction.Inputs)
                    .Select(input => Key(input.TransactionId, input.OutputIndex)), StringComparer.Ordinal);
                var confirmed = BuildUtxo();
                return BuildPendingUtxo(confirmed, pending, blocks).Values.Where(item => wanted.Contains(item.Output.OneTimeAddress) &&
                    item.TransactionKind != TransactionKind.StakeLock && !item.IsPoolReserve && InitialLaunch.IsUnlocked(item, confirmed) && item.Output.AssetId == null &&
                    !reserved.Contains(Key(item.TransactionId, item.OutputIndex))).ToArray();
            }
        }

        public InitialLaunchStatus GetInitialLaunchStatus()
        {
            lock (sync) return InitialLaunch.State(BuildUtxo())?.Copy() ?? new InitialLaunchStatus();
        }

        public long GetInitialLaunchLockedBalance(IEnumerable<string> addresses)
        {
            var owned = new HashSet<string>(addresses ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            lock (sync)
            {
                var confirmed = BuildUtxo();
                return confirmed.Values.Where(item => owned.Contains(item.Output.OneTimeAddress) && !InitialLaunch.IsUnlocked(item, confirmed))
                    .Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
            }
        }

        public long GetSpendableBalance(IEnumerable<string> addresses, IEnumerable<Transaction> pendingTransactions)
        {
            return GetSpendableOutputs(addresses, pendingTransactions)
                .Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
        }

        public long GetBalance(IEnumerable<string> addresses)
        {
            return GetUnspentOutputs(addresses).Where(item => item.Output.AssetId == null).Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
        }

        /// <summary>Calculates all confirmed address balances from a single chain snapshot.</summary>
        public IReadOnlyDictionary<string, long> GetBalancesByAddress()
        {
            return GetBalancesByAddress(Enumerable.Empty<Transaction>());
        }

        /// <summary>Calculates all address balances after applying the ordered pending transactions.</summary>
        public IReadOnlyDictionary<string, long> GetBalancesByAddress(IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                var utxo = BuildUtxo();
                foreach (Transaction transaction in OrderByFeePriority(pendingTransactions))
                    Apply(transaction, utxo, false, IsValidTransactionApproval(transaction, utxo, blocks), ConsensusVersion, blocks.Count);
                var balances = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (UnspentOutput item in utxo.Values.Where(item => item.TokenRuntime == null && item.OutputIndex >= 0 && item.Output.AssetId == null))
                {
                    long balance;
                    balances.TryGetValue(item.Output.OneTimeAddress, out balance);
                    balances[item.Output.OneTimeAddress] = checked(balance + item.Output.Amount);
                }
                return balances;
            }
        }

        public IReadOnlyList<ValidatorStake> GetActiveValidators()
        {
            lock (sync)
            {
                return StakesFromUtxo(BuildUtxo());
            }
        }

        /// <summary>
        /// Returns the projected balance after applying the ordered pending
        /// transactions. This is a preview only; the confirmed balance returned by
        /// the overload without pending transactions changes only when a block is added.
        /// </summary>
        public long GetBalance(IEnumerable<string> addresses, IEnumerable<Transaction> pendingTransactions)
        {
            return GetUnspentOutputs(addresses, pendingTransactions).Where(item => item.Output.AssetId == null)
                .Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
        }

        public bool IsValid()
        {
            lock (sync)
            {
                try
                {
                    if (blocks.Count == 0 || blocks[0] == null || !IsCanonicalGenesis(blocks[0])) return false;
                    for (int i = 0; i < blocks.Count; i++)
                    {
                        var block = blocks[i];
                        if (block == null || (i > 0 && block.Rules != null) || block.Height != i || block.Hash != block.CalculateHash() || !ProofOfWork.MeetsTarget(block.Hash)) return false;
                        if (i > 0 && block.PreviousHash != blocks[i - 1].Hash) return false;
                    }
                    ValidateWholeChain(); return true;
                }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException ||
                    error is CryptographicException || error is OverflowException || error is FormatException ||
                    error is NullReferenceException || error is System.Xml.XmlException || error is KeyNotFoundException) { return false; }
            }
        }

        private void ValidateTransactions(IList<Transaction> pending)
        {
            var utxo = BuildUtxo();
            var distributions = blocks.SelectMany(block => block.Transactions).Where(tx => tx.Kind == TransactionKind.WalletDistribution).ToDictionary(tx => tx.Id, StringComparer.Ordinal);
            var counted = new HashSet<string>(blocks.SelectMany(block => block.Transactions).Where(tx => tx.Kind == TransactionKind.WalletCreate && tx.WalletDistributionId != null).Select(tx => tx.WalletDistributionId), StringComparer.Ordinal);
            var distributionAddresses = WalletDistributionAddresses();
            var ids = new HashSet<string>(blocks.SelectMany(block => block.Transactions).Select(tx => tx.Id), StringComparer.Ordinal);
            long issued = checked((long)CountWalletCreationRewards() * WalletCreationReward);
            foreach (var transaction in OrderByFeePriority(pending))
            {
                if (transaction == null || !ids.Add(transaction.Id)) throw new InvalidOperationException("Duplicate or missing pending transaction.");
                if (transaction.Kind == TransactionKind.WalletDistribution) throw new InvalidOperationException("WalletDistribution is only valid in historical version 9 blocks.");
                if (transaction.Kind == TransactionKind.WalletCreate || transaction.Kind == TransactionKind.InitialLaunchCreate)
                {
                    if (transaction.WalletDistributionId != null) ValidateWalletReceipt(transaction, distributions, counted);
                    else
                    {
                        if (transaction.Outputs == null || (transaction.Kind == TransactionKind.InitialLaunchCreate ? transaction.Outputs.Count != 5 : transaction.Outputs.Count < 1 || transaction.Outputs.Count > 2)) throw new InvalidOperationException("Invalid wallet distribution.");
                        if (!distributionAddresses.Add(InitialLaunch.WalletAddress(transaction))) throw new InvalidOperationException("Wallet already received its distribution.");
                        ValidateWalletIssuance(transaction, ref issued);
                    }
                }
                if ((transaction.Kind == TransactionKind.StakeLock || transaction.Kind == TransactionKind.StakeUnlock) && transaction.Fee != 0)
                    throw new InvalidOperationException("Self-validated collateral operations do not charge validator fees.");
                Apply(transaction, utxo, false, IsValidTransactionApproval(transaction, utxo, blocks), ConsensusVersion, blocks.Count);
            }
        }

        private void ValidateWholeChain()
        {
            var utxo = new Dictionary<string, UnspentOutput>();
            Block genesis = blocks[0];
            if (genesis.Transactions.Count != 0) throw new InvalidOperationException("The genesis block must not issue tokens.");
            long issued = 0;
            long validatorIssued = 0;
            int latestVersion = 0;
            var transactionIds = new HashSet<string>(StringComparer.Ordinal);
            var distributions = new Dictionary<string, Transaction>(StringComparer.Ordinal);
            var counted = new HashSet<string>(StringComparer.Ordinal);
            var distributionAddresses = new HashSet<string>(StringComparer.Ordinal);

            for (int blockIndex = 1; blockIndex < blocks.Count; blockIndex++)
            {
                Block block = blocks[blockIndex];
                if (block.ConsensusVersion >= LegacySignedVersion && block.Transactions.Count == 0)
                    throw new InvalidOperationException("A new block must contain validated operations or a wallet reward.");
                if (block.ConsensusVersion != 0 && block.ConsensusVersion != LegacySignedVersion && block.ConsensusVersion != LegacyHybridVersion && block.ConsensusVersion != LegacyWalletBatchVersion && block.ConsensusVersion != LegacyImmediateVersion && block.ConsensusVersion != SelfValidatedVersion && block.ConsensusVersion != ApprovalVersion && block.ConsensusVersion != ImmediateChangeVersion && block.ConsensusVersion != ImmediateTokenVersion && block.ConsensusVersion != LegacyImmediateTransferVersion && block.ConsensusVersion != BlockConfirmationVersion && block.ConsensusVersion != VariableBatchVersion && block.ConsensusVersion != PoolReserveVersion && block.ConsensusVersion != 18 && block.ConsensusVersion != 19 && block.ConsensusVersion != 20 && block.ConsensusVersion != 21 && block.ConsensusVersion != ConsensusVersion)
                    throw new InvalidOperationException("Unsupported block consensus version.");
                if (block.ConsensusVersion < latestVersion)
                    throw new InvalidOperationException("A chain cannot revert to legacy consensus.");
                latestVersion = Math.Max(latestVersion, block.ConsensusVersion);
                foreach (Transaction tx in block.Transactions)
                {
                    if (block.ConsensusVersion < LegacyImmediateVersion && (tx.Kind == TransactionKind.WalletDistribution || tx.WalletDistributionId != null))
                        throw new InvalidOperationException("Immediate wallet distributions require consensus version 9.");
                    if (block.ConsensusVersion < ApprovalVersion && tx.TransactionApproval != null)
                        throw new InvalidOperationException("Token creation approvals require consensus version 11.");
                    if (block.ConsensusVersion < PoolReserveVersion && (TokenExchange.IsPoolOperation(tx.Kind) || tx.LiquiditySwap != null))
                        throw new InvalidOperationException("Pool reserve locks require consensus version 17.");
                    if (!transactionIds.Add(tx.Id)) throw new InvalidOperationException("Duplicate transaction identifier.");
                }
                if (block.TransactionValidations != null && (block.ConsensusVersion == 0 || block.Validators == null || block.Validators.Count == 0))
                    throw new InvalidOperationException("Unexpected transaction validation proofs.");
                if (block.ConsensusVersion >= SelfValidatedVersion && (block.Validators == null || block.Validators.Count == 0))
                {
                    ValidateBatch(block.Transactions, block.ConsensusVersion);
                    if (block.Transactions.Any(tx => !IsSelfValidatedOperation(tx))) throw new InvalidOperationException("Unsigned blocks can contain only self-validated operations.");
                }
                if (block.ConsensusVersion >= LegacyWalletBatchVersion && block.ConsensusVersion < SelfValidatedVersion && (block.Validators == null || block.Validators.Count == 0) &&
                    block.Transactions.Any(tx => tx.Kind == TransactionKind.WalletCreate) && !block.Transactions.All(tx => tx.Kind == TransactionKind.WalletCreate))
                    throw new InvalidOperationException("Unsigned wallet batches cannot include other operations.");
                int regularTransactionIndex = 0;
                if (block.Validators != null && block.Validators.Count > 0)
                {
                    ValidateProofOfStakeBlock(block, blocks[blockIndex - 1].Hash, utxo, blocks.Take(blockIndex).ToList());
                    validatorIssued = checked(validatorIssued + ProofOfStake.GetBlockReward(block.Height));
                    regularTransactionIndex = 1;
                }
                else if (block.ConsensusVersion == LegacyImmediateVersion && block.Transactions.Count == 1 && block.Transactions[0].Kind == TransactionKind.WalletDistribution)
                {
                    Transaction distribution = block.Transactions[0];
                    if (!distributionAddresses.Add(distribution.Outputs[0].OneTimeAddress)) throw new InvalidOperationException("Duplicate wallet distribution address.");
                    ValidateWalletIssuance(distribution, ref issued);
                    Apply(distribution, utxo, true);
                    distributions.Add(distribution.Id, distribution);
                    regularTransactionIndex = 1;
                }
                else if (block.ConsensusVersion >= LegacyWalletBatchVersion && block.Transactions.All(tx => tx.Kind == TransactionKind.WalletCreate || (block.ConsensusVersion >= SelfValidatedVersion && IsSelfValidatedOperation(tx))))
                {
                    ValidateBatch(block.Transactions, block.ConsensusVersion);
                }
                else if (block.Transactions.Count > 0 && block.Transactions[0].Inputs.Count == 0)
                {
                    Transaction reward = block.Transactions[0];
                    if (block.ConsensusVersion >= LegacyWalletBatchVersion) throw new InvalidOperationException("Wallet creation must use a registration block.");
                    if (!IsWalletCreationReward(reward)) throw new InvalidOperationException("Invalid wallet creation reward.");
                    if (block.Transactions.Count != 1) throw new InvalidOperationException("A wallet creation reward must have its own block.");
                    distributionAddresses.Add(reward.Outputs[0].OneTimeAddress);
                    Apply(reward, utxo, true);
                    issued = checked(issued + WalletCreationReward);
                    if (issued > DistributionSupply) throw new InvalidOperationException("The wallet distribution supply was exceeded.");
                    regularTransactionIndex = 1;
                }
                for (int transactionIndex = regularTransactionIndex; transactionIndex < block.Transactions.Count; transactionIndex++)
                {
                    if (block.ConsensusVersion >= LegacySignedVersion && regularTransactionIndex == 0 &&
                        !(block.ConsensusVersion >= LegacyWalletBatchVersion && (block.Transactions[transactionIndex].Kind == TransactionKind.WalletCreate || block.Transactions[transactionIndex].Kind == TransactionKind.InitialLaunchCreate)) &&
                        block.Transactions[transactionIndex].Kind != TransactionKind.StakeLock &&
                        block.Transactions[transactionIndex].Kind != TransactionKind.StakeUnlock &&
                        !(block.ConsensusVersion >= 28 && IsFirstLaunchPurchase(block.Transactions[transactionIndex]) && block.Transactions[transactionIndex].TransactionApproval == null))
                        throw new InvalidOperationException("Transfers and token operations require signed consensus validations.");
                    if (block.ConsensusVersion < LegacyWalletBatchVersion && block.Transactions[transactionIndex].Kind == TransactionKind.WalletCreate)
                        throw new InvalidOperationException("Wallet registrations require consensus version 8.");
                    Transaction operation = block.Transactions[transactionIndex];
                    if (operation.Kind == TransactionKind.WalletDistribution) throw new InvalidOperationException("Distribution cannot be included in a regular batch.");
                    if (operation.Kind == TransactionKind.WalletCreate || operation.Kind == TransactionKind.InitialLaunchCreate)
                    {
                        if (block.ConsensusVersion >= LegacyImmediateVersion && (block.ConsensusVersion < SelfValidatedVersion || operation.WalletDistributionId != null)) ValidateWalletReceipt(operation, distributions, counted);
                        else
                        {
                            if (operation.WalletDistributionId != null) throw new InvalidOperationException("Unexpected wallet receipt in legacy consensus.");
                            if (!distributionAddresses.Add(InitialLaunch.WalletAddress(operation)) && block.ConsensusVersion >= SelfValidatedVersion) throw new InvalidOperationException("Duplicate wallet distribution address.");
                        }
                    }
                    if (block.ConsensusVersion >= SelfValidatedVersion && (operation.Kind == TransactionKind.StakeLock || operation.Kind == TransactionKind.StakeUnlock) && operation.Fee != 0)
                        throw new InvalidOperationException("Self-validated collateral operations cannot charge validator fees.");
                    if (block.ConsensusVersion < SelfValidatedVersion && (operation.Kind == TransactionKind.StakeLock || operation.Kind == TransactionKind.StakeUnlock) && operation.Fee < TransferFeeStep)
                        throw new InvalidOperationException("Historical collateral operations require their original minimum fee.");
                    ValidateWalletIssuance(operation, ref issued);
                    Apply(block.Transactions[transactionIndex], utxo, false, block.ConsensusVersion >= ApprovalVersion && RequiresLockedTokenApproval(operation), block.ConsensusVersion, block.Height);
                }
            }
            if (issued > MaximumSupply || validatorIssued > ProofOfStake.MaximumSupply) throw new InvalidOperationException("Invalid supply.");
        }

        private static void ValidateProofOfStakeBlock(Block block, string previousHash, IDictionary<string, UnspentOutput> utxo, IList<Block> history)
        {
            if (block.Transactions.Count == 0) throw new InvalidOperationException("A proof-of-stake block must contain its reward.");
            if (block.Transactions.Skip(1).Any(tx => IsFirstLaunchPurchase(tx) && tx.TransactionApproval == null)) throw new InvalidOperationException("A rewarded first launch purchase requires a signed validator approval.");
            BlockValidator[] records = block.Validators.ToArray();
            var launchBlock = IsLaunchValidationBatch(block.Transactions.Skip(1));
            bool launchRegistration = block.ConsensusVersion >= 30 && IsLaunchRegistrationBatch(block.Transactions.Skip(1), utxo);
            if (records.Length < (launchBlock || launchRegistration ? 1 : 2) || records.Any(item => item == null) || records.Count(item => item.IsCreator) != 1)
                throw new InvalidOperationException("Invalid proof-of-stake validator proof.");
            var stakes = records.Select(item => new ValidatorStake(item.ValidatorId, item.RewardAddress, item.LockedAmount,
                item.OwnedAddresses ?? new List<string> { item.RewardAddress }, item.PublicKey, null)).ToArray();
            var collateral = block.ConsensusVersion >= SelfValidatedVersion && block.ConsensusVersion < BlockConfirmationVersion
                ? BuildLegacyImmediateUtxo(utxo, block.Transactions.Skip(1), history, null, block.ConsensusVersion >= ImmediateChangeVersion,
                    block.ConsensusVersion >= ImmediateTokenVersion, block.ConsensusVersion >= LegacyImmediateTransferVersion) : utxo;
            var launchValidators = launchBlock ? ConsensusValidators(block.Transactions.Skip(1), collateral, history).ToDictionary(item => item.ValidatorId) : null;
            foreach (BlockValidator record in records)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.PublicKey) ||
                    record.ValidatorId != Crypto.Sha256(record.PublicKey) ||
                    !ProofOfStake.VerifyVote(record.PublicKey, CreateVotePayload(block), record.VoteSignature))
                    throw new InvalidOperationException("Invalid individual validator vote signature.");
                ValidatorStake launchValidator;
                bool collateralExists = launchBlock ? launchValidators.TryGetValue(record.ValidatorId, out launchValidator) &&
                    launchValidator.RewardAddress == record.RewardAddress && launchValidator.LockedAmount == record.LockedAmount :
                    StakesFromUtxo(collateral, block.ConsensusVersion).Any(item =>
                    item.ValidatorId == record.ValidatorId && item.RewardAddress == record.RewardAddress &&
                    item.LockedAmount == record.LockedAmount);
                if (!collateralExists) throw new InvalidOperationException("The validator collateral is not globally locked on chain.");
            }
            Transaction[] transfers = block.Transactions.Skip(1).ToArray();
            if (block.ConsensusVersion >= LegacySignedVersion) ValidateBatch(transfers, block.ConsensusVersion);
            bool excludeAllParticipants = block.ConsensusVersion < SelfValidatedVersion || block.ConsensusVersion >= BlockConfirmationVersion;
            ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(launchBlock ? ConsensusValidators(transfers, collateral, history) : StakesFromUtxo(collateral, block.ConsensusVersion), transfers, excludeAllParticipants).ToArray();
            if (!ValidValidatorSet(stakes, globallyEligible, launchBlock || launchRegistration))
                throw new InvalidOperationException("The validator proof does not contain the global eligible collateral set.");
            if (ExcludeTransactionParticipants(stakes, transfers, excludeAllParticipants).Count() != stakes.Length)
                throw new InvalidOperationException("A transfer sender or receiver cannot create or confirm its block.");
            ValidatorStake expectedCreator = ProofOfStake.SelectCreator(stakes, previousHash, block.Height);
            BlockValidator recordedCreator = records.Single(item => item.IsCreator);
            if (recordedCreator.ValidatorId != expectedCreator.ValidatorId)
                throw new InvalidOperationException("The recorded validator was not selected to create this block.");
            IReadOnlyList<ValidatorReward> expected = ProofOfStake.DistributeReward(block.Height, expectedCreator,
                stakes.Where(item => item.ValidatorId != expectedCreator.ValidatorId));
            Transaction reward = block.Transactions[0];
            if (block.ConsensusVersion >= LegacySignedVersion)
            {
                ValidateTransactionFees(block, transfers, stakes, previousHash, expected, utxo, history);
                if (block.ConsensusVersion < ApprovalVersion || reward.Outputs.Count > 0) Apply(reward, utxo, true);
                return;
            }
            long fees = transfers.Aggregate(0L, (total, transaction) => checked(total + transaction.Fee));
            int expectedOutputCount = expected.Count == 0 && fees > 0 ? 1 : expected.Count;
            if (reward.Id != reward.CalculateId() || reward.Inputs.Count != 0 || reward.Outputs.Count != expectedOutputCount)
                throw new InvalidOperationException("Invalid validator reward transaction.");
            if (expected.Count == 0 && fees > 0 &&
                (reward.Outputs[0].Amount != fees || reward.Outputs[0].OneTimeAddress != expectedCreator.RewardAddress))
                throw new InvalidOperationException("The block creator did not receive the transaction fees.");
            for (int index = 0; index < expected.Count; index++)
                if (reward.Outputs[index].Amount != checked(expected[index].Amount + (expected[index].IsCreator ? fees : 0)) || reward.Outputs[index].OneTimeAddress != expected[index].RewardAddress)
                    throw new InvalidOperationException("The validator reward distribution is incorrect.");
            Apply(reward, utxo, true);
        }

        /// <summary>Approves one movement using confirmed collateral. Its fee is paid in the block.</summary>
        public TransactionApproval CreateTransactionApproval(Transaction transaction, IEnumerable<Transaction> pendingTransactions,
            IEnumerable<ValidatorStake> localValidators)
        {
            if (transaction == null || (!RequiresLockedTokenApproval(transaction) && transaction.Kind != TransactionKind.InitialLaunchPurchase)) throw new ArgumentException("A movement requiring a fee is required.", nameof(transaction));
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            if (localValidators == null) throw new ArgumentNullException(nameof(localValidators));
            lock (sync)
            {
                var pending = pendingTransactions.Where(tx => tx.Id != transaction.Id).Concat(new[] { transaction }).ToList();
                ValidateTransactions(pending);
                var collateral = BuildPendingUtxo(BuildUtxo(), pending, blocks, transaction.Id);
                ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(ConsensusValidators(new[] { transaction }, collateral, blocks), new[] { transaction }, false).ToArray();
                ValidatorStake[] owned = localValidators.Where(local => globallyEligible.Any(global =>
                    global.ValidatorId == local.ValidatorId && (global.PublicKey == null || global.PublicKey == local.PublicKey) &&
                    global.RewardAddress == local.RewardAddress && global.LockedAmount == local.LockedAmount)).ToArray();
                if (owned.Length == 0) return null;
                Block anchor = blocks.Last();
                ValidatorStake validator = SelectTransactionValidator(owned, transaction, anchor.Hash, anchor.Height + 1);
                UnspentOutput locked = collateral.Values.First(item => {
                    if (transaction.Kind != TransactionKind.InitialLaunchPurchase)
                        return IsValidatorCollateral(item, collateral, ConsensusVersion) &&
                            CollateralIdentity(item) == validator.ValidatorId && item.ValidatorRewardAddress == validator.RewardAddress;
                    var itemStake = LaunchCollateral(item, collateral, blocks);
                    return itemStake != null && itemStake.ValidatorId == validator.ValidatorId && itemStake.RewardAddress == validator.RewardAddress;
                });
                var validation = new TransactionApproval { AnchorHeight = anchor.Height, AnchorHash = anchor.Hash,
                    CollateralTransactionId = locked.TransactionId, CollateralOutputIndex = locked.OutputIndex,
                    Proof = new TransactionValidation { TransactionId = transaction.Id, ValidatorId = validator.ValidatorId,
                        RewardAddress = validator.RewardAddress, PublicKey = validator.PublicKey } };
                validation.Proof.Signature = validator.CreateVote(CreateTransactionApprovalPayload(transaction, validation));
                var approved = new Transaction { Id = transaction.Id, TimestampUtcTicks = transaction.TimestampUtcTicks,
                    Inputs = transaction.Inputs, Outputs = transaction.Outputs, Fee = transaction.Fee, Kind = transaction.Kind,
                    InitialLaunchIdentity = transaction.InitialLaunchIdentity, Launch = transaction.Launch?.Copy(),
                    PoolParticipation = transaction.PoolParticipation?.Copy(), LiquiditySwap = transaction.LiquiditySwap, LiquidityChange = transaction.LiquidityChange,
                    PoolBurnPolicy = transaction.PoolBurnPolicy?.Copy(), Token = transaction.Token, TokenTransferTerms = transaction.TokenTransferTerms, TokenCommand = transaction.TokenCommand, TokenAuthorizations = transaction.TokenAuthorizations, TransactionApproval = validation };
                var candidate = pending.Where(tx => tx.Id != transaction.Id).Concat(new[] { approved }).ToList();
                if (!IsValidTransactionApproval(approved, BuildPendingUtxo(BuildUtxo(), candidate, blocks, transaction.Id), blocks)) return null;
                return validation;
            }
        }

        public bool HasValidTransactionApproval(Transaction transaction, IEnumerable<Transaction> pendingTransactions)
        {
            if (!RequiresLockedTokenApproval(transaction) || transaction.TransactionApproval == null) return false;
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                try
                {
                    var pending = pendingTransactions.Where(tx => tx.Id != transaction.Id).Concat(new[] { transaction }).ToList();
                    ValidateTransactions(pending);
                    ValidateTransactionApproval(transaction, BuildPendingUtxo(BuildUtxo(), pending, blocks, transaction.Id), blocks);
                    return true;
                }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is FormatException ||
                    error is CryptographicException || error is OverflowException) { return false; }
            }
        }

        /// <summary>Selects up to 20 approved operations; the producer controls the waiting deadline.</summary>
        public IReadOnlyList<Transaction> SelectApprovedValidationBatch(IEnumerable<Transaction> pendingTransactions)
        {
            return SelectValidationBatch(GetApprovedTransactions(pendingTransactions));
        }

        /// <summary>Returns the entire ready queue, excluding unapproved movements and their dependants.</summary>
        public IReadOnlyList<Transaction> GetApprovedTransactions(IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                Transaction[] pending = OrderByFeePriority(pendingTransactions).ToArray();
                ValidateTransactions(pending);
                var blocked = new HashSet<string>(StringComparer.Ordinal);
                var ready = new List<Transaction>();
                foreach (Transaction transaction in pending)
                {
                    if ((RequiresLockedTokenApproval(transaction) && !HasValidTransactionApproval(transaction, pending)) ||
                        transaction.Inputs.Any(input => blocked.Contains(input.TransactionId))) blocked.Add(transaction.Id);
                    else ready.Add(transaction);
                }
                return ready.ToArray();
            }
        }

        private static string CreateTransactionApprovalPayload(Transaction transaction, TransactionApproval validation)
        {
            return NetworkId + "|transaction-approval-v11|" + transaction.Id + "|" +
                validation.AnchorHeight.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + validation.AnchorHash + "|" +
                validation.CollateralTransactionId + "|" + validation.CollateralOutputIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                validation.Proof.RewardAddress;
        }

        private static void ValidateTransactionApproval(Transaction transaction, IDictionary<string, UnspentOutput> collateral, IList<Block> history, int consensusVersion = ConsensusVersion)
        {
            TransactionApproval validation = transaction.TransactionApproval;
            TransactionValidation proof = validation?.Proof;
            UnspentOutput locked;
            if (!RequiresLockedTokenApproval(transaction) || validation == null || proof == null ||
                transaction.Inputs.Any(input => !collateral.ContainsKey(Key(input.TransactionId, input.OutputIndex))) ||
                validation.AnchorHeight < 0 || validation.AnchorHeight >= history.Count ||
                history[validation.AnchorHeight].Hash != validation.AnchorHash ||
                !collateral.TryGetValue(Key(validation.CollateralTransactionId, validation.CollateralOutputIndex), out locked))
                throw new InvalidOperationException("A movement requires a valid approval backed by locked tokens.");
            var validator = ApprovalCollateral(transaction, locked, collateral, history, consensusVersion);
            if (validator == null || proof.TransactionId != transaction.Id || (validator.PublicKey != null && proof.PublicKey != validator.PublicKey) ||
                proof.RewardAddress != validator.RewardAddress || proof.ValidatorId != validator.ValidatorId || proof.ValidatorId != Crypto.Sha256(proof.PublicKey))
                throw new InvalidOperationException("The approval collateral is not eligible for this operation.");
            if (!ExcludeTransactionParticipants(new[] { validator }, new[] { transaction }, false).Any() ||
                !ProofOfStake.VerifyVote(proof.PublicKey, CreateTransactionApprovalPayload(transaction, validation), proof.Signature))
                throw new InvalidOperationException("Invalid movement validator signature or participation.");
        }

        private static bool IsValidTransactionApproval(Transaction transaction, IDictionary<string, UnspentOutput> utxo, IList<Block> history)
        {
            if (transaction.TransactionApproval == null) return false;
            try { ValidateTransactionApproval(transaction, utxo, history); return true; }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is FormatException ||
                error is CryptographicException || error is OverflowException) { return false; }
        }

        private static ValidatorStake SelectTransactionValidator(IEnumerable<ValidatorStake> validators,
            Transaction transaction, string previousHash, int height)
        {
            return ProofOfStake.SelectCreator(validators, "transaction-validation-v4|" + previousHash + "|" + transaction.Id, height);
        }

        private static string CreateTransactionValidationPayload(Transaction transaction, string previousHash, int height, string rewardAddress)
        {
            return NetworkId + "|transaction-validation-v4|" + previousHash + "|" +
                height.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + transaction.Id + "|" +
                transaction.Fee.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + rewardAddress;
        }

        private static void ValidateTransactionFees(Block block, Transaction[] transactions, ValidatorStake[] stakes,
            string previousHash, IReadOnlyList<ValidatorReward> rewards, IDictionary<string, UnspentOutput> collateral, IList<Block> history)
        {
            Transaction reward = block.Transactions[0];
            if (block.ConsensusVersion >= ApprovalVersion && (reward.Kind != TransactionKind.Transfer || reward.Fee != 0 || reward.Token != null ||
                reward.PoolBurnEnabled || reward.TokenTransferTerms != null || reward.TokenCommand != null || reward.TokenAuthorizations != null || reward.Outputs.Any(output => output.UnlockHeight != 0) ||
                reward.TransactionApproval != null || reward.WalletDistributionId != null || reward.ValidatorPublicKey != null ||
                reward.ValidatorRewardAddress != null || reward.ValidatorOwnedAddresses != null))
                throw new InvalidOperationException("Invalid scheduled block reward.");
            Transaction[] signedTransactions = transactions.Where(tx => block.ConsensusVersion >= SelfValidatedVersion ? !IsSelfValidatedOperation(tx) : block.ConsensusVersion < LegacyWalletBatchVersion || tx.Kind != TransactionKind.WalletCreate).ToArray();
            if (block.TransactionValidations == null || block.TransactionValidations.Count != signedTransactions.Length ||
                reward.Id != reward.CalculateId() || reward.Inputs.Count != 0 || reward.Outputs.Count != rewards.Count + (block.ConsensusVersion >= ApprovalVersion ? 0 : signedTransactions.Length))
                throw new InvalidOperationException("Invalid transaction validation proofs or fee settlement.");
            for (int index = 0; index < rewards.Count; index++)
                if (reward.Outputs[index].Amount != rewards[index].Amount || reward.Outputs[index].OneTimeAddress != rewards[index].RewardAddress)
                    throw new InvalidOperationException("The block reward must not include transaction fees.");
            if (block.ConsensusVersion >= ApprovalVersion)
            {
                var replay = new Dictionary<string, UnspentOutput>(collateral, StringComparer.Ordinal);
                foreach (Transaction operation in transactions)
                {
                    if (RequiresLockedTokenApproval(operation))
                    {
                        ValidateTransactionApproval(operation, replay, history, block.ConsensusVersion);
                        TransactionValidation proof = block.TransactionValidations.SingleOrDefault(item => item.TransactionId == operation.Id);
                        if (proof == null || proof.ProofPayload() != operation.TransactionApproval.Proof.ProofPayload())
                            throw new InvalidOperationException("The fee must belong to the approving locked validator.");
                    }
                    Apply(operation, replay, false, RequiresLockedTokenApproval(operation), block.ConsensusVersion, block.Height);
                }
                return;
            }
            for (int index = 0; index < signedTransactions.Length; index++)
            {
                Transaction transaction = signedTransactions[index];
                TransactionValidation proof = block.TransactionValidations[index];
                ValidatorStake validator = SelectTransactionValidator(stakes, transaction, previousHash, block.Height);
                if (proof == null || proof.TransactionId != transaction.Id || proof.ValidatorId != validator.ValidatorId ||
                    proof.PublicKey != validator.PublicKey || proof.RewardAddress != validator.RewardAddress ||
                    !ProofOfStake.VerifyVote(proof.PublicKey, CreateTransactionValidationPayload(transaction, previousHash, block.Height, proof.RewardAddress), proof.Signature))
                    throw new InvalidOperationException("Invalid signed transaction validation.");
                TransactionOutput payment = reward.Outputs[rewards.Count + index];
                if (payment.Amount != transaction.Fee || payment.OneTimeAddress != proof.RewardAddress)
                    throw new InvalidOperationException("A transaction fee must be paid to its transaction validator.");
            }
        }

        private static IEnumerable<ValidatorStake> ExcludeTransactionParticipants(
            IEnumerable<ValidatorStake> validators,
            IEnumerable<Transaction> transactions, bool includeSelfValidated = true)
        {
            var endpoints = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transaction transaction in transactions)
            {
                if (!includeSelfValidated && IsSelfValidatedOperation(transaction)) continue;
                foreach (TransactionInput input in transaction.Inputs)
                    if (!string.IsNullOrWhiteSpace(input.PublicKey)) endpoints.Add(Crypto.Sha256(input.PublicKey));
                foreach (TransactionOutput output in transaction.Outputs)
                    if (!string.IsNullOrWhiteSpace(output.OneTimeAddress)) endpoints.Add(output.OneTimeAddress);
            }
            return validators.Where(validator => !validator.OwnedAddresses.Any(endpoints.Contains));
        }

        private static bool IsValidatorCollateral(UnspentOutput item, IDictionary<string, UnspentOutput> utxo, int version)
        {
            return item.TransactionKind == TransactionKind.StakeLock ||
                version >= LaunchValidatorVersion && item.Output.AssetId == null && item.Output.Amount > 0 &&
                item.Output.InitialLaunchLocked && !InitialLaunch.IsUnlocked(item, utxo) &&
                (item.OutputIndex == 2 && !string.IsNullOrEmpty(item.ValidatorPublicKey) ||
                 version >= 27 && item.OutputIndex == 4 && item.Output.Amount == WalletCreationReward);
        }

        // The first wallet registration commits its address rather than a public key.
        // Its validator must reveal the matching key in a signed vote/approval.
        private static string CollateralIdentity(UnspentOutput item) =>
            item.ValidatorPublicKey == null ? item.Output.OneTimeAddress : Crypto.Sha256(item.ValidatorPublicKey);

        private static ValidatorStake[] StakesFromUtxo(IDictionary<string, UnspentOutput> utxo, int version = ConsensusVersion)
        {
            return utxo.Values.Where(item => IsValidatorCollateral(item, utxo, version))
                .GroupBy(CollateralIdentity, StringComparer.Ordinal)
                .Select(group => {
                    var primary = group.FirstOrDefault(item => item.TransactionKind == TransactionKind.StakeLock) ?? group.First();
                    return new ValidatorStake(group.Key, primary.ValidatorRewardAddress,
                        group.Aggregate(0L, (sum, item) => checked(sum + item.Output.Amount)),
                        group.SelectMany(item => item.ValidatorOwnedAddresses).Distinct(StringComparer.Ordinal),
                        group.Select(item => item.ValidatorPublicKey).FirstOrDefault(key => key != null), null);
                }).OrderBy(item => item.ValidatorId, StringComparer.Ordinal).ToArray();
        }

        private static bool SameStakeSet(IEnumerable<ValidatorStake> left, IEnumerable<ValidatorStake> right)
        {
            string[] first = left.Select(item => item.ValidatorId + "|" + item.RewardAddress + "|" + item.LockedAmount)
                .OrderBy(item => item, StringComparer.Ordinal).ToArray();
            string[] second = right.Select(item => item.ValidatorId + "|" + item.RewardAddress + "|" + item.LockedAmount)
                .OrderBy(item => item, StringComparer.Ordinal).ToArray();
            return first.SequenceEqual(second, StringComparer.Ordinal);
        }

        private static bool IsWalletCreationReward(Transaction transaction)
        {
            return transaction != null && transaction.Id == transaction.CalculateId() && transaction.Inputs.Count == 0 &&
                transaction.Outputs.Count == 1 && transaction.Outputs[0].Amount == WalletCreationReward &&
                !string.IsNullOrWhiteSpace(transaction.Outputs[0].OneTimeAddress);
        }

        private Dictionary<string, UnspentOutput> BuildUtxo()
        {
            var result = new Dictionary<string, UnspentOutput>(StringComparer.Ordinal);
            foreach (var block in blocks)
                foreach (var transaction in block.Transactions)
                {
                    foreach (var input in transaction.Inputs) result.Remove(Key(input.TransactionId, input.OutputIndex));
                    AddOutputs(transaction, result);
                    if (block.ConsensusVersion >= ApprovalVersion && RequiresLockedTokenApproval(transaction)) AddValidationFee(transaction, result);
                }
            return result;
        }

        private static void Apply(Transaction transaction, IDictionary<string, UnspentOutput> utxo, bool allowMint, bool includeValidationFee = false, int consensusVersion = ConsensusVersion, int height = 0)
        {
            if (transaction == null || transaction.Id != transaction.CalculateId() || (transaction.Outputs.Count == 0 && transaction.Kind != TransactionKind.TokenManage)) throw new InvalidOperationException("Invalid transaction.");
            if (transaction.TransactionApproval != null && !RequiresLockedTokenApproval(transaction))
                throw new InvalidOperationException("Only fee-paying movements may carry an approval.");
            if (transaction.Launch != null && !TokenLaunch.IsOperation(transaction.Kind))
                throw new InvalidOperationException("Launch metadata is only valid on launch operations.");
            if (consensusVersion < 21 && (transaction.Launch != null || TokenLaunch.IsOperation(transaction.Kind)))
                throw new InvalidOperationException("Launch pools require consensus v21.");
            if (consensusVersion < 19 && (transaction.PoolParticipation != null || transaction.LiquidityChange != null ||
                transaction.Kind == TransactionKind.LiquidityAdd || transaction.Kind == TransactionKind.LiquidityRemove))
                throw new InvalidOperationException("Public liquidity requires consensus v19.");
            if ((transaction.PoolParticipation != null || transaction.LiquidityChange != null) && !TokenExchange.IsPoolOperation(transaction.Kind))
                throw new InvalidOperationException("Liquidity metadata is only valid on pool operations.");
            if (transaction.LiquiditySwap != null && transaction.Kind != TransactionKind.LiquiditySwap)
                throw new InvalidOperationException("Swap terms are only valid on pool swaps.");
            if (transaction.WalletDistributionId != null && transaction.Kind != TransactionKind.WalletCreate)
                throw new InvalidOperationException("Only wallet receipts may reference a distribution.");
            InitialLaunch.ValidateEnvelope(transaction, utxo, consensusVersion);
            TokenGovernance.Validate(transaction, utxo, height, consensusVersion);
            if (transaction.Kind == TransactionKind.WalletCreate || transaction.Kind == TransactionKind.WalletDistribution)
            {
                if (allowMint != (transaction.Kind == TransactionKind.WalletDistribution) || transaction.Inputs.Count != 0 || transaction.Fee != 0 || transaction.Outputs.Count < 1 || transaction.Outputs.Count > 2 ||
                    (transaction.Outputs[0].Amount != WalletCreationReward && transaction.Outputs[0].Amount != 0) ||
                    (transaction.WalletDistributionId != null && transaction.Outputs[0].Amount != 0) ||
                    string.IsNullOrWhiteSpace(transaction.Outputs[0].OneTimeAddress) || transaction.Outputs[0].AssetId != null ||
                    transaction.Token != null || transaction.ValidatorPublicKey != null || transaction.ValidatorRewardAddress != null || transaction.ValidatorOwnedAddresses != null)
                    throw new InvalidOperationException("Invalid fee-free wallet registration.");
                if (consensusVersion < 23 && transaction.Outputs.Count == 2) throw new InvalidOperationException("USDC registration rewards require consensus v23.");
                InitialLaunch.ValidateWalletUsdcReward(transaction, utxo);
                AddOutputs(transaction, utxo);
                return;
            }
            if (transaction.Inputs.Count == 0 && !allowMint && transaction.Kind != TransactionKind.InitialLaunchCreate) throw new InvalidOperationException("Minting is only allowed for genesis and mining rewards.");
            if (!Enum.IsDefined(typeof(TransactionKind), transaction.Kind)) throw new InvalidOperationException("Unknown transaction kind.");
            TokenExchange.ValidatePoolOperation(transaction, utxo, height);
            bool poolOperation = TokenExchange.IsPoolOperation(transaction.Kind);
            bool tokenOperation = transaction.Kind == TransactionKind.TokenCreate || transaction.Kind == TransactionKind.TokenTransfer || transaction.Kind == TransactionKind.TokenManage || poolOperation;
            if (allowMint && (transaction.Kind != TransactionKind.Transfer || transaction.Token != null || transaction.Outputs.Any(o => o.AssetId != null)))
                throw new InvalidOperationException("Rewards must contain only POVIX.");
            if (transaction.Kind == TransactionKind.TokenCreate)
            {
                if (transaction.Token == null || transaction.Inputs.Count == 0) throw new InvalidOperationException("Missing token definition.");
                transaction.Token.Validate();
                if (transaction.Token.Id != TokenDefinition.IdFor(transaction.Inputs[0])) throw new InvalidOperationException("Invalid token identifier.");
            }
            else if (transaction.Token != null) throw new InvalidOperationException("Only token creation may declare metadata.");
            var tokenInputs = new Dictionary<string, long>(StringComparer.Ordinal);
            var tokenOutputs = new Dictionary<string, long>(StringComparer.Ordinal);
            long inputTotal = 0;
            var used = new HashSet<string>();
            byte[] payload = Encoding.UTF8.GetBytes(transaction.SigningPayload());
            foreach (var input in transaction.Inputs)
            {
                string id = Key(input.TransactionId, input.OutputIndex);
                UnspentOutput source;
                if (!used.Add(id) || !utxo.TryGetValue(id, out source)) throw new InvalidOperationException("Missing or already spent input.");
                if (source.TransactionKind == TransactionKind.StakeLock && transaction.Kind != TransactionKind.StakeUnlock)
                    throw new InvalidOperationException("Locked validator collateral requires an unlock transaction.");
                if (source.TransactionKind != TransactionKind.StakeLock && transaction.Kind == TransactionKind.StakeUnlock)
                    throw new InvalidOperationException("An unlock transaction may only spend validator collateral.");
                if (Crypto.Sha256(input.PublicKey) != source.Output.OneTimeAddress) throw new InvalidOperationException("Input does not own the output.");
                // Validated swaps and proportional liquidity changes may consume paired reserves
                // without provider signatures. Free funding and closure still require ownership.
                bool automaticReserve = source.IsPoolReserve &&
                    (consensusVersion >= 18 && transaction.Kind == TransactionKind.LiquiditySwap ||
                     consensusVersion >= 19 && (transaction.Kind == TransactionKind.LiquidityAdd || transaction.Kind == TransactionKind.LiquidityRemove) ||
                     consensusVersion >= 21 && TokenLaunch.IsStep(transaction.Kind) ||
                     consensusVersion >= 22 && transaction.Kind == TransactionKind.InitialLaunchPurchase);
                if (!automaticReserve || !string.IsNullOrEmpty(input.Signature))
                {
                    if (string.IsNullOrEmpty(input.Signature)) throw new InvalidOperationException("Missing ownership signature.");
                    using (var rsa = new RSACryptoServiceProvider())
                    {
                        rsa.PersistKeyInCsp = false;
                        rsa.FromXmlString(input.PublicKey);
                        if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID(Crypto.SignatureHashAlgorithm), Convert.FromBase64String(input.Signature))) throw new InvalidOperationException("Invalid signature.");
                    }
                }
                if (source.Output.AssetId == null) inputTotal = checked(inputTotal + source.Output.Amount);
                else
                {
                    if (transaction.Kind != TransactionKind.TokenTransfer && transaction.Kind != TransactionKind.TokenManage && !poolOperation) throw new InvalidOperationException("Token inputs require a token transfer.");
                    AddAssetAmount(tokenInputs, source.Output.AssetId, source.Output.Amount);
                }
            }
            long outputTotal = 0;
            foreach (var output in transaction.Outputs)
            {
                if ((output.Amount <= 0 && !(transaction.Kind == TransactionKind.InitialLaunchCreate && ReferenceEquals(output, transaction.Outputs[1]) && output.Amount == 0)) || string.IsNullOrWhiteSpace(output.OneTimeAddress)) throw new InvalidOperationException("Invalid output.");
                if (output.AssetId == null) outputTotal = checked(outputTotal + output.Amount);
                else
                {
                    if (!tokenOperation || output.AssetId.Length != 64 || output.AssetId.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                        throw new InvalidOperationException("Invalid asset output.");
                    if (output.Amount > 0) AddAssetAmount(tokenOutputs, output.AssetId, output.Amount);
                }
            }
            if (transaction.Kind == TransactionKind.TokenCreate)
            {
                if (tokenOutputs.Count != 1 || !tokenOutputs.ContainsKey(transaction.Token.Id) || tokenOutputs[transaction.Token.Id] != transaction.Token.Supply)
                    throw new InvalidOperationException("Token creation must issue exactly the declared supply.");
            }
            if (transaction.Kind == TransactionKind.TokenManage)
            {
                string asset = transaction.TokenCommand.TokenId;
                long before, after;
                tokenInputs.TryGetValue(asset, out before); tokenOutputs.TryGetValue(asset, out after);
                long delta = transaction.TokenCommand.Operation == "mint" ? transaction.TokenCommand.Amount :
                    transaction.TokenCommand.Operation == "burn" ? -transaction.TokenCommand.Amount : 0;
                if (tokenInputs.Keys.Concat(tokenOutputs.Keys).Any(id => id != asset) || checked(before + delta) != after)
                    throw new InvalidOperationException("Token command does not conserve the declared amount.");
            }
            if (transaction.Kind == TransactionKind.TokenTransfer) TokenGovernance.ValidateTransferConservation(transaction, utxo, tokenInputs, tokenOutputs);
            if (transaction.Kind == TransactionKind.InitialLaunchCreate && (tokenInputs.Count != 0 || tokenOutputs.Count != 2 ||
                InitialLaunch.FundingTokens().Any(token => !tokenOutputs.ContainsKey(token.Id) || tokenOutputs[token.Id] != (token.Id == InitialLaunch.UsdcTokenId ? InitialLaunch.WalletUsdcReward : token.Supply))))
                throw new InvalidOperationException("Initial launch must issue 100 local USDC and one million local USDT.");
            if (poolOperation && transaction.Kind != TransactionKind.InitialLaunchCreate &&
                (tokenInputs.Count == 0 || tokenInputs.Count != tokenOutputs.Count ||
                 tokenInputs.Any(pair => !tokenOutputs.ContainsKey(pair.Key) || checked(tokenOutputs[pair.Key] + TokenGovernance.SwapBurn(transaction, pair.Key)) != pair.Value)))
                throw new InvalidOperationException("Token transfers must conserve each asset independently.");
            if (transaction.Fee < 0) throw new InvalidOperationException("A transaction fee cannot be negative.");
            if (transaction.Kind == TransactionKind.StakeLock)
            {
                if (string.IsNullOrWhiteSpace(transaction.ValidatorPublicKey) || string.IsNullOrWhiteSpace(transaction.ValidatorRewardAddress) ||
                    transaction.ValidatorOwnedAddresses == null || transaction.ValidatorOwnedAddresses.Count == 0 ||
                    !transaction.ValidatorOwnedAddresses.Contains(transaction.ValidatorRewardAddress, StringComparer.Ordinal) ||
                    transaction.Outputs.Count == 0 || transaction.Outputs[0].OneTimeAddress != Crypto.Sha256(transaction.ValidatorPublicKey))
                    throw new InvalidOperationException("Invalid validator collateral transaction.");
                if (utxo.Values.Any(item => item.TransactionKind == TransactionKind.StakeLock &&
                    item.ValidatorPublicKey == transaction.ValidatorPublicKey))
                    throw new InvalidOperationException("This validator already has globally locked collateral.");
            }
            else if (!string.IsNullOrEmpty(transaction.ValidatorPublicKey) || !string.IsNullOrEmpty(transaction.ValidatorRewardAddress) ||
                transaction.ValidatorOwnedAddresses != null)
                throw new InvalidOperationException("Validator metadata is only valid on collateral transactions.");
            if (!allowMint && transaction.Kind != TransactionKind.StakeLock && transaction.Kind != TransactionKind.StakeUnlock && transaction.Kind != TransactionKind.InitialLaunchCreate && transaction.Fee < TransferFeeStep)
                throw new InvalidOperationException("The minimum transaction fee is one atomic unit.");
            long initialIssuance = transaction.Kind == TransactionKind.InitialLaunchCreate ? InitialLaunch.ReserveSupply + WalletCreationReward : 0;
            if (!allowMint && checked(inputTotal + initialIssuance) != checked(outputTotal + transaction.Fee))
                throw new InvalidOperationException("Inputs must equal outputs plus the transaction fee.");
            if (allowMint && transaction.Fee != 0) throw new InvalidOperationException("A reward transaction cannot declare a fee.");
            foreach (string id in used) utxo.Remove(id);
            AddOutputs(transaction, utxo);
            if (includeValidationFee) AddValidationFee(transaction, utxo);
        }

        private static void AddAssetAmount(IDictionary<string, long> amounts, string assetId, long amount)
        {
            long previous;
            amounts.TryGetValue(assetId, out previous);
            amounts[assetId] = checked(previous + amount);
        }

        public IReadOnlyList<TokenDefinition> GetTokens()
        {
            lock (sync) return BuildUtxo().Values.Where(item => item.TokenRuntime != null).Select(item => {
                var state = item.TokenRuntime; var token = state.Token;
                return new TokenDefinition { Id = token.Id, Name = token.Name, Symbol = token.Symbol,
                    Decimals = token.Decimals, Supply = state.Supply, Policy = token.Policy?.Copy() };
            }).ToArray();
        }

        public TokenRuntime GetTokenRuntime(string tokenId)
        {
            lock (sync)
            {
                var state = TokenGovernance.State(BuildUtxo(), tokenId);
                if (state == null) return null;
                var token = state.Token;
                return new TokenRuntime { Token = new TokenDefinition { Id = token.Id, Name = token.Name, Symbol = token.Symbol,
                    Decimals = token.Decimals, Supply = token.Supply, Policy = token.Policy?.Copy() },
                    Supply = state.Supply, Issued = state.Issued, Paused = state.Paused, Revision = state.Revision };
            }
        }

        public IReadOnlyList<Transaction> GetApprovedTokenCreations(IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                var pending = pendingTransactions.ToArray();
                ValidateTransactions(pending.ToList());
                return pending.Where(tx => tx.Kind == TransactionKind.TokenCreate && HasValidTransactionApproval(tx, pending)).ToArray();
            }
        }

        public IReadOnlyList<TokenDefinition> GetTokens(IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                ValidateTransactions(pendingTransactions.ToList());
                return GetTokens();
            }
        }

        public IReadOnlyList<UnspentOutput> GetRegisteredTokenOutputs(IEnumerable<string> addresses, IEnumerable<Transaction> pendingTransactions)
        {
            var owned = new HashSet<string>(addresses ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            lock (sync)
            {
                var pending = pendingTransactions.ToList();
                ValidateTransactions(pending);
                return BuildUtxo().Values
                    .Where(item => item.Output.AssetId != null && owned.Contains(item.Output.OneTimeAddress)).ToArray();
            }
        }

        public long GetTokenBalance(IEnumerable<string> addresses, string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) throw new ArgumentException("Token identifier is required.", nameof(tokenId));
            return GetUnspentOutputs(addresses).Where(item => item.Output.AssetId == tokenId)
                .Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
        }

        /// <summary>Lists all registered tokens and confirmed balances under a single chain lock.</summary>
        public IReadOnlyList<TokenBalance> GetTokenBalances(IEnumerable<string> addresses)
        {
            var owned = new HashSet<string>(addresses ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            lock (sync)
            {
                var amounts = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (UnspentOutput output in BuildUtxo().Values.Where(item => item.Output.AssetId != null && owned.Contains(item.Output.OneTimeAddress)))
                    AddAssetAmount(amounts, output.Output.AssetId, output.Output.Amount);
                var definitions = GetTokens().ToDictionary(token => token.Id, StringComparer.Ordinal);
                return blocks.SelectMany(block => block.Transactions.Where(tx => tx.Kind == TransactionKind.TokenCreate || tx.Kind == TransactionKind.InitialLaunchCreate)
                    .SelectMany(tx => (tx.Kind == TransactionKind.InitialLaunchCreate ? InitialLaunch.FundingTokens() : new[] { tx.Token })
                    .Select(token => new TokenBalance(definitions[token.Id], amounts.ContainsKey(token.Id) ? amounts[token.Id] : 0,
                        block.Height, blocks[blocks.Count - 1].Height - block.Height + 1,
                        tx.TransactionApproval != null || block.TransactionValidations?.Any(proof => proof.TransactionId == tx.Id) == true ? 1 : 0)))).ToArray();
            }
        }

        public IReadOnlyList<TokenBalance> GetTokenBalances(IEnumerable<string> addresses, IEnumerable<Transaction> pendingTransactions)
        {
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            lock (sync)
            {
                ValidateTransactions(pendingTransactions.ToList());
                return GetTokenBalances(addresses);
            }
        }

        public IReadOnlyList<UnspentOutput> GetSpendableTokenOutputs(IEnumerable<string> addresses,
            IEnumerable<Transaction> pendingTransactions, string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) throw new ArgumentException("Token identifier is required.", nameof(tokenId));
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            var pending = pendingTransactions.ToList();
            lock (sync)
            {
                ValidateTransactions(pending);
                var reserved = new HashSet<string>(pending.SelectMany(tx => tx.Inputs).Select(i => Key(i.TransactionId, i.OutputIndex)), StringComparer.Ordinal);
                var owned = new HashSet<string>(addresses ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
                return BuildPendingUtxo(BuildUtxo(), pending, blocks).Values.Where(item => owned.Contains(item.Output.OneTimeAddress) && item.Output.AssetId == tokenId && item.Output.UnlockHeight <= blocks.Count && !item.IsPoolReserve &&
                    !reserved.Contains(Key(item.TransactionId, item.OutputIndex))).ToArray();
            }
        }

        private static void AddOutputs(Transaction transaction, IDictionary<string, UnspentOutput> utxo)
        {
            TokenGovernance.Record(transaction, utxo);
            InitialLaunch.Record(transaction, utxo);
            for (int i = 0; i < transaction.Outputs.Count; i++)
                if (transaction.Outputs[i].Amount > 0 || transaction.Kind == TransactionKind.InitialLaunchCreate && i == 1) utxo.Add(Key(transaction.Id, i), new UnspentOutput { TransactionId = transaction.Id, OutputIndex = i, Output = transaction.Outputs[i],
                    TransactionKind = i == 0 || (i == 1 && (transaction.Kind == TransactionKind.LiquidityPool || transaction.Kind == TransactionKind.LiquiditySwap || transaction.Kind == TransactionKind.LiquidityAdd || transaction.Kind == TransactionKind.LiquidityRemove || transaction.Kind == TransactionKind.LaunchCreate || transaction.Kind == TransactionKind.LaunchContribute || transaction.Kind == TransactionKind.LaunchActivate || InitialLaunch.IsOperation(transaction.Kind))) ? transaction.Kind : TransactionKind.Transfer,
                    InitialLaunchCollecting = i < 2 && InitialLaunch.IsOperation(transaction.Kind) && transaction.Outputs[0].Amount > InitialLaunch.PrincipalSupply,
                    Launch = i < 2 && transaction.Kind != TransactionKind.LaunchActivate && transaction.Kind != TransactionKind.LaunchRefund ? transaction.Launch?.Copy() : null,
                    PoolParticipation = i < 2 ? transaction.PoolParticipation?.Copy() : null,
                    PoolBurnPolicy = i < 2 ? transaction.PoolBurnPolicy?.Copy() : null,
                    ValidatorPublicKey = i == 2 && transaction.Kind == TransactionKind.InitialLaunchPurchase ? transaction.InitialLaunchIdentity.PublicKey : i == 0 ? transaction.ValidatorPublicKey : null,
                    ValidatorRewardAddress = i == 4 && transaction.Kind == TransactionKind.InitialLaunchCreate ? transaction.Outputs[4].OneTimeAddress : i == 2 && transaction.Kind == TransactionKind.InitialLaunchPurchase ? transaction.Outputs[2].OneTimeAddress : i == 0 ? transaction.ValidatorRewardAddress : null,
                    ValidatorOwnedAddresses = i == 4 && transaction.Kind == TransactionKind.InitialLaunchCreate ? new List<string> { transaction.Outputs[4].OneTimeAddress } : i == 2 && transaction.Kind == TransactionKind.InitialLaunchPurchase ? new List<string> { transaction.Outputs[2].OneTimeAddress } : i == 0 ? transaction.ValidatorOwnedAddresses : null });
        }

        private static void AddValidationFee(Transaction transaction, IDictionary<string, UnspentOutput> utxo)
        {
            TransactionOutput fee = transaction.GetValidationFeeOutput();
            if (fee == null) throw new InvalidOperationException("Missing validated fee credit.");
            int index = transaction.Outputs.Count;
            utxo.Add(Key(transaction.Id, index), new UnspentOutput { TransactionId = transaction.Id,
                OutputIndex = index, Output = fee, TransactionKind = TransactionKind.Transfer });
        }

        private static string Key(string transactionId, int outputIndex) => transactionId + ":" + outputIndex;

        internal static string CreateVotePayload(Block block)
        {
            return block.Height.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + block.PreviousHash + "|" +
                string.Join("|", block.Transactions.Select(item => item.Id)) + "|" +
                string.Join("|", block.Validators.OrderBy(item => item.ValidatorId, StringComparer.Ordinal).Select(item =>
                    item.ValidatorId + ":" + item.RewardAddress + ":" + item.LockedAmount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + item.IsCreator)) +
                block.ValidationProofPayload();
        }

        private static Block BuildCanonicalGenesis()
        {
            var block = new Block
            {
                Height = 0,
                PreviousHash = new string('0', 64),
                TimestampUtcTicks = GenesisTimestampUtcTicks,
                Rules = GenesisRules.CreateCanonical()
            };
            Mine(block);
            return block;
        }

        private static Block CreateGenesisBlock()
        {
            // Never expose the cached block/manifest through the mutable Blocks API.
            return new Block
            {
                Height = 0,
                PreviousHash = new string('0', 64),
                TimestampUtcTicks = GenesisTimestampUtcTicks,
                Nonce = canonicalGenesis.Nonce,
                Hash = GenesisHash,
                Rules = GenesisRules.CreateCanonical()
            };
        }

        private static bool IsCanonicalGenesis(Block block)
        {
            return block != null && block.Height == 0 &&
                block.PreviousHash == new string('0', 64) &&
                block.TimestampUtcTicks == GenesisTimestampUtcTicks &&
                block.Nonce == canonicalGenesis.Nonce && block.Hash == GenesisHash &&
                block.ConsensusVersion == 0 && block.TransactionValidations == null &&
                block.Transactions != null && block.Transactions.Count == 0 &&
                block.Validators == null && block.Rules != null && block.Rules.IsCanonical() &&
                block.CalculateHash() == GenesisHash;
        }

        private static void Mine(Block block)
        {
            ProofOfWork.Mine(block, CancellationToken.None);
        }
    }
}
