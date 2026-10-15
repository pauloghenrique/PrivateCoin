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
        public IReadOnlyDictionary<string, TokenDefinition> Tokens { get; set; }
        public IReadOnlyList<Transaction> ValidationQueue { get; set; }
        public string[] ConnectedPeers { get; set; }
        public int KnownPeerCount { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public static string FormatTokens(long atomicAmount)
        {
            return (atomicAmount / (decimal)Blockchain.OneCoin).ToString("N8", Portuguese) + " POVIX";
        }

        public static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            return value.Substring(0, Math.Min(12, value.Length));
        }

        public static long LedgerAmount(LedgerEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (entry.Type == LedgerEntryType.LiquiditySwap)
                return entry.SwapInputAssetId == null ? entry.SwapInputAmount :
                    entry.SwapOutputAssetId == null ? entry.SwapOutputAmount : 0;

            // Token quantities and native change are not POVIX payments.
            if (IsTokenEntry(entry)) return 0;

            // Wallet transfers always place the payment first and an optional
            // refund to the sender second. The sum of all outputs therefore
            // includes the refund and is not the amount that was transferred.
            if (entry.Type == LedgerEntryType.Transfer && entry.Outputs.Count > 0)
                return entry.Outputs[0].Amount;

            return entry.OutputAmount;
        }

        public static string EntryTypeName(LedgerEntryType type)
        {
            switch (type)
            {
                case LedgerEntryType.InitialDistribution: return "Emissão inicial";
                case LedgerEntryType.ValidatorReward: return "Recompensa";
                case LedgerEntryType.TokenCreation: return "Criação de token";
                case LedgerEntryType.TokenTransfer: return "Transferência de token";
                case LedgerEntryType.LiquiditySwap: return "Troca";
                default: return "Transferência";
            }
        }

        public static string EntryTypeName(LedgerEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return entry.Type == LedgerEntryType.Transfer && IsTokenEntry(entry)
                ? "Transferência de token" : EntryTypeName(entry.Type);
        }

        private static bool IsTokenEntry(LedgerEntry entry)
        {
            if (entry.Type == LedgerEntryType.TokenCreation || entry.Type == LedgerEntryType.TokenTransfer)
                return true;
            // Use the transferred asset, even if the ledger labels this as a generic transfer.
            if (entry.Type == LedgerEntryType.Transfer)
                foreach (var output in entry.Outputs)
                    if (!string.IsNullOrEmpty(output.AssetId)) return true;
            return false;
        }

        public string FormatLedgerAmount(LedgerEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Type == LedgerEntryType.LiquiditySwap)
                return FormatAssetAmount(entry.SwapInputAmount, entry.SwapInputAssetId) + " → " +
                    FormatAssetAmount(entry.SwapOutputAmount, entry.SwapOutputAssetId);
            if (!IsTokenEntry(entry))
                return FormatTokens(LedgerAmount(entry));

            // The first token output is the recipient's quantity; later outputs include change.
            foreach (var output in entry.Outputs)
            {
                if (string.IsNullOrEmpty(output.AssetId)) continue;
                return FormatAssetAmount(output.Amount, output.AssetId);
            }
            return "—";
        }

        private string AssetName(string assetId)
        {
            if (assetId == null) return "POVIX";
            TokenDefinition token;
            return Tokens != null && Tokens.TryGetValue(assetId, out token) && !string.IsNullOrWhiteSpace(token.Name)
                ? token.Name : assetId;
        }

        public string FormatAssetAmount(long amount, string assetId)
        {
            if (assetId == null) return FormatTokens(amount);
            TokenDefinition token;
            if (Tokens == null || !Tokens.TryGetValue(assetId, out token))
                return amount.ToString("N0", Portuguese) + " unidades atômicas · " + assetId;
            decimal scale = 1;
            for (int index = 0; index < token.Decimals; index++) scale *= 10;
            return (amount / scale).ToString("N" + token.Decimals, Portuguese) + " " + AssetName(assetId);
        }

        public string LedgerTokenName(LedgerEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Type == LedgerEntryType.LiquiditySwap)
                return AssetName(entry.SwapInputAssetId) + " → " + AssetName(entry.SwapOutputAssetId);
            if (!IsTokenEntry(entry))
                return "—";

            foreach (var output in entry.Outputs)
            {
                if (string.IsNullOrEmpty(output.AssetId)) continue;
                TokenDefinition token;
                if (Tokens != null && Tokens.TryGetValue(output.AssetId, out token) && !string.IsNullOrWhiteSpace(token.Name))
                    return token.Name;
                return output.AssetId;
            }
            return "—";
        }
    }
}
