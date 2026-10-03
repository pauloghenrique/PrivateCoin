using System;
using System.Collections.Generic;
using System.Globalization;
using PrivateCoin.Core;

namespace PrivateCoin.Site.Models
{
    public sealed class HomeViewModel
    {
        private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

        public HomeViewModel(BlockchainSummary summary, IReadOnlyList<LedgerEntry> ledger)
        {
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        public BlockchainSummary Summary { get; }
        public IReadOnlyList<LedgerEntry> Ledger { get; }

        public static string FormatTokens(long atomicAmount)
        {
            decimal tokens = atomicAmount / (decimal)Blockchain.OneCoin;
            return tokens.ToString("N8", Portuguese) + " PRIVATE";
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
