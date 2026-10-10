using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Desktop
{
    internal sealed class WalletBalanceSummary
    {
        private WalletBalanceSummary(long available, long pendingChange)
        {
            Available = available;
            PendingChange = pendingChange;
        }

        public long Available { get; }
        public long PendingChange { get; }
        public long Total => checked(Available + PendingChange);

        public static WalletBalanceSummary Read(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending)
        {
            if (wallet == null) return new WalletBalanceSummary(0, 0);
            Transaction[] queue = pending.ToArray();
            var addresses = new HashSet<string>(wallet.OwnedOneTimeAddresses, StringComparer.Ordinal);
            // This query validates the queue and keeps the spending rules in the Core.
            UnspentOutput[] spendable = chain.GetSpendableOutputs(addresses, queue).ToArray();
            long available = spendable.Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
            if (queue.Length == 0) return new WalletBalanceSummary(available, 0);

            var outgoingIds = new HashSet<string>(queue.Where(transaction => transaction.Inputs.Any(input =>
                OwnsInput(addresses, input))).Select(transaction => transaction.Id), StringComparer.Ordinal);
            var availableKeys = new HashSet<string>(spendable.Select(item => Key(item.TransactionId, item.OutputIndex)), StringComparer.Ordinal);

            // Show the sender's remaining change while its inputs are reserved. Incoming
            // payments, locked collateral and change already available are not added.
            long pendingChange = chain.GetUnspentOutputs(addresses, queue)
                .Where(item => item.Output.AssetId == null && item.TransactionKind != TransactionKind.StakeLock &&
                    outgoingIds.Contains(item.TransactionId) && !availableKeys.Contains(Key(item.TransactionId, item.OutputIndex)))
                .Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
            return new WalletBalanceSummary(available, pendingChange);
        }

        private static string Key(string transactionId, int outputIndex)
        {
            return transactionId + ":" + outputIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static bool OwnsInput(HashSet<string> addresses, TransactionInput input)
        {
            if (string.IsNullOrWhiteSpace(input.PublicKey)) return false;
            using (var sha = SHA256.Create())
            {
                string address = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(input.PublicKey))
                    .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
                return addresses.Contains(address);
            }
        }
    }
}
