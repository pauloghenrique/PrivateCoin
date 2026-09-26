using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Core
{
    public sealed class Blockchain
    {
        public const int DecimalPlaces = 8;
        public const long OneCoin = 100000000L;
        public const long InitialSupply = 0L;
        public const long WalletCreationDistribution = 180000L * OneCoin;
        public const long MaximumWalletCreationReward = 18L * OneCoin;
        public const long InitialMiningReward = 18L * OneCoin;
        public const int HalvingInterval = 180000;
        public const int MiningBlockSizeBytes = 2 * 1024 * 1024;
        public const long MaximumSupply = WalletCreationDistribution + (InitialMiningReward * HalvingInterval * 2L);
        private const string ProofPrefix = "000";
        private readonly object sync = new object();
        private readonly List<Block> blocks = new List<Block>();

        public Blockchain()
        {
            var genesis = new Block { Height = 0, PreviousHash = new string('0', 64), TimestampUtcTicks = DateTime.UtcNow.Ticks };
            Mine(genesis);
            blocks.Add(genesis);
        }

        [Obsolete("The genesis block no longer credits an address. Use Blockchain() instead.")]
        public Blockchain(string genesisOneTimeAddress) : this() { }

        /// <summary>Restores and validates an existing chain.</summary>
        public Blockchain(IEnumerable<Block> existingBlocks)
        {
            if (existingBlocks == null) throw new ArgumentNullException(nameof(existingBlocks));
            blocks.AddRange(existingBlocks);
            if (!IsValid()) throw new InvalidOperationException("The stored blockchain is invalid.");
        }

        public IReadOnlyList<Block> Blocks { get { lock (sync) return blocks.ToArray(); } }

        /// <summary>Returns the encoded size used to decide when pending transactions fill a mining block.</summary>
        public static long GetTransactionBatchSize(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            var batch = transactions.ToList();
            if (batch.Any(transaction => transaction == null)) throw new ArgumentException("Transactions cannot contain null values.", nameof(transactions));

            var serializer = new DataContractJsonSerializer(typeof(List<Transaction>));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, batch);
                return stream.Length;
            }
        }

        public Block AddBlock(IEnumerable<Transaction> transactions)
        {
            if (transactions == null) throw new ArgumentNullException(nameof(transactions));
            lock (sync)
            {
                var pending = transactions.ToList();
                ValidateTransactions(pending);
                var block = new Block { Height = blocks.Count, PreviousHash = blocks[blocks.Count - 1].Hash, TimestampUtcTicks = DateTime.UtcNow.Ticks, Transactions = pending };
                long reward = GetMiningReward(block.Height);
                if (reward > 0)
                {
                    var rewardTransaction = new Transaction { TimestampUtcTicks = block.TimestampUtcTicks, Kind = TransactionKind.MiningReward };
                    rewardTransaction.Outputs.Add(new TransactionOutput { Amount = reward, OneTimeAddress = miningRewardAddress });
                    rewardTransaction.Id = rewardTransaction.CalculateId();
                    block.Transactions.Insert(0, rewardTransaction);
                }
                Mine(block);
                blocks.Add(block);
                return block;
            }
        }

        /// <summary>
        /// Creates a block with a cryptographically random reward for a new wallet.
        /// Rewards are whole PRIVATE amounts and stop permanently when the shared
        /// 180,000 PRIVATE distribution pool has been exhausted.
        /// </summary>
        public bool TryAddWalletCreationReward(string rewardAddress, out Block rewardBlock)
        {
            if (string.IsNullOrWhiteSpace(rewardAddress)) throw new ArgumentException("A wallet reward address is required.", nameof(rewardAddress));
            lock (sync)
            {
                long distributed = GetWalletCreationDistribution();
                long remaining = WalletCreationDistribution - distributed;
                if (remaining <= 0)
                {
                    rewardBlock = null;
                    return false;
                }

                long maximum = Math.Min(MaximumWalletCreationReward, remaining);
                long reward = NextRandomWholeCoinAmount(maximum);
                var rewardTransaction = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks, Kind = TransactionKind.WalletCreationReward };
                rewardTransaction.Outputs.Add(new TransactionOutput { Amount = reward, OneTimeAddress = rewardAddress });
                rewardTransaction.Id = rewardTransaction.CalculateId();

                rewardBlock = new Block
                {
                    Height = blocks.Count,
                    PreviousHash = blocks[blocks.Count - 1].Hash,
                    TimestampUtcTicks = DateTime.UtcNow.Ticks
                };
                rewardBlock.Transactions.Add(rewardTransaction);
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

        public long GetBalance(IEnumerable<string> addresses)
        {
            return GetUnspentOutputs(addresses).Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
        }

        /// <summary>
        /// Returns the spendable balance after applying the ordered pending
        /// transactions. This lets a validated transfer take effect immediately;
        /// mining only confirms the accumulated transactions in a block.
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
            foreach (var transaction in pending) Apply(transaction, utxo, false);
        }

        private void ValidateWholeChain()
        {
            var utxo = new Dictionary<string, UnspentOutput>();
            Block genesis = blocks[0];
            if (genesis.Transactions.Count != 0) throw new InvalidOperationException("The genesis block must not issue tokens.");
            long issued = 0;
            long walletCreationDistribution = 0;

            for (int blockIndex = 1; blockIndex < blocks.Count; blockIndex++)
            {
                Block block = blocks[blockIndex];
                if (IsWalletCreationRewardBlock(block))
                {
                    Transaction walletReward = block.Transactions[0];
                    long amount = walletReward.Outputs.Count == 1 ? walletReward.Outputs[0].Amount : 0;
                    if (amount <= 0 || amount > MaximumWalletCreationReward || amount % OneCoin != 0 ||
                        amount > WalletCreationDistribution - walletCreationDistribution)
                        throw new InvalidOperationException("Invalid wallet creation reward.");
                    Apply(walletReward, utxo, true);
                    walletCreationDistribution = checked(walletCreationDistribution + amount);
                    issued = checked(issued + amount);
                    continue;
                }

                long expectedReward = GetMiningReward(blockIndex);
                int regularTransactionIndex = 0;
                if (block.Transactions.Count > 0 && block.Transactions[0].Inputs.Count == 0)
                {
                    Transaction reward = block.Transactions[0];
                    if (reward.Kind != TransactionKind.MiningReward || reward.Inputs.Count != 0 ||
                        reward.Outputs.Count != 1 || reward.Outputs[0].Amount != expectedReward)
                        throw new InvalidOperationException("Invalid mining reward.");
                    Apply(reward, utxo, true);
                    issued = checked(issued + WalletCreationReward);
                    if (issued > DistributionSupply) throw new InvalidOperationException("The wallet distribution supply was exceeded.");
                    regularTransactionIndex = 1;
                }
                for (int transactionIndex = regularTransactionIndex; transactionIndex < block.Transactions.Count; transactionIndex++)
                {
                    if (block.Transactions[transactionIndex].Kind != TransactionKind.Transfer)
                        throw new InvalidOperationException("Unexpected reward transaction.");
                    Apply(block.Transactions[transactionIndex], utxo, false);
                }
            }
            if (issued > MaximumSupply) throw new InvalidOperationException("Invalid supply.");
        }

        private long GetWalletCreationDistribution()
        {
            return blocks.Where(IsWalletCreationRewardBlock)
                .Aggregate(0L, (total, block) => checked(total + block.Transactions[0].Outputs[0].Amount));
        }

        private static bool IsWalletCreationRewardBlock(Block block)
        {
            return block.Transactions.Count == 1 && block.Transactions[0] != null &&
                block.Transactions[0].Kind == TransactionKind.WalletCreationReward;
        }

        private static long NextRandomWholeCoinAmount(long maximumAmount)
        {
            int maximumCoins = checked((int)(maximumAmount / OneCoin));
            if (maximumCoins <= 1) return OneCoin;

            // Rejection sampling avoids the modulo bias produced by a simple remainder.
            uint range = (uint)maximumCoins;
            uint limit = uint.MaxValue - (uint.MaxValue % range);
            var bytes = new byte[sizeof(uint)];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                uint value;
                do
                {
                    generator.GetBytes(bytes);
                    value = BitConverter.ToUInt32(bytes, 0);
                }
                while (value >= limit);
                return ((value % range) + 1L) * OneCoin;
            }
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
            if ((allowMint && transaction.Kind == TransactionKind.Transfer) ||
                (!allowMint && transaction.Kind != TransactionKind.Transfer))
                throw new InvalidOperationException("Invalid transaction kind.");
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
            if (!allowMint && inputTotal != outputTotal) throw new InvalidOperationException("Inputs and outputs must balance; fees are not supported.");
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
