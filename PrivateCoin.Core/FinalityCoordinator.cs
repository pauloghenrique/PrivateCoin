using System;
using System.Collections.Generic;
using System.Linq;

namespace PrivateCoin.Core
{
    // Single-height finality: safe but deliberately conservative. Split votes
    // can halt progress; this is not a multi-round Byzantine agreement engine.
    public sealed class FinalityCoordinator
    {
        private readonly FinalityPolicy policy;
        private readonly FinalityVoteJournal journal;
        private readonly Dictionary<string, Block[]> proposals = new Dictionary<string, Block[]>(StringComparer.Ordinal);
        private readonly HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, FinalityVote>> votes = new Dictionary<string, Dictionary<string, FinalityVote>>(StringComparer.Ordinal);

        public FinalityCoordinator(FinalityPolicy policy, FinalityVoteJournal journal)
        { this.policy = policy ?? throw new ArgumentNullException(nameof(policy)); this.journal = journal; }

        public IReadOnlyList<Block[]> PendingProposals => proposals.Values.Select(p => FinalityPolicy.CopyBlocks(p)).ToArray();

        // Callers serialize these methods with their chain/persistence operations.
        public IReadOnlyList<FinalityVote> Observe(Blockchain local, IEnumerable<Block> received, IEnumerable<ValidatorStake> signers, bool resend = false)
        {
            var candidate = new Blockchain(received, policy);
            int height = local.FinalizedHeight;
            if (height < policy.AnchorHeight) return new FinalityVote[0];
            foreach (string key in proposals.Keys.Where(k => proposals[k].Last().Height <= height).ToArray())
            { proposals.Remove(key); votes.Remove(key); emitted.RemoveWhere(v => v.StartsWith(key + "|", StringComparison.Ordinal)); }
            Block[] chain = candidate.Blocks.Take(height + 2).ToArray();
            if (chain.Length != height + 2 || chain[height].Hash != local.Blocks[height].Hash)
                return new FinalityVote[0];
            Block block = chain.Last();
            ValidatorStake[] committee = FinalityPolicy.Committee(chain.Take(height + 1));
            FinalityPolicy.Committee(chain); // Never propose a committee that cannot finalize successors.
            if (!proposals.ContainsKey(block.Hash))
            {
                if (proposals.Count >= 32) return new FinalityVote[0];
                // Isolate untrusted arrays from callers; never mutate the block hash.
                proposals[block.Hash] = chain;
                votes[block.Hash] = new Dictionary<string, FinalityVote>(StringComparer.Ordinal);
            }
            var outbound = new List<FinalityVote>();
            if (journal == null) return outbound;
            foreach (ValidatorStake signer in signers ?? new ValidatorStake[0])
            {
                ValidatorStake registered = committee.SingleOrDefault(v => v.PublicKey == signer.PublicKey);
                string emissionKey = block.Hash + "|" + signer.PublicKey;
                if (registered == null || (!resend && emitted.Contains(emissionKey))) continue;
                try
                {
                    FinalityVote vote = journal.Sign(policy, block, signer);
                    Receive(local, vote);
                    outbound.Add(vote);
                    emitted.Add(emissionKey);
                }
                catch (InvalidOperationException) { /* An earlier vote locks this height. */ }
            }
            return outbound;
        }

        public bool Receive(Blockchain local, FinalityVote vote)
        {
            if (vote == null || vote.PublicKey == null || vote.BlockHash == null) return false;
            Block[] proposal;
            if (!proposals.TryGetValue(vote.BlockHash, out proposal)) return false;
            Block block = proposal.Last();
            if (vote.Height != block.Height || vote.Height != local.FinalizedHeight + 1 ||
                !ProofOfStake.VerifyVote(vote.PublicKey, policy.Payload(vote.Height, vote.BlockHash), vote.Signature)) return false;
            ValidatorStake[] committee = FinalityPolicy.Committee(proposal.Take(proposal.Length - 1));
            if (!committee.Any(v => v.PublicKey == vote.PublicKey)) return false;
            votes[vote.BlockHash][vote.PublicKey] = vote;
            return true;
        }

        public bool TryFinalize(Blockchain local)
        {
            foreach (Block[] proposal in proposals.Values)
            {
                Block block = proposal.Last();
                if (block.Height != local.FinalizedHeight + 1) continue;
                block.FinalityVotes = votes[block.Hash].Values.OrderBy(v => v.PublicKey, StringComparer.Ordinal).ToList();
                if (!policy.HasQuorum(block, FinalityPolicy.Committee(proposal.Take(proposal.Length - 1)))) continue;
                // Replacement preserves the common finalized prefix and validates
                // both transaction history and the complete certificate chain.
                return local.TryReplaceChain(proposal);
            }
            return false;
        }
    }
}
