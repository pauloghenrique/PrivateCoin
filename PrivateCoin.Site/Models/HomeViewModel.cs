using System;
using System.Collections.Generic;
using System.Globalization;
using PrivateCoin.Core;

namespace PrivateCoin.Site.Models
{
    public sealed class HomeViewModel
    {
        private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

        public BlockchainSummary Summary { get; set; }
        public IReadOnlyList<LedgerEntry> Ledger { get; set; }
        public IReadOnlyList<Transaction> ValidationQueue { get; set; }
        public string[] ConnectedPeers { get; set; }
        public int KnownPeerCount { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public static string FormatTokens(long atomicAmount)
        {
            return (atomicAmount / (decimal)Blockchain.OneCoin).ToString("N8", Portuguese) + " PRIVATE";
        }

        public static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            return value.Substring(0, Math.Min(12, value.Length));
        }

        public static string EntryTypeName(LedgerEntryType type)
        {
            switch (type)
            {
                case LedgerEntryType.InitialDistribution: return "Emissão inicial";
                case LedgerEntryType.ValidatorReward: return "Recompensa";
                default: return "Transferência";
            }
        }
    }
}
