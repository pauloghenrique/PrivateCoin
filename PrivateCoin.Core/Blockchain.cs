using System;
using System.Collections.Generic;
using System.Linq;
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
        public const int ConsensusVersion = 4;
        public const string GenesisHash = "0008127acee1ee9328acdc860e2497340d4923426da9b391088d5c83ef46b673";
        public const string NetworkId = "povix-mainnet-v1-" + GenesisHash;
        private const long GenesisTimestampUtcTicks = 639028224000000000L;
        private const long GenesisNonce = 4995L;
        private const string ProofPrefix = "000";
        private readonly object sync = new object();
        internal bool AllowCreatorOnlyProof { get; set; }
        private readonly List<Block> blocks = new List<Block>();

        public FinalityPolicy Finality { get; }
        public int FinalizedHeight => Finality == null ? -1 : Finality.FinalizedHeight(Blocks);

        public Blockchain GetConfirmedView()
        {
            if (Finality == null) return this;
            int height = FinalizedHeight;
            return height < 0 ? new Blockchain(Finality) : new Blockchain(Blocks.Take(height + 1), Finality);
        }

        public Blockchain() : this(FinalityPolicy.FromConfiguration()) { }

        public Blockchain(FinalityPolicy finality)
        {
            Finality = finality;
            blocks.Add(CreateGenesisBlock());
        }

        /// <summary>Restores and validates an existing chain.</summary>
        public Blockchain(IEnumerable<Block> existingBlocks) : this(existingBlocks, FinalityPolicy.FromConfiguration()) { }

        public Blockchain(IEnumerable<Block> existingBlocks, FinalityPolicy finality) : this(existingBlocks, finality, false) { }

        internal Blockchain(IEnumerable<Block> existingBlocks, FinalityPolicy finality, bool creatorOnly)
        {
            AllowCreatorOnlyProof = creatorOnly;
            Finality = finality;
            if (existingBlocks == null) throw new ArgumentNullException(nameof(existingBlocks));
            blocks.AddRange(finality == null ? existingBlocks : FinalityPolicy.CopyBlocks(existingBlocks));
            if (!IsValid()) throw new InvalidOperationException("The stored blockchain is invalid.");
        }

        public IReadOnlyList<Block> Blocks { get { lock (sync) return blocks.ToArray(); } }

        public Block AddBlock(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            lock (sync)
            {
                RequireFinalizedTip();
                var pending = OrderByFeePriority(transactions).ToList();
                ValidateTransactions(pending);
                var block = new Block { Height = blocks.Count, PreviousHash = blocks[blocks.Count - 1].Hash, TimestampUtcTicks = DateTime.UtcNow.Ticks, Transactions = pending };
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
                RequireFinalizedTip();
                var pending = OrderByFeePriority(transactions).ToList();
                ValidateTransactions(pending);
                ValidatorStake[] active = ExcludeTransactionParticipants(validators, pending).ToArray();
                ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(StakesFromUtxo(BuildUtxo()), pending).ToArray();
                if (Finality == null && !SameStakeSet(active, globallyEligible))
                    throw new InvalidOperationException("The proposed validators do not match the globally locked collateral set.");
                if (globallyEligible.Length < 2) throw new InvalidOperationException("At least two active validators are required to create and confirm a block.");
                int height = blocks.Count;
                ValidatorStake creator = ProofOfStake.SelectCreator(globallyEligible, blocks[blocks.Count - 1].Hash, height);
                if (!active.Any(v => v.PublicKey == creator.PublicKey && v.LockedAmount == creator.LockedAmount))
                    throw new InvalidOperationException("Only the selected creator can propose this block.");
                ValidatorStake[] confirmers = globallyEligible.Where(item => item.ValidatorId != creator.ValidatorId).ToArray();
                IReadOnlyList<ValidatorReward> rewards = ProofOfStake.DistributeReward(height, creator, confirmers);
                long fees = pending.Aggregate(0L, (total, transaction) => checked(total + transaction.Fee));
                var reward = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks };
                foreach (ValidatorReward share in rewards)
                    reward.Outputs.Add(new TransactionOutput
                    {
                        Amount = checked(share.Amount + (share.IsCreator ? fees : 0)),
                        OneTimeAddress = share.RewardAddress
                    });
                if (rewards.Count == 0 && fees > 0)
                    reward.Outputs.Add(new TransactionOutput { Amount = fees, OneTimeAddress = creator.RewardAddress });
                reward.Id = reward.CalculateId();
                var block = new Block
                {
                    Height = height,
                    PreviousHash = blocks[blocks.Count - 1].Hash,
                    TimestampUtcTicks = reward.TimestampUtcTicks,
                    Transactions = new[] { reward }.Concat(pending).ToList(),
                    Validators = globallyEligible.OrderBy(item => item.ValidatorId, StringComparer.Ordinal).Select(item => new BlockValidator
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
                    ValidatorStake registered = globallyEligible.Single(item => item.ValidatorId == record.ValidatorId);
                    record.PublicKey = registered.PublicKey;
                    ValidatorStake signer = active.SingleOrDefault(item => item.ValidatorId == record.ValidatorId);
                    if (Finality == null || record.IsCreator)
                        record.VoteSignature = signer.CreateVote(votePayload);
                }
                Mine(block);
                blocks.Add(block);
                return block;
            }
        }

        /// <summary>
        /// Creates a block that distributes six coins to a newly-created wallet.
        /// Distribution stops permanently after 180,000 coins have been issued.
        /// </summary>
        public bool TryAddWalletCreationReward(string rewardAddress, out Block rewardBlock)
        {
            if (string.IsNullOrWhiteSpace(rewardAddress)) throw new ArgumentException("A wallet reward address is required.", nameof(rewardAddress));
            lock (sync)
            {
                RequireFinalizedTip();
                int rewardedWallets = CountWalletCreationRewards();
                if (rewardedWallets >= RewardedWalletLimit)
                {
                    rewardBlock = null;
                    return false;
                }

                var reward = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks };
                reward.Outputs.Add(new TransactionOutput { Amount = WalletCreationReward, OneTimeAddress = rewardAddress });
                reward.Id = reward.CalculateId();
                rewardBlock = new Block
                {
                    Height = blocks.Count,
                    PreviousHash = blocks[blocks.Count - 1].Hash,
                    TimestampUtcTicks = reward.TimestampUtcTicks
                };
                rewardBlock.Transactions.Add(reward);
                Mine(rewardBlock);
                blocks.Add(rewardBlock);
                return true;
            }
        }

        private void RequireFinalizedTip()
        {
            if (Finality != null && Finality.FinalizedHeight(blocks) != blocks.Count - 1)
                throw new InvalidOperationException("Aguarde a finalização por quórum dos validadores antes de propor outro bloco.");
        }

        private int CountWalletCreationRewards()
        {
            return blocks.Skip(1).Count(block => block.Transactions.Count > 0 && IsWalletCreationReward(block.Transactions[0]));
        }

        /// <summary>
        /// Adopts a chain only with newer quorum-finalized blocks, preserving
        /// the finalized prefix. Inspection-only chains cannot adopt peer data.
        /// </summary>
        public bool TryReplaceChain(IEnumerable<Block> candidateBlocks)
        {
            if (candidateBlocks == null) throw new ArgumentNullException(nameof(candidateBlocks));
            if (Finality == null)
                throw new InvalidOperationException("Uma cadeia de inspeção sem checkpoint não pode escolher ou adotar cadeias da rede.");
            var candidate = new Blockchain(candidateBlocks, Finality);
            Block[] replacement = candidate.Blocks.ToArray();

            lock (sync)
            {
                int currentFinalized = Finality.FinalizedHeight(blocks);
                int incomingFinalized = Finality.FinalizedHeight(replacement);
                if (incomingFinalized < Finality.AnchorHeight) return false;
                int commonFinalized = Math.Min(currentFinalized, incomingFinalized);
                if (commonFinalized >= Finality.AnchorHeight && blocks[commonFinalized].Hash != replacement[commonFinalized].Hash)
                    throw new InvalidOperationException("Conflicting finalized chains: stop and investigate validator equivocation.");
                if (incomingFinalized <= currentFinalized) return false;
                // Discard an uncertified local suffix, even if it was longer.
                replacement = replacement.Take(incomingFinalized + 1).ToArray();

                blocks.Clear();
                blocks.AddRange(replacement);
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
            return transactions.OrderByDescending(item => item == null ? long.MinValue : item.Fee)
                .ThenBy(item => item == null ? long.MaxValue : item.TimestampUtcTicks)
                .ThenBy(item => item == null ? null : item.Id, StringComparer.Ordinal).ToArray();
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
                foreach (Transaction transaction in pendingTransactions) Apply(transaction, utxo, false);
                return utxo.Values.Where(x => wanted.Contains(x.Output.OneTimeAddress)).ToArray();
            }
        }

        /// <summary>
        /// Returns confirmed outputs that are not already reserved as inputs by an
        /// ordered set of valid pending transactions. Outputs created by pending
        /// transactions are intentionally excluded until their block is confirmed.
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
                return BuildUtxo().Values.Where(item => wanted.Contains(item.Output.OneTimeAddress) &&
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
                foreach (Transaction transaction in pendingTransactions) Apply(transaction, utxo, false);
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

        public IReadOnlyList<ValidatorStake> GetEligibleValidators(IEnumerable<Transaction> transactions)
        { return ExcludeTransactionParticipants(GetActiveValidators(), transactions).ToArray(); }

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
                if (blocks.Count == 0) return false;
                if (!IsCanonicalGenesis(blocks[0])) return false;
                for (int i = 0; i < blocks.Count; i++)
                {
                    var block = blocks[i];
                    if (block.Height != i || block.Hash != block.CalculateHash() || !block.Hash.StartsWith(ProofPrefix, StringComparison.Ordinal)) return false;
                    if (i > 0 && block.PreviousHash != blocks[i - 1].Hash) return false;
                }
                try { ValidateWholeChain(); if (Finality != null) Finality.FinalizedHeight(blocks); return true; }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException ||
                    error is CryptographicException || error is OverflowException) { return false; }
            }
        }

        private void ValidateTransactions(IList<Transaction> pending)
        {
            var utxo = BuildUtxo();
            foreach (var transaction in OrderByFeePriority(pending)) Apply(transaction, utxo, false);
        }

        private void ValidateWholeChain()
        {
            var utxo = new Dictionary<string, UnspentOutput>();
            Block genesis = blocks[0];
            if (genesis.Transactions.Count != 0) throw new InvalidOperationException("The genesis block must not issue tokens.");
            long issued = 0;
            long validatorIssued = 0;

            for (int blockIndex = 1; blockIndex < blocks.Count; blockIndex++)
            {
                Block block = blocks[blockIndex];
                int regularTransactionIndex = 0;
                if (block.Validators != null && block.Validators.Count > 0)
                {
                    ValidateProofOfStakeBlock(block, blocks[blockIndex - 1].Hash, utxo);
                    validatorIssued = checked(validatorIssued + ProofOfStake.GetBlockReward(block.Height));
                    regularTransactionIndex = 1;
                }
                else if (block.Transactions.Count > 0 && block.Transactions[0].Inputs.Count == 0)
                {
                    Transaction reward = block.Transactions[0];
                    if (!IsWalletCreationReward(reward)) throw new InvalidOperationException("Invalid wallet creation reward.");
                    if (block.Transactions.Count != 1) throw new InvalidOperationException("A wallet creation reward must have its own block.");
                    Apply(reward, utxo, true);
                    issued = checked(issued + WalletCreationReward);
                    if (issued > DistributionSupply) throw new InvalidOperationException("The wallet distribution supply was exceeded.");
                    regularTransactionIndex = 1;
                }
                for (int transactionIndex = regularTransactionIndex; transactionIndex < block.Transactions.Count; transactionIndex++)
                    Apply(block.Transactions[transactionIndex], utxo, false);
            }
            if (issued > MaximumSupply || validatorIssued > ProofOfStake.MaximumSupply) throw new InvalidOperationException("Invalid supply.");
        }

        private void ValidateProofOfStakeBlock(Block block, string previousHash, IDictionary<string, UnspentOutput> utxo)
        {
            if (block.Transactions.Count == 0) throw new InvalidOperationException("A proof-of-stake block must contain its reward.");
            BlockValidator[] records = block.Validators.ToArray();
            if (records.Length < 2 || records.Any(item => item == null) || records.Count(item => item.IsCreator) != 1)
                throw new InvalidOperationException("Invalid proof-of-stake validator proof.");
            var stakes = records.Select(item => new ValidatorStake(item.ValidatorId, item.RewardAddress, item.LockedAmount,
                item.OwnedAddresses ?? new List<string> { item.RewardAddress }, item.PublicKey, null)).ToArray();
            foreach (BlockValidator record in records)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.PublicKey) ||
                    record.ValidatorId != Crypto.Sha256(record.PublicKey) ||
                    (((!AllowCreatorOnlyProof && Finality == null) || record.IsCreator || !string.IsNullOrWhiteSpace(record.VoteSignature)) &&
                     !ProofOfStake.VerifyVote(record.PublicKey, CreateVotePayload(block), record.VoteSignature)))
                    throw new InvalidOperationException("Invalid individual validator vote signature.");
                bool collateralExists = utxo.Values.Any(item => item.TransactionKind == TransactionKind.StakeLock &&
                    item.ValidatorPublicKey == record.PublicKey && item.ValidatorRewardAddress == record.RewardAddress &&
                    item.Output.Amount == record.LockedAmount);
                if (!collateralExists) throw new InvalidOperationException("The validator collateral is not globally locked on chain.");
            }
            Transaction[] transfers = block.Transactions.Skip(1).ToArray();
            ValidatorStake[] globallyEligible = ExcludeTransactionParticipants(StakesFromUtxo(utxo), transfers).ToArray();
            if (!SameStakeSet(stakes, globallyEligible))
                throw new InvalidOperationException("The validator proof does not contain the global eligible collateral set.");
            if (ExcludeTransactionParticipants(stakes, transfers).Count() != stakes.Length)
                throw new InvalidOperationException("A transfer sender or receiver cannot create or confirm its block.");
            ValidatorStake expectedCreator = ProofOfStake.SelectCreator(stakes, previousHash, block.Height);
            BlockValidator recordedCreator = records.Single(item => item.IsCreator);
            if (recordedCreator.ValidatorId != expectedCreator.ValidatorId)
                throw new InvalidOperationException("The recorded validator was not selected to create this block.");
            IReadOnlyList<ValidatorReward> expected = ProofOfStake.DistributeReward(block.Height, expectedCreator,
                stakes.Where(item => item.ValidatorId != expectedCreator.ValidatorId));
            long fees = transfers.Aggregate(0L, (total, transaction) => checked(total + transaction.Fee));
            Transaction reward = block.Transactions[0];
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

        private static IEnumerable<ValidatorStake> ExcludeTransactionParticipants(
            IEnumerable<ValidatorStake> validators,
            IEnumerable<Transaction> transactions)
        {
            var endpoints = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transaction transaction in transactions)
            {
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
                }
            return result;
        }

        private static void Apply(Transaction transaction, IDictionary<string, UnspentOutput> utxo, bool allowMint)
        {
            if (transaction == null || transaction.Id != transaction.CalculateId() || transaction.Outputs.Count == 0) throw new InvalidOperationException("Invalid transaction.");
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
            if (!allowMint && transaction.Fee < TransferFeeStep)
                throw new InvalidOperationException("The minimum transaction fee is one atomic unit.");
            if (!allowMint && inputTotal != checked(outputTotal + transaction.Fee))
                throw new InvalidOperationException("Inputs must equal outputs plus the transaction fee.");
            if (allowMint && transaction.Fee != 0) throw new InvalidOperationException("A reward transaction cannot declare a fee.");
            foreach (string id in used) utxo.Remove(id);
            AddOutputs(transaction, utxo);
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
                        block.Height, blocks[blocks.Count - 1].Height - block.Height + 1))).ToArray();
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
                return GetUnspentOutputs(addresses).Where(item => item.Output.AssetId == tokenId &&
                    !reserved.Contains(Key(item.TransactionId, item.OutputIndex))).ToArray();
            }
        }

        private static void AddOutputs(Transaction transaction, IDictionary<string, UnspentOutput> utxo)
        {
            for (int i = 0; i < transaction.Outputs.Count; i++)
                utxo.Add(Key(transaction.Id, i), new UnspentOutput { TransactionId = transaction.Id, OutputIndex = i, Output = transaction.Outputs[i],
                    TransactionKind = i == 0 ? transaction.Kind : TransactionKind.Transfer,
                    ValidatorPublicKey = i == 0 ? transaction.ValidatorPublicKey : null,
                    ValidatorRewardAddress = i == 0 ? transaction.ValidatorRewardAddress : null,
                    ValidatorOwnedAddresses = i == 0 ? transaction.ValidatorOwnedAddresses : null });
        }

        private static string Key(string transactionId, int outputIndex) => transactionId + ":" + outputIndex;

        internal static string CreateVotePayload(Block block)
        {
            return block.Height.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + block.PreviousHash + "|" +
                string.Join("|", block.Transactions.Select(item => item.Id)) + "|" +
                string.Join("|", block.Validators.OrderBy(item => item.ValidatorId, StringComparer.Ordinal).Select(item =>
                    item.ValidatorId + ":" + item.RewardAddress + ":" + item.LockedAmount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + item.IsCreator));
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
            do { block.Nonce++; block.Hash = block.CalculateHash(); }
            while (!block.Hash.StartsWith(ProofPrefix, StringComparison.Ordinal));
        }
    }
}
