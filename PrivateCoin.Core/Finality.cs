using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.Serialization;

namespace PrivateCoin.Core
{
    [DataContract]
    public sealed class FinalityVote
    {
        [DataMember(Order = 1)] public int Height { get; set; }
        [DataMember(Order = 2)] public string BlockHash { get; set; }
        [DataMember(Order = 3)] public string PreviousHash { get; set; }
        [DataMember(Order = 4)] public string PublicKey { get; set; }
        [DataMember(Order = 5)] public string Signature { get; set; }

        internal string Payload() => "povix-finality-v1|" + Blockchain.NetworkId + "|" +
            Blockchain.ConsensusVersion.ToString(CultureInfo.InvariantCulture) + "|" +
            Height.ToString(CultureInfo.InvariantCulture) + "|" + BlockHash + "|" + PreviousHash;

        internal FinalityVote Copy() => new FinalityVote { Height = Height, BlockHash = BlockHash,
            PreviousHash = PreviousHash, PublicKey = PublicKey, Signature = Signature };
    }

    [DataContract]
    public sealed class FinalityCertificate
    {
        [DataMember(Order = 1)] public int Height { get; set; }
        [DataMember(Order = 2)] public string BlockHash { get; set; }
        [DataMember(Order = 3)] public List<FinalityVote> Votes { get; set; }
    }

    [DataContract]
    public sealed class FinalityState
    {
        [DataMember(Order = 1)] public int FinalizedHeight { get; set; }
        [DataMember(Order = 2)] public string FinalizedHash { get; set; }
        [DataMember(Order = 3)] public List<FinalityCertificate> Certificates { get; set; } = new List<FinalityCertificate>();
        [DataMember(Order = 4, EmitDefaultValue = false)] public List<FinalityVote> PendingVotes { get; set; } = new List<FinalityVote>();
    }

    [DataContract]
    public sealed class BlockchainSnapshot
    {
        [DataMember(Order = 1)] public Block[] Blocks { get; internal set; }
        [DataMember(Order = 2)] public FinalityState Finality { get; internal set; }
    }

    public sealed partial class Blockchain
    {
        private int finalizedHeight;
        private string finalizedHash = GenesisHash;
        private readonly List<FinalityCertificate> certificates = new List<FinalityCertificate>();
        private readonly Dictionary<string, FinalityVote> pendingFinalityVotes = new Dictionary<string, FinalityVote>(StringComparer.Ordinal);

        public int FinalizedHeight { get { lock (sync) return finalizedHeight; } }
        public string FinalizedHash { get { lock (sync) return finalizedHash; } }

        public BlockchainSnapshot GetSnapshot()
        {
            lock (sync) return new BlockchainSnapshot { Blocks = blocks.ToArray(), Finality = GetFinalityState() };
        }

        public Blockchain(IEnumerable<Block> existingBlocks, string localNodeId, FinalityState finality)
            : this(existingBlocks, localNodeId)
        {
            if (finality != null) ImportFinality(finality, true);
        }

        public FinalityState GetFinalityState()
        {
            lock (sync) return new FinalityState { FinalizedHeight = finalizedHeight, FinalizedHash = finalizedHash,
                Certificates = certificates.Select(CopyCertificate).ToList(),
                PendingVotes = pendingFinalityVotes.Values.Select(v => v.Copy()).ToList() };
        }

        private static FinalityCertificate CopyCertificate(FinalityCertificate certificate) => new FinalityCertificate
        { Height = certificate.Height, BlockHash = certificate.BlockHash, Votes = certificate.Votes.Select(v => v.Copy()).ToList() };

        // After bootstrap, certificates must be contiguous. Stake changes only become
        // voting weight once their parent history has itself been finalized.
        public int NextFinalityHeight
        {
            get
            {
                lock (sync)
                {
                    if (finalizedHeight > 0)
                        return finalizedHeight + 1 < blocks.Count && PreviousValidators(finalizedHeight + 1).Length > 0
                            ? finalizedHeight + 1 : -1;
                    for (int height = 1; height < blocks.Count; height++)
                        if (PreviousValidators(height).Length > 0) return height;
                    return -1;
                }
            }
        }

        public IReadOnlyList<ValidatorStake> GetFinalityValidators(int height)
        {
            lock (sync)
            {
                if (height <= 0 || height >= blocks.Count) throw new ArgumentOutOfRangeException(nameof(height));
                return PreviousValidators(height);
            }
        }

        private ValidatorStake[] PreviousValidators(int height)
        {
            var utxo = new Dictionary<string, UnspentOutput>(StringComparer.Ordinal);
            foreach (Block block in blocks.Take(height))
                foreach (Transaction transaction in block.Transactions)
                {
                    foreach (TransactionInput input in transaction.Inputs) utxo.Remove(Key(input.TransactionId, input.OutputIndex));
                    AddOutputs(transaction, utxo);
                }
            // Includes all previous stake, even transaction participants excluded from rewards.
            return StakesFromUtxo(utxo).ToArray();
        }

        public FinalityVote CreateFinalityVote(int height, ValidatorStake validator)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            lock (sync)
            {
                if (height != NextFinalityHeight || height <= 0)
                    throw new InvalidOperationException("Only the next eligible block can receive a finality vote.");
                if (!PreviousValidators(height).Any(v => v.PublicKey == validator.PublicKey))
                    throw new InvalidOperationException("The voter has no active stake in the preceding state.");
                var vote = new FinalityVote { Height = height, BlockHash = blocks[height].Hash,
                    PreviousHash = blocks[height - 1].Hash, PublicKey = validator.PublicKey };
                vote.Signature = validator.CreateVote(vote.Payload());
                return vote;
            }
        }

        private long ValidateFinalityVote(FinalityVote vote, ValidatorStake[] validators)
        {
            if (vote == null || vote.Height <= 0 || vote.Height >= blocks.Count ||
                vote.BlockHash != blocks[vote.Height].Hash || vote.PreviousHash != blocks[vote.Height - 1].Hash)
                throw new InvalidOperationException("Invalid finality vote: height or block hash does not match.");
            ValidatorStake stake = validators.SingleOrDefault(v => v.PublicKey == vote.PublicKey);
            if (stake == null) throw new InvalidOperationException("Finality voter is not active in the preceding state.");
            if (!ProofOfStake.VerifyVote(vote.PublicKey, vote.Payload(), vote.Signature))
                throw new InvalidOperationException("Invalid finality vote signature.");
            return stake.LockedAmount;
        }

        /// <summary>Collects a distinct validator vote; returns true only when the checkpoint advances.</summary>
        public bool AddFinalityVote(FinalityVote vote)
        {
            lock (sync)
            {
                if (vote == null) throw new ArgumentNullException(nameof(vote));
                if (vote.Height <= 0 || vote.Height >= blocks.Count) throw new InvalidOperationException("The voted block is unknown.");
                ValidatorStake[] validators = PreviousValidators(vote.Height);
                ValidateFinalityVote(vote, validators);
                if (vote.Height <= finalizedHeight) return false;
                if (vote.Height != NextFinalityHeight) throw new InvalidOperationException("A finality vote cannot skip an unfinalized parent.");
                if (pendingFinalityVotes.ContainsKey(vote.PublicKey)) return false;
                pendingFinalityVotes.Add(vote.PublicKey, vote.Copy());
                BigInteger total = validators.Aggregate(BigInteger.Zero, (sum, v) => sum + v.LockedAmount);
                BigInteger signed = pendingFinalityVotes.Values.Aggregate(BigInteger.Zero,
                    (sum, v) => sum + validators.Single(stake => stake.PublicKey == v.PublicKey).LockedAmount);
                if (total == 0 || signed * 3 <= total * 2) return false;
                certificates.Add(new FinalityCertificate { Height = vote.Height, BlockHash = vote.BlockHash,
                    Votes = pendingFinalityVotes.Values.OrderBy(v => v.PublicKey, StringComparer.Ordinal).Select(v => v.Copy()).ToList() });
                finalizedHeight = vote.Height;
                finalizedHash = vote.BlockHash;
                pendingFinalityVotes.Clear();
                return true;
            }
        }

        private void ImportFinality(FinalityState state, bool exactCheckpoint)
        {
            if (state == null || state.Certificates == null) throw new InvalidOperationException("Missing finality evidence.");
            int previous = 0;
            foreach (FinalityCertificate certificate in state.Certificates)
            {
                if (certificate == null || certificate.Height <= previous || certificate.Votes == null || certificate.Votes.Count == 0 ||
                    certificate.Height >= blocks.Count || certificate.BlockHash != blocks[certificate.Height].Hash)
                    throw new InvalidOperationException("Invalid finality certificate.");
                previous = certificate.Height;
                ValidatorStake[] validators = PreviousValidators(certificate.Height);
                var seenVoters = new HashSet<string>(StringComparer.Ordinal);
                BigInteger weight = BigInteger.Zero;
                foreach (FinalityVote vote in certificate.Votes)
                {
                    if (vote == null || vote.Height != certificate.Height || vote.BlockHash != certificate.BlockHash ||
                        !seenVoters.Add(vote.PublicKey)) throw new InvalidOperationException("Duplicate or mismatched finality voter.");
                    weight += ValidateFinalityVote(vote, validators);
                }
                BigInteger total = validators.Aggregate(BigInteger.Zero, (sum, v) => sum + v.LockedAmount);
                if (total == 0 || weight * 3 <= total * 2) throw new InvalidOperationException("Finality requires strictly more than two thirds of preceding active stake.");
                if (certificate.Height > finalizedHeight)
                {
                    foreach (FinalityVote vote in certificate.Votes) AddFinalityVote(vote);
                    if (finalizedHeight != certificate.Height) throw new InvalidOperationException("Incomplete finality certificate.");
                }
            }
            var pendingVoters = new HashSet<string>(StringComparer.Ordinal);
            foreach (FinalityVote vote in state.PendingVotes ?? new List<FinalityVote>())
            {
                if (vote == null || !pendingVoters.Add(vote.PublicKey)) throw new InvalidOperationException("Duplicate pending finality voter.");
                AddFinalityVote(vote);
            }
            if (state.FinalizedHeight < 0 || state.FinalizedHeight >= blocks.Count ||
                state.FinalizedHash != blocks[state.FinalizedHeight].Hash ||
                (exactCheckpoint && (state.FinalizedHeight != finalizedHeight || state.FinalizedHash != finalizedHash)) ||
                (!exactCheckpoint && state.FinalizedHeight > finalizedHeight))
                throw new InvalidOperationException("The saved finality checkpoint does not match its verified certificates.");
        }
    }
}
