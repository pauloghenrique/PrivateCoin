using System;
using System.Collections.Generic;
using System.Linq;

namespace PrivateCoin.Core
{
    /// <summary>A point-in-time summary of the public blockchain and its emission.</summary>
    public sealed class BlockchainSummary
    {
        internal BlockchainSummary(int issuedBlockCount, long issuedTokenAmount,
            long currentReward, int initialDistributionBlocksIssued)
        {
            IssuedBlockCount = issuedBlockCount;
            IssuedTokenAmount = issuedTokenAmount;
            CurrentReward = currentReward;
            InitialDistributionBlocksIssued = initialDistributionBlocksIssued;
            InitialDistributionBlocksRemaining = Math.Max(0,
                Blockchain.RewardedWalletLimit - initialDistributionBlocksIssued);
            InitialDistributionTokenAmount = checked(
                (long)initialDistributionBlocksIssued * Blockchain.WalletCreationReward);
        }

        /// <summary>Number of emitted blocks, excluding the genesis block.</summary>
        public int IssuedBlockCount { get; }

        /// <summary>Tokens minted so far, in atomic units. Transaction fees are not issuance.</summary>
        public long IssuedTokenAmount { get; }

        /// <summary>Proof-of-stake reward for the next block height, in atomic units.</summary>
        public long CurrentReward { get; }

        /// <summary>Number of six-coin initial-distribution blocks already emitted.</summary>
        public int InitialDistributionBlocksIssued { get; }

        /// <summary>Number of six-coin initial-distribution blocks still available.</summary>
        public int InitialDistributionBlocksRemaining { get; }

        /// <summary>Tokens emitted by the initial wallet distribution, in atomic units.</summary>
        public long InitialDistributionTokenAmount { get; }

        /// <summary>Maximum tokens defined by the initial and proof-of-stake schedules.</summary>
        public long MaximumTokenAmount => checked(Blockchain.MaximumSupply + ProofOfStake.MaximumSupply);
    }

    public enum LedgerEntryType
    {
        InitialDistribution,
        ValidatorReward,
        Transfer
    }

    /// <summary>A transaction entry in the public cash book.</summary>
    public sealed class LedgerEntry
    {
        internal LedgerEntry(int blockHeight, string blockHash, Transaction transaction,
            LedgerEntryType type, long inputAmount, long outputAmount, long issuedAmount)
        {
            BlockHeight = blockHeight;
            BlockHash = blockHash;
            TransactionId = transaction.Id;
            TimestampUtcTicks = transaction.TimestampUtcTicks;
            Type = type;
            InputAmount = inputAmount;
            OutputAmount = outputAmount;
            Fee = transaction.Fee;
            IssuedAmount = issuedAmount;
            Outputs = transaction.Outputs.Select(output => new LedgerOutput(output.OneTimeAddress, output.Amount)).ToArray();
        }

        public int BlockHeight { get; }
        public string BlockHash { get; }
        public string TransactionId { get; }
        public long TimestampUtcTicks { get; }
        public LedgerEntryType Type { get; }
        public long InputAmount { get; }
        public long OutputAmount { get; }
        public long Fee { get; }
        public long IssuedAmount { get; }
        public IReadOnlyList<LedgerOutput> Outputs { get; }
    }

    /// <summary>A destination and amount recorded by a cash-book entry.</summary>
    public sealed class LedgerOutput
    {
        internal LedgerOutput(string oneTimeAddress, long amount)
        {
            OneTimeAddress = oneTimeAddress;
            Amount = amount;
        }

        public string OneTimeAddress { get; }
        public long Amount { get; }
    }

    /// <summary>
    /// Read-only query API for explorers, wallets and other consumers of PrivateCoin.Core.
    /// Every query works from one chain snapshot, so a result cannot mix different tips.
    /// </summary>
    public sealed class BlockchainQueryApi
    {
        private readonly Blockchain blockchain;

        public BlockchainQueryApi(Blockchain blockchain)
        {
            this.blockchain = blockchain ?? throw new ArgumentNullException(nameof(blockchain));
        }

        public BlockchainSummary GetSummary()
        {
            Block[] snapshot = blockchain.Blocks.ToArray();
            int initialBlocks = snapshot.Skip(1).Count(IsInitialDistributionBlock);
            long validatorIssuance = snapshot.Skip(1)
                .Where(IsProofOfStakeBlock)
                .Aggregate(0L, (total, block) => checked(total + ProofOfStake.GetBlockReward(block.Height)));
            long issued = checked((long)initialBlocks * Blockchain.WalletCreationReward + validatorIssuance);
            return new BlockchainSummary(Math.Max(0, snapshot.Length - 1), issued,
                ProofOfStake.GetBlockReward(snapshot.Length), initialBlocks);
        }

        /// <summary>Returns emitted blocks in ascending height order. Genesis is optional.</summary>
        public IReadOnlyList<Block> GetBlocks(int offset, int limit, bool includeGenesis = false)
        {
            ValidatePage(offset, limit);
            IEnumerable<Block> blocks = blockchain.Blocks;
            if (!includeGenesis) blocks = blocks.Skip(1);
            return blocks.Skip(offset).Take(limit).ToArray();
        }

        /// <summary>Returns a block by height, or null when that height has not been emitted.</summary>
        public Block GetBlock(int height)
        {
            if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
            return blockchain.Blocks.FirstOrDefault(block => block.Height == height);
        }

        /// <summary>Returns the public cash book in block and transaction order.</summary>
        public IReadOnlyList<LedgerEntry> GetLedger(int offset, int limit)
        {
            ValidatePage(offset, limit);
            Block[] snapshot = blockchain.Blocks.ToArray();
            var knownOutputs = new Dictionary<string, long>(StringComparer.Ordinal);
            var entries = new List<LedgerEntry>();

            foreach (Block block in snapshot)
            {
                for (int transactionIndex = 0; transactionIndex < block.Transactions.Count; transactionIndex++)
                {
                    Transaction transaction = block.Transactions[transactionIndex];
                    long inputAmount = transaction.Inputs.Aggregate(0L, (total, input) =>
                    {
                        long amount;
                        if (!knownOutputs.TryGetValue(OutputKey(input.TransactionId, input.OutputIndex), out amount))
                            throw new InvalidOperationException("The blockchain cash book contains an unknown input.");
                        return checked(total + amount);
                    });
                    long outputAmount = transaction.Outputs.Aggregate(0L,
                        (total, output) => checked(total + output.Amount));
                    LedgerEntryType type = LedgerEntryType.Transfer;
                    long issuedAmount = 0;
                    if (transactionIndex == 0 && IsProofOfStakeBlock(block))
                    {
                        type = LedgerEntryType.ValidatorReward;
                        issuedAmount = ProofOfStake.GetBlockReward(block.Height);
                    }
                    else if (transactionIndex == 0 && IsInitialDistributionBlock(block))
                    {
                        type = LedgerEntryType.InitialDistribution;
                        issuedAmount = Blockchain.WalletCreationReward;
                    }
                    entries.Add(new LedgerEntry(block.Height, block.Hash, transaction, type,
                        inputAmount, outputAmount, issuedAmount));

                    for (int outputIndex = 0; outputIndex < transaction.Outputs.Count; outputIndex++)
                        knownOutputs.Add(OutputKey(transaction.Id, outputIndex), transaction.Outputs[outputIndex].Amount);
                }
            }
            return entries.Skip(offset).Take(limit).ToArray();
        }

        private static bool IsProofOfStakeBlock(Block block)
        {
            return block.Validators != null && block.Validators.Count > 0;
        }

        private static bool IsInitialDistributionBlock(Block block)
        {
            if (IsProofOfStakeBlock(block) || block.Transactions.Count != 1) return false;
            Transaction transaction = block.Transactions[0];
            return transaction.Inputs.Count == 0 && transaction.Outputs.Count == 1 &&
                transaction.Outputs[0].Amount == Blockchain.WalletCreationReward;
        }

        private static void ValidatePage(int offset, int limit)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        }

        private static string OutputKey(string transactionId, int outputIndex)
        {
            return transactionId + ":" + outputIndex;
        }
    }
}
