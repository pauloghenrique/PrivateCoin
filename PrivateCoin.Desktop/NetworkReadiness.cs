using System;

namespace PrivateCoin.Desktop
{
    // Serializes chain changes with loss of connectivity. Epochs invalidate
    // chain callbacks queued in the UI before the last disconnection.
    internal sealed class NetworkReadiness
    {
        private readonly object sync = new object();
        private long epoch;
        private bool synchronized;
        public long Epoch { get { lock (sync) return epoch; } }
        public bool IsReady { get { lock (sync) return synchronized; } }

        public void Disconnect()
        {
            lock (sync) { synchronized = false; epoch++; }
        }

        public bool Accept(long expectedEpoch, Func<bool> connected, Action validateAndApply)
        {
            lock (sync)
            {
                if (epoch != expectedEpoch || !connected()) return false;
                validateAndApply();
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
