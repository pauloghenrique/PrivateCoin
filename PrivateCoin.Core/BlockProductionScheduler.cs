using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PrivateCoin.Core
{
    /// <summary>
    /// Local production policy: up to 20 ready operations, or ten seconds from
    /// their first observation. This monotonic clock is not a consensus rule.
    /// </summary>
    public sealed class BlockProductionScheduler
    {
        private readonly Func<TimeSpan> elapsed;
        private readonly Dictionary<string, TimeSpan> readySince = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        public TimeSpan RemainingWait { get; private set; }
        public int CandidateCount { get; private set; }

        public BlockProductionScheduler(Func<TimeSpan> elapsed = null)
        {
            var clock = Stopwatch.StartNew();
            this.elapsed = elapsed ?? (() => clock.Elapsed);
        }

        /// <summary>
        /// Observe all ready operations, including overflow. The optional predicate
        /// keeps a batch compatible with the producer's available validator set.
        /// </summary>
        public IReadOnlyList<Transaction> SelectBatch(IEnumerable<Transaction> readyOperations,
            Func<IReadOnlyList<Transaction>, bool> canProduce = null)
        {
            Transaction[] ready = Blockchain.OrderByFeePriority(readyOperations).ToArray();
            var ids = new HashSet<string>(ready.Select(tx => tx.Id), StringComparer.Ordinal);
            if (ids.Count != ready.Length) throw new InvalidOperationException("Duplicate ready operation.");
            TimeSpan now = elapsed();
            foreach (string id in readySince.Keys.Where(id => !ids.Contains(id)).ToArray()) readySince.Remove(id);
            foreach (Transaction tx in ready)
                if (!readySince.ContainsKey(tx.Id)) readySince.Add(tx.Id, now);

            var batch = new List<Transaction>();
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transaction tx in ready)
            {
                if (tx.Inputs.Any(input => ids.Contains(input.TransactionId) && !selected.Contains(input.TransactionId))) continue;
                Transaction[] candidate = batch.Concat(new[] { tx }).ToArray();
                if (canProduce != null && !canProduce(candidate)) continue;
                batch.Add(tx);
                selected.Add(tx.Id);
                if (batch.Count == Blockchain.MaxOperationsPerBlock) break;
            }

            CandidateCount = batch.Count;
            RemainingWait = TimeSpan.Zero;
            if (batch.Count == 0) return new Transaction[0];
            TimeSpan wait = TimeSpan.FromSeconds(Blockchain.BlockWaitSeconds) - (now - batch.Min(tx => readySince[tx.Id]));
            if (batch.Count < Blockchain.MaxOperationsPerBlock && wait > TimeSpan.Zero)
            {
                RemainingWait = wait;
                return new Transaction[0];
            }
            return batch.ToArray();
        }
    }
}
