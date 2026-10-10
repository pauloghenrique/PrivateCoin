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
        public const long WalletCreationReward = 6L * OneCoin;
        public const long DistributionSupply = 180000L * OneCoin;
        public const int RewardedWalletLimit = (int)(DistributionSupply / WalletCreationReward);
        public const long MaximumSupply = DistributionSupply;
        // One atomic unit: 0.00000001 POVIX.
        public const long TransferFeeStep = 1L;
        public const long MaximumTransferFee = OneCoin;
        public const int ConsensusVersion = 15;
        private const int LegacyImmediateTransferVersion = 14;
        private const int ImmediateTokenVersion = 13;
        private const int ImmediateChangeVersion = 12;
        private const int ApprovalVersion = 11;
        private const int SelfValidatedVersion = 10;
        private const int LegacyImmediateVersion = 9;
        private const int LegacyWalletBatchVersion = 8;
        private const int LegacyHybridVersion = 7;
        private const int LegacySignedVersion = 4;
        public const int ValidationsPerBlock = 1;
        private const int LegacyValidationsPerBlock = 20;
        public const string GenesisHash = "0008127acee1ee9328acdc860e2497340d4923426da9b391088d5c83ef46b673";
        public const string NetworkId = "povix-mainnet-v1-" + GenesisHash;
        private const long GenesisTimestampUtcTicks = 639028224000000000L;
        private const long GenesisNonce = 4995L;
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
                if (pending.Count != ValidationsPerBlock || pending.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != ValidationsPerBlock)
                    throw new InvalidOperationException("A consensus block requires exactly one validated operation.");
                ValidatorStake[] active = ExcludeTransactionParticipants(validators, pending).ToArray();
                ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(StakesFromUtxo(BuildUtxo()), pending).ToArray();
                if (!SameStakeSet(active, globallyEligible))
                    throw new InvalidOperationException("The proposed validators do not match the globally locked collateral set.");
                if (active.Length < 2) throw new InvalidOperationException("At least two active validators are required to create and confirm a block.");
                var replay = BuildUtxo();
                foreach (Transaction transaction in pending)
                {
                    if (RequiresLockedTokenApproval(transaction)) ValidateTransactionApproval(transaction, replay, blocks);
                    Apply(transaction, replay, false, RequiresLockedTokenApproval(transaction));
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
                if (WalletDistributionAddresses().Contains(rewardAddress) || pending.Any(tx => tx.Kind == TransactionKind.WalletCreate &&
                    tx.Outputs[0].OneTimeAddress == rewardAddress))
                    throw new InvalidOperationException("This wallet address already received its initial distribution.");
                var registration = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = DateTime.UtcNow.Ticks };
                int queuedRewards = pending.Count(tx => tx.Kind == TransactionKind.WalletCreate && tx.WalletDistributionId == null && tx.Outputs[0].Amount == WalletCreationReward);
                registration.Outputs.Add(new TransactionOutput { Amount = CountWalletCreationRewards() + queuedRewards < RewardedWalletLimit ? WalletCreationReward : 0, OneTimeAddress = rewardAddress });
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
                tx.Kind == TransactionKind.WalletDistribution || (tx.Kind == TransactionKind.WalletCreate && tx.WalletDistributionId == null) ||
                ((block.Validators == null || block.Validators.Count == 0) && IsWalletCreationReward(tx))))
                .Select(tx => tx.Outputs[0].OneTimeAddress), StringComparer.Ordinal);
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
                ((tx.Kind == TransactionKind.WalletCreate || tx.Kind == TransactionKind.WalletDistribution) && tx.Outputs[0].Amount == WalletCreationReward) ||
                ((block.Validators == null || block.Validators.Count == 0) && IsWalletCreationReward(tx))));
        }

        public static bool IsSelfValidatedOperation(Transaction transaction)
        {
            return transaction != null && (transaction.Kind == TransactionKind.WalletCreate ||
                transaction.Kind == TransactionKind.StakeLock || transaction.Kind == TransactionKind.StakeUnlock);
        }

        public static bool RequiresLockedTokenApproval(Transaction transaction)
        {
            return transaction != null && transaction.Inputs.Count > 0 &&
                (transaction.Kind == TransactionKind.Transfer || transaction.Kind == TransactionKind.TokenCreate || transaction.Kind == TransactionKind.TokenTransfer);
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

        private static void ValidateBatch(IList<Transaction> transactions, int expectedCount = ValidationsPerBlock)
        {
            if (transactions.Count != expectedCount || transactions.Select(tx => tx.Id).Distinct(StringComparer.Ordinal).Count() != expectedCount ||
                !transactions.Select(tx => tx.Id).SequenceEqual(OrderByFeePriority(transactions).Select(tx => tx.Id), StringComparer.Ordinal))
                throw new InvalidOperationException("A block requires exactly " + expectedCount + " distinct operation(s) in fee priority order.");
        }

        private static void ValidateWalletIssuance(Transaction transaction, ref long issued)
        {
            if ((transaction.Kind != TransactionKind.WalletCreate && transaction.Kind != TransactionKind.WalletDistribution) || transaction.WalletDistributionId != null) return;
            long expected = issued < DistributionSupply ? WalletCreationReward : 0;
            if (transaction.Outputs.Count != 1 || transaction.Outputs[0].Amount != expected)
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
            Transaction[] batch = OrderByFeePriority(transactions).Take(ValidationsPerBlock).ToArray();
            return batch.Length == ValidationsPerBlock ? batch : new Transaction[0];
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
                    Apply(transaction, utxo, false, IsValidTransactionApproval(transaction, utxo, blocks));
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
                return BuildPendingUtxo(BuildUtxo(), pending, blocks).Values.Where(item => wanted.Contains(item.Output.OneTimeAddress) &&
                    item.TransactionKind != TransactionKind.StakeLock && item.Output.AssetId == null &&
                    !reserved.Contains(Key(item.TransactionId, item.OutputIndex))).ToArray();
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
                    Apply(transaction, utxo, false, IsValidTransactionApproval(transaction, utxo, blocks));
                var balances = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (UnspentOutput item in utxo.Values.Where(item => item.Output.AssetId == null))
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
                        if (block == null || block.Height != i || block.Hash != block.CalculateHash() || !ProofOfWork.MeetsTarget(block.Hash)) return false;
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
                if (transaction.Kind == TransactionKind.WalletCreate)
                {
                    if (transaction.WalletDistributionId != null) ValidateWalletReceipt(transaction, distributions, counted);
                    else
                    {
                        if (transaction.Outputs == null || transaction.Outputs.Count != 1) throw new InvalidOperationException("Invalid wallet distribution.");
                        if (!distributionAddresses.Add(transaction.Outputs[0].OneTimeAddress)) throw new InvalidOperationException("Wallet already received its distribution.");
                        ValidateWalletIssuance(transaction, ref issued);
                    }
                }
                if ((transaction.Kind == TransactionKind.StakeLock || transaction.Kind == TransactionKind.StakeUnlock) && transaction.Fee != 0)
                    throw new InvalidOperationException("Self-validated collateral operations do not charge validator fees.");
                Apply(transaction, utxo, false, IsValidTransactionApproval(transaction, utxo, blocks));
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
                if (block.ConsensusVersion != 0 && block.ConsensusVersion != LegacySignedVersion && block.ConsensusVersion != LegacyHybridVersion && block.ConsensusVersion != LegacyWalletBatchVersion && block.ConsensusVersion != LegacyImmediateVersion && block.ConsensusVersion != SelfValidatedVersion && block.ConsensusVersion != ApprovalVersion && block.ConsensusVersion != ImmediateChangeVersion && block.ConsensusVersion != ImmediateTokenVersion && block.ConsensusVersion != LegacyImmediateTransferVersion && block.ConsensusVersion != ConsensusVersion)
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
                    if (!transactionIds.Add(tx.Id)) throw new InvalidOperationException("Duplicate transaction identifier.");
                }
                if (block.TransactionValidations != null && (block.ConsensusVersion == 0 || block.Validators == null || block.Validators.Count == 0))
                    throw new InvalidOperationException("Unexpected transaction validation proofs.");
                if (block.ConsensusVersion >= SelfValidatedVersion && (block.Validators == null || block.Validators.Count == 0))
                {
                    ValidateBatch(block.Transactions, block.ConsensusVersion < ConsensusVersion ? LegacyValidationsPerBlock : ValidationsPerBlock);
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
                    ValidateBatch(block.Transactions, block.ConsensusVersion < ConsensusVersion ? LegacyValidationsPerBlock : ValidationsPerBlock);
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
                        !(block.ConsensusVersion >= LegacyWalletBatchVersion && block.Transactions[transactionIndex].Kind == TransactionKind.WalletCreate) &&
                        block.Transactions[transactionIndex].Kind != TransactionKind.StakeLock &&
                        block.Transactions[transactionIndex].Kind != TransactionKind.StakeUnlock)
                        throw new InvalidOperationException("Transfers and token operations require signed consensus validations.");
                    if (block.ConsensusVersion < LegacyWalletBatchVersion && block.Transactions[transactionIndex].Kind == TransactionKind.WalletCreate)
                        throw new InvalidOperationException("Wallet registrations require consensus version 8.");
                    Transaction operation = block.Transactions[transactionIndex];
                    if (operation.Kind == TransactionKind.WalletDistribution) throw new InvalidOperationException("Distribution cannot be included in a regular batch.");
                    if (operation.Kind == TransactionKind.WalletCreate)
                    {
                        if (block.ConsensusVersion >= LegacyImmediateVersion && (block.ConsensusVersion < SelfValidatedVersion || operation.WalletDistributionId != null)) ValidateWalletReceipt(operation, distributions, counted);
                        else
                        {
                            if (operation.WalletDistributionId != null) throw new InvalidOperationException("Unexpected wallet receipt in legacy consensus.");
                            if (!distributionAddresses.Add(operation.Outputs[0].OneTimeAddress) && block.ConsensusVersion >= SelfValidatedVersion) throw new InvalidOperationException("Duplicate wallet distribution address.");
                        }
                    }
                    if (block.ConsensusVersion >= SelfValidatedVersion && (operation.Kind == TransactionKind.StakeLock || operation.Kind == TransactionKind.StakeUnlock) && operation.Fee != 0)
                        throw new InvalidOperationException("Self-validated collateral operations cannot charge validator fees.");
                    if (block.ConsensusVersion < SelfValidatedVersion && (operation.Kind == TransactionKind.StakeLock || operation.Kind == TransactionKind.StakeUnlock) && operation.Fee < TransferFeeStep)
                        throw new InvalidOperationException("Historical collateral operations require their original minimum fee.");
                    ValidateWalletIssuance(operation, ref issued);
                    Apply(block.Transactions[transactionIndex], utxo, false, block.ConsensusVersion >= ApprovalVersion && RequiresLockedTokenApproval(operation));
                }
            }
            if (issued > MaximumSupply || validatorIssued > ProofOfStake.MaximumSupply) throw new InvalidOperationException("Invalid supply.");
        }

        private static void ValidateProofOfStakeBlock(Block block, string previousHash, IDictionary<string, UnspentOutput> utxo, IList<Block> history)
        {
            if (block.Transactions.Count == 0) throw new InvalidOperationException("A proof-of-stake block must contain its reward.");
            BlockValidator[] records = block.Validators.ToArray();
            if (records.Length < 2 || records.Any(item => item == null) || records.Count(item => item.IsCreator) != 1)
                throw new InvalidOperationException("Invalid proof-of-stake validator proof.");
            var stakes = records.Select(item => new ValidatorStake(item.ValidatorId, item.RewardAddress, item.LockedAmount,
                item.OwnedAddresses ?? new List<string> { item.RewardAddress }, item.PublicKey, null)).ToArray();
            var collateral = block.ConsensusVersion >= SelfValidatedVersion && block.ConsensusVersion < ConsensusVersion
                ? BuildLegacyImmediateUtxo(utxo, block.Transactions.Skip(1), history, null, block.ConsensusVersion >= ImmediateChangeVersion,
                    block.ConsensusVersion >= ImmediateTokenVersion, block.ConsensusVersion >= LegacyImmediateTransferVersion) : utxo;
            foreach (BlockValidator record in records)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.PublicKey) ||
                    record.ValidatorId != Crypto.Sha256(record.PublicKey) ||
                    !ProofOfStake.VerifyVote(record.PublicKey, CreateVotePayload(block), record.VoteSignature))
                    throw new InvalidOperationException("Invalid individual validator vote signature.");
                bool collateralExists = collateral.Values.Any(item => item.TransactionKind == TransactionKind.StakeLock &&
                    item.ValidatorPublicKey == record.PublicKey && item.ValidatorRewardAddress == record.RewardAddress &&
                    item.Output.Amount == record.LockedAmount);
                if (!collateralExists) throw new InvalidOperationException("The validator collateral is not globally locked on chain.");
            }
            Transaction[] transfers = block.Transactions.Skip(1).ToArray();
            int expectedOperations = block.ConsensusVersion < ConsensusVersion ? LegacyValidationsPerBlock : ValidationsPerBlock;
            if (block.ConsensusVersion >= LegacySignedVersion &&
                (transfers.Length != expectedOperations || transfers.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != expectedOperations ||
                !transfers.Select(item => item.Id).SequenceEqual(OrderByFeePriority(transfers).Select(item => item.Id), StringComparer.Ordinal)))
                throw new InvalidOperationException("A consensus block requires " + expectedOperations + " distinct operation(s) in fee priority order.");
            bool excludeAllParticipants = block.ConsensusVersion < SelfValidatedVersion || block.ConsensusVersion >= ConsensusVersion;
            ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(StakesFromUtxo(collateral), transfers, excludeAllParticipants).ToArray();
            if (!SameStakeSet(stakes, globallyEligible))
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
            if (!RequiresLockedTokenApproval(transaction)) throw new ArgumentException("A movement requiring a fee is required.", nameof(transaction));
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            if (localValidators == null) throw new ArgumentNullException(nameof(localValidators));
            lock (sync)
            {
                var pending = pendingTransactions.Where(tx => tx.Id != transaction.Id).Concat(new[] { transaction }).ToList();
                ValidateTransactions(pending);
                var collateral = BuildPendingUtxo(BuildUtxo(), pending, blocks, transaction.Id);
                ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(StakesFromUtxo(collateral), new[] { transaction }, false).ToArray();
                ValidatorStake[] owned = localValidators.Where(local => globallyEligible.Any(global =>
                    global.ValidatorId == local.ValidatorId && global.PublicKey == local.PublicKey &&
                    global.RewardAddress == local.RewardAddress && global.LockedAmount == local.LockedAmount)).ToArray();
                if (owned.Length == 0) return null;
                Block anchor = blocks.Last();
                ValidatorStake validator = SelectTransactionValidator(owned, transaction, anchor.Hash, anchor.Height + 1);
                UnspentOutput locked = collateral.Values.Single(item => item.TransactionKind == TransactionKind.StakeLock &&
                    item.ValidatorPublicKey == validator.PublicKey && item.ValidatorRewardAddress == validator.RewardAddress);
                var validation = new TransactionApproval { AnchorHeight = anchor.Height, AnchorHash = anchor.Hash,
                    CollateralTransactionId = locked.TransactionId, CollateralOutputIndex = locked.OutputIndex,
                    Proof = new TransactionValidation { TransactionId = transaction.Id, ValidatorId = validator.ValidatorId,
                        RewardAddress = validator.RewardAddress, PublicKey = validator.PublicKey } };
                validation.Proof.Signature = validator.CreateVote(CreateTransactionApprovalPayload(transaction, validation));
                var approved = new Transaction { Id = transaction.Id, TimestampUtcTicks = transaction.TimestampUtcTicks,
                    Inputs = transaction.Inputs, Outputs = transaction.Outputs, Fee = transaction.Fee, Kind = transaction.Kind,
                    Token = transaction.Token, TransactionApproval = validation };
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

        /// <summary>Excludes unapproved movements and operations depending on them.</summary>
        public IReadOnlyList<Transaction> SelectApprovedValidationBatch(IEnumerable<Transaction> pendingTransactions)
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
                return SelectValidationBatch(ready);
            }
        }

        private static string CreateTransactionApprovalPayload(Transaction transaction, TransactionApproval validation)
        {
            return NetworkId + "|transaction-approval-v11|" + transaction.Id + "|" +
                validation.AnchorHeight.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + validation.AnchorHash + "|" +
                validation.CollateralTransactionId + "|" + validation.CollateralOutputIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                validation.Proof.RewardAddress;
        }

        private static void ValidateTransactionApproval(Transaction transaction, IDictionary<string, UnspentOutput> collateral, IList<Block> history)
        {
            TransactionApproval validation = transaction.TransactionApproval;
            TransactionValidation proof = validation?.Proof;
            UnspentOutput locked;
            if (!RequiresLockedTokenApproval(transaction) || validation == null || proof == null ||
                transaction.Inputs.Any(input => !collateral.ContainsKey(Key(input.TransactionId, input.OutputIndex))) ||
                validation.AnchorHeight < 0 || validation.AnchorHeight >= history.Count ||
                history[validation.AnchorHeight].Hash != validation.AnchorHash ||
                !collateral.TryGetValue(Key(validation.CollateralTransactionId, validation.CollateralOutputIndex), out locked) ||
                locked.TransactionKind != TransactionKind.StakeLock || locked.Output.Amount <= 0 ||
                proof.TransactionId != transaction.Id || proof.PublicKey != locked.ValidatorPublicKey ||
                proof.RewardAddress != locked.ValidatorRewardAddress || proof.ValidatorId != Crypto.Sha256(proof.PublicKey))
                throw new InvalidOperationException("A movement requires a valid approval backed by locked tokens.");
            var validator = new ValidatorStake(proof.ValidatorId, proof.RewardAddress, locked.Output.Amount,
                locked.ValidatorOwnedAddresses, proof.PublicKey, null);
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
                        ValidateTransactionApproval(operation, replay, history);
                        TransactionValidation proof = block.TransactionValidations.SingleOrDefault(item => item.TransactionId == operation.Id);
                        if (proof == null || proof.ProofPayload() != operation.TransactionApproval.Proof.ProofPayload())
                            throw new InvalidOperationException("The fee must belong to the approving locked validator.");
                    }
                    Apply(operation, replay, false, RequiresLockedTokenApproval(operation));
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

        private static ValidatorStake[] StakesFromUtxo(IDictionary<string, UnspentOutput> utxo)
        {
            return utxo.Values.Where(item => item.TransactionKind == TransactionKind.StakeLock)
                .Select(item => new ValidatorStake(Crypto.Sha256(item.ValidatorPublicKey), item.ValidatorRewardAddress,
                    item.Output.Amount, item.ValidatorOwnedAddresses, item.ValidatorPublicKey, null))
                .OrderBy(item => item.ValidatorId, StringComparer.Ordinal).ToArray();
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

        private static void Apply(Transaction transaction, IDictionary<string, UnspentOutput> utxo, bool allowMint, bool includeValidationFee = false)
        {
            if (transaction == null || transaction.Id != transaction.CalculateId() || transaction.Outputs.Count == 0) throw new InvalidOperationException("Invalid transaction.");
            if (transaction.TransactionApproval != null && !RequiresLockedTokenApproval(transaction))
                throw new InvalidOperationException("Only fee-paying movements may carry an approval.");
            if (transaction.WalletDistributionId != null && transaction.Kind != TransactionKind.WalletCreate)
                throw new InvalidOperationException("Only wallet receipts may reference a distribution.");
            if (transaction.Kind == TransactionKind.WalletCreate || transaction.Kind == TransactionKind.WalletDistribution)
            {
                if (allowMint != (transaction.Kind == TransactionKind.WalletDistribution) || transaction.Inputs.Count != 0 || transaction.Fee != 0 || transaction.Outputs.Count != 1 ||
                    (transaction.Outputs[0].Amount != WalletCreationReward && transaction.Outputs[0].Amount != 0) ||
                    (transaction.WalletDistributionId != null && transaction.Outputs[0].Amount != 0) ||
                    string.IsNullOrWhiteSpace(transaction.Outputs[0].OneTimeAddress) || transaction.Outputs[0].AssetId != null ||
                    transaction.Token != null || transaction.ValidatorPublicKey != null || transaction.ValidatorRewardAddress != null || transaction.ValidatorOwnedAddresses != null)
                    throw new InvalidOperationException("Invalid fee-free wallet registration.");
                AddOutputs(transaction, utxo);
                return;
            }
            if (transaction.Inputs.Count == 0 && !allowMint) throw new InvalidOperationException("Minting is only allowed for genesis and mining rewards.");
            if (!Enum.IsDefined(typeof(TransactionKind), transaction.Kind)) throw new InvalidOperationException("Unknown transaction kind.");
            bool tokenOperation = transaction.Kind == TransactionKind.TokenCreate || transaction.Kind == TransactionKind.TokenTransfer;
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
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(input.PublicKey);
                    if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(input.Signature))) throw new InvalidOperationException("Invalid signature.");
                }
                if (source.Output.AssetId == null) inputTotal = checked(inputTotal + source.Output.Amount);
                else
                {
                    if (transaction.Kind != TransactionKind.TokenTransfer) throw new InvalidOperationException("Token inputs require a token transfer.");
                    AddAssetAmount(tokenInputs, source.Output.AssetId, source.Output.Amount);
                }
            }
            long outputTotal = 0;
            foreach (var output in transaction.Outputs)
            {
                if (output.Amount <= 0 || string.IsNullOrWhiteSpace(output.OneTimeAddress)) throw new InvalidOperationException("Invalid output.");
                if (output.AssetId == null) outputTotal = checked(outputTotal + output.Amount);
                else
                {
                    if (!tokenOperation || output.AssetId.Length != 64 || output.AssetId.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                        throw new InvalidOperationException("Invalid asset output.");
                    AddAssetAmount(tokenOutputs, output.AssetId, output.Amount);
                }
            }
            if (transaction.Kind == TransactionKind.TokenCreate)
            {
                if (tokenOutputs.Count != 1 || !tokenOutputs.ContainsKey(transaction.Token.Id) || tokenOutputs[transaction.Token.Id] != transaction.Token.Supply)
                    throw new InvalidOperationException("Token creation must issue exactly the declared supply.");
            }
            if (transaction.Kind == TransactionKind.TokenTransfer &&
                (tokenInputs.Count == 0 || tokenInputs.Count != tokenOutputs.Count ||
                 tokenInputs.Any(pair => !tokenOutputs.ContainsKey(pair.Key) || tokenOutputs[pair.Key] != pair.Value)))
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
            if (!allowMint && transaction.Kind != TransactionKind.StakeLock && transaction.Kind != TransactionKind.StakeUnlock && transaction.Fee < TransferFeeStep)
                throw new InvalidOperationException("The minimum transaction fee is one atomic unit.");
            if (!allowMint && inputTotal != checked(outputTotal + transaction.Fee))
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
            lock (sync) return blocks.SelectMany(block => block.Transactions)
                .Where(tx => tx.Kind == TransactionKind.TokenCreate).Select(tx => new TokenDefinition {
                    Id = tx.Token.Id, Name = tx.Token.Name, Symbol = tx.Token.Symbol,
                    Decimals = tx.Token.Decimals, Supply = tx.Token.Supply }).ToArray();
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
                return blocks.SelectMany(block => block.Transactions.Where(tx => tx.Kind == TransactionKind.TokenCreate)
                    .Select(tx => new TokenBalance(tx.Token, amounts.ContainsKey(tx.Token.Id) ? amounts[tx.Token.Id] : 0,
                        block.Height, blocks[blocks.Count - 1].Height - block.Height + 1,
                        tx.TransactionApproval != null || block.TransactionValidations?.Any(proof => proof.TransactionId == tx.Id) == true ? 1 : 0))).ToArray();
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
                return BuildPendingUtxo(BuildUtxo(), pending, blocks).Values.Where(item => owned.Contains(item.Output.OneTimeAddress) && item.Output.AssetId == tokenId &&
                    !reserved.Contains(Key(item.TransactionId, item.OutputIndex))).ToArray();
            }
        }

        private static void AddOutputs(Transaction transaction, IDictionary<string, UnspentOutput> utxo)
        {
            for (int i = 0; i < transaction.Outputs.Count; i++)
                if (transaction.Outputs[i].Amount > 0) utxo.Add(Key(transaction.Id, i), new UnspentOutput { TransactionId = transaction.Id, OutputIndex = i, Output = transaction.Outputs[i],
                    TransactionKind = i == 0 ? transaction.Kind : TransactionKind.Transfer,
                    ValidatorPublicKey = i == 0 ? transaction.ValidatorPublicKey : null,
                    ValidatorRewardAddress = i == 0 ? transaction.ValidatorRewardAddress : null,
                    ValidatorOwnedAddresses = i == 0 ? transaction.ValidatorOwnedAddresses : null });
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

        private static Block CreateGenesisBlock()
        {
            return new Block
            {
                Height = 0,
                PreviousHash = new string('0', 64),
                TimestampUtcTicks = GenesisTimestampUtcTicks,
                Nonce = GenesisNonce,
                Hash = GenesisHash
            };
        }

        private static bool IsCanonicalGenesis(Block block)
        {
            return block != null && block.Height == 0 &&
                block.PreviousHash == new string('0', 64) &&
                block.TimestampUtcTicks == GenesisTimestampUtcTicks &&
                block.Nonce == GenesisNonce && block.Hash == GenesisHash &&
                block.Transactions != null && block.Transactions.Count == 0 &&
                (block.Validators == null || block.Validators.Count == 0) &&
                block.CalculateHash() == GenesisHash;
        }

        private static void Mine(Block block)
        {
            ProofOfWork.Mine(block, CancellationToken.None);
        }
    }
}
