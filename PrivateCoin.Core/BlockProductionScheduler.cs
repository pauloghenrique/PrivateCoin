using System;
using System.Collections.Generic;
using System.Linq;

namespace PrivateCoin.Core
{
    /// <summary>
    /// Local production policy: immediately select up to 20 ready, eligible operations.
    /// Mining and validator requirements still apply; no fixed collection delay.
    /// </summary>
    public sealed class BlockProductionScheduler
    {
        public TimeSpan RemainingWait => TimeSpan.Zero;
        public int CandidateCount { get; private set; }

        public BlockProductionScheduler(Func<TimeSpan> elapsed = null)
        {
            // Keep the optional clock argument for source compatibility; production no longer waits.
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
            return batch.ToArray();
        }
    }
}
