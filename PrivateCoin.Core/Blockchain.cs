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
        // One atomic unit: 0.00000001 PONEX.
        public const long TransferFeeStep = 1L;
        public const long MaximumTransferFee = OneCoin;
        private const string ProofPrefix = "000";
        private readonly object sync = new object();
        private readonly List<Block> blocks = new List<Block>();

        public Blockchain()
        {
            var genesis = new Block { Height = 0, PreviousHash = new string('0', 64), TimestampUtcTicks = DateTime.UtcNow.Ticks };
            Mine(genesis);
            blocks.Add(genesis);
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
                var pending = OrderByFeePriority(transactions).ToList();
                ValidateTransactions(pending);
                ValidatorStake[] active = ExcludeTransactionParticipants(validators, pending).ToArray();
                if (active.Length < 2) throw new InvalidOperationException("At least two active validators are required to create and confirm a block.");
                int height = blocks.Count;
                ValidatorStake creator = ProofOfStake.SelectCreator(active, blocks[blocks.Count - 1].Hash, height);
                ValidatorStake[] confirmers = active.Where(item => item.ValidatorId != creator.ValidatorId).ToArray();
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
                    Validators = active.OrderBy(item => item.ValidatorId, StringComparer.Ordinal).Select(item => new BlockValidator
                    {
                        ValidatorId = item.ValidatorId,
                        RewardAddress = item.RewardAddress,
                        LockedAmount = item.LockedAmount,
                        IsCreator = item.ValidatorId == creator.ValidatorId,
                        OwnedAddresses = item.OwnedAddresses.OrderBy(address => address, StringComparer.Ordinal).ToList()
                    }).ToList()
                };
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

        private int CountWalletCreationRewards()
        {
            return blocks.Skip(1).Count(block => block.Transactions.Count > 0 && IsWalletCreationReward(block.Transactions[0]));
        }

        /// <summary>
        /// Adopts a valid chain selected by a deterministic longest-chain rule.
        /// The tip hash breaks ties so two newly connected nodes also converge when
        /// they were created independently at the same height.
        /// </summary>
        public bool TryReplaceChain(IEnumerable<Block> candidateBlocks)
        {
            if (candidateBlocks == null) throw new ArgumentNullException(nameof(candidateBlocks));
            var candidate = new Blockchain(candidateBlocks);
            Block[] replacement = candidate.Blocks.ToArray();

            lock (sync)
            {
                bool isBetter = replacement.Length > blocks.Count ||
                    (replacement.Length == blocks.Count &&
                     string.CompareOrdinal(replacement[replacement.Length - 1].Hash, blocks[blocks.Count - 1].Hash) < 0);
                if (!isBetter) return false;

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
            return GetUnspentOutputs(addresses).Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
        }

        /// <summary>
        /// Returns the projected balance after applying the ordered pending
        /// transactions. This is a preview only; the confirmed balance returned by
        /// the overload without pending transactions changes only when a block is added.
        /// </summary>
        public long GetBalance(IEnumerable<string> addresses, IEnumerable<Transaction> pendingTransactions)
        {
            return GetUnspentOutputs(addresses, pendingTransactions)
                .Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
        }

        public bool IsValid()
        {
            lock (sync)
            {
                if (blocks.Count == 0) return false;
                for (int i = 0; i < blocks.Count; i++)
                {
                    var block = blocks[i];
                    if (block.Height != i || block.Hash != block.CalculateHash() || !block.Hash.StartsWith(ProofPrefix, StringComparison.Ordinal)) return false;
                    if (i > 0 && block.PreviousHash != blocks[i - 1].Hash) return false;
                }
                try { ValidateWholeChain(); return true; } catch (InvalidOperationException) { return false; }
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

        private static void ValidateProofOfStakeBlock(Block block, string previousHash, IDictionary<string, UnspentOutput> utxo)
        {
            if (block.Transactions.Count == 0) throw new InvalidOperationException("A proof-of-stake block must contain its reward.");
            BlockValidator[] records = block.Validators.ToArray();
            if (records.Length < 2 || records.Count(item => item.IsCreator) != 1)
                throw new InvalidOperationException("Invalid proof-of-stake validator proof.");
            var stakes = records.Select(item => new ValidatorStake(item.ValidatorId, item.RewardAddress, item.LockedAmount,
                item.OwnedAddresses ?? new List<string> { item.RewardAddress })).ToArray();
            Transaction[] transfers = block.Transactions.Skip(1).ToArray();
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
            long inputTotal = 0;
            var used = new HashSet<string>();
            byte[] payload = Encoding.UTF8.GetBytes(transaction.SigningPayload());
            foreach (var input in transaction.Inputs)
            {
                string id = Key(input.TransactionId, input.OutputIndex);
                UnspentOutput source;
                if (!used.Add(id) || !utxo.TryGetValue(id, out source)) throw new InvalidOperationException("Missing or already spent input.");
                if (Crypto.Sha256(input.PublicKey) != source.Output.OneTimeAddress) throw new InvalidOperationException("Input does not own the output.");
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(input.PublicKey);
                    if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(input.Signature))) throw new InvalidOperationException("Invalid signature.");
                }
                inputTotal = checked(inputTotal + source.Output.Amount);
            }
            long outputTotal = 0;
            foreach (var output in transaction.Outputs)
            {
                if (output.Amount <= 0 || string.IsNullOrWhiteSpace(output.OneTimeAddress)) throw new InvalidOperationException("Invalid output.");
                outputTotal = checked(outputTotal + output.Amount);
            }
            if (transaction.Fee < 0) throw new InvalidOperationException("A transaction fee cannot be negative.");
            if (!allowMint && transaction.Fee < TransferFeeStep)
                throw new InvalidOperationException("The minimum transaction fee is one atomic unit.");
            if (!allowMint && inputTotal != checked(outputTotal + transaction.Fee))
                throw new InvalidOperationException("Inputs must equal outputs plus the transaction fee.");
            if (allowMint && transaction.Fee != 0) throw new InvalidOperationException("A reward transaction cannot declare a fee.");
            foreach (string id in used) utxo.Remove(id);
            AddOutputs(transaction, utxo);
        }

        private static void AddOutputs(Transaction transaction, IDictionary<string, UnspentOutput> utxo)
        {
            for (int i = 0; i < transaction.Outputs.Count; i++)
                utxo.Add(Key(transaction.Id, i), new UnspentOutput { TransactionId = transaction.Id, OutputIndex = i, Output = transaction.Outputs[i] });
        }

        private static string Key(string transactionId, int outputIndex) => transactionId + ":" + outputIndex;

        private static void Mine(Block block)
        {
            do { block.Nonce++; block.Hash = block.CalculateHash(); }
            while (!block.Hash.StartsWith(ProofPrefix, StringComparison.Ordinal));
        }
    }
}
