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
        private WalletBalanceSummary(long available, long pendingChange, long launchLocked = 0)
        {
            Available = available;
            PendingChange = pendingChange;
            LaunchLocked = launchLocked;
        }

        public long Available { get; }
        public long PendingChange { get; }
        public long LaunchLocked { get; }
        public long Total => checked(Available + PendingChange + LaunchLocked);
        public string LaunchLockText => LaunchLocked > 0
            ? "Bloqueado no lançamento: " + ((decimal)LaunchLocked / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " POVIX"
            : string.Empty;
        public string LaunchLockDescription => LaunchLocked > 0
            ? LaunchLockText + ". O recebimento já está em garantia para validação e criação de blocos. Só será liberado para movimentação quando a pool atingir a meta. Mantenha a carteira e o nó conectados para participar."
            : string.Empty;

        public static WalletBalanceSummary Read(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending)
        {
            if (wallet == null) return new WalletBalanceSummary(0, 0);
            Transaction[] queue = pending.ToArray();
            var addresses = new HashSet<string>(wallet.OwnedOneTimeAddresses, StringComparer.Ordinal);
            // This query validates the queue and keeps the spending rules in the Core.
            UnspentOutput[] spendable = chain.GetSpendableOutputs(addresses, queue).ToArray();
            long available = spendable.Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
            long launchLocked = chain.GetInitialLaunchLockedBalance(addresses);
            if (queue.Length == 0) return new WalletBalanceSummary(available, 0, launchLocked);

            var outgoingIds = new HashSet<string>(queue.Where(transaction => transaction.Inputs.Any(input =>
                OwnsInput(addresses, input))).Select(transaction => transaction.Id), StringComparer.Ordinal);
            var availableKeys = new HashSet<string>(spendable.Select(item => Key(item.TransactionId, item.OutputIndex)), StringComparer.Ordinal);

            // Show the sender's remaining change while its inputs are reserved. Incoming
            // payments, locked collateral and change already available are not added.
            long pendingChange = chain.GetUnspentOutputs(addresses, queue)
                .Where(item => item.Output.AssetId == null && item.TransactionKind != TransactionKind.StakeLock && !item.IsPoolReserve &&
                    !item.Output.InitialLaunchLocked && outgoingIds.Contains(item.TransactionId) && !availableKeys.Contains(Key(item.TransactionId, item.OutputIndex)))
                .Aggregate(0L, (total, item) => checked(total + item.Output.Amount));
            return new WalletBalanceSummary(available, pendingChange, launchLocked);
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
