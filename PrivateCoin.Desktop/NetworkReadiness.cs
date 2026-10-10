using System;
using System.Collections.Generic;
using System.Linq;
using PrivateCoin.Core;

namespace PrivateCoin.Desktop
{
    // Serializes chain changes with loss of connectivity. Epochs invalidate
    // chain callbacks queued in the UI before the last disconnection.
    internal sealed class NetworkReadiness
    {
        private readonly object sync = new object();
        private long epoch;
        private bool synchronized;
        private readonly Dictionary<string, Transaction> deferred = new Dictionary<string, Transaction>(StringComparer.Ordinal);
        public long Epoch { get { lock (sync) return epoch; } }
        public bool IsReady { get { lock (sync) return synchronized; } }

        public void Disconnect()
        {
            lock (sync) { synchronized = false; epoch++; deferred.Clear(); }
        }

        public void Defer(long expectedEpoch, Transaction transaction)
        {
            lock (sync)
            {
                if (epoch != expectedEpoch || transaction == null || string.IsNullOrEmpty(transaction.Id)) return;
                Transaction previous;
                if (deferred.TryGetValue(transaction.Id, out previous))
                {
                    if (previous.TransactionApproval == null || transaction.TransactionApproval != null) deferred[transaction.Id] = transaction;
                }
                else if (deferred.Count < 1000) deferred.Add(transaction.Id, transaction);
            }
        }

        public Transaction[] TakeDeferred(long expectedEpoch)
        {
            lock (sync)
            {
                if (!synchronized || epoch != expectedEpoch) return new Transaction[0];
                Transaction[] transactions = deferred.Values.ToArray();
                deferred.Clear();
                return transactions;
            }
        }

        public bool Accept(long expectedEpoch, Func<bool> connected, Func<bool> validateAndApply)
        {
            lock (sync)
            {
                if (epoch != expectedEpoch || !connected()) return false;
                if (!validateAndApply()) return false;
                synchronized = connected();
                return synchronized;
            }
        }

        public T Execute<T>(Func<bool> connected, Func<T> operation)
        {
            lock (sync)
            {
                if (!synchronized || !connected())
                    throw new InvalidOperationException("Aguarde a conexão e a sincronização com a rede POVIX.");
                return operation();
            }
        }
    }
}
