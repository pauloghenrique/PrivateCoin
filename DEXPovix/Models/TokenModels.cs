using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DEXPovix.Models
{
    public sealed class PrepareTokenRequest
    {
        public string Name { get; set; }
        public string Symbol { get; set; }
        public int Decimals { get; set; }
        public string Supply { get; set; }
        public string[] PublicKeys { get; set; }
        public string Recipient { get; set; }
        public string ChangeAddress { get; set; }
    }

    public sealed class SubmitTokenRequest
    {
        public string DraftId { get; set; }
        public string[] Signatures { get; set; }
    }

    public sealed class TokenRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Symbol { get; set; }
        public int Decimals { get; set; }
        public string Supply { get; set; }
        public string TransactionId { get; set; }
        public int? Height { get; set; }
        public string Status { get; set; }
    }

    public sealed class TokenDashboard
    {
        public TokenRow[] Tokens { get; set; }
        public int ConnectedPeers { get; set; }
        public int Height { get; set; }
        public string NetworkId { get; set; }
        public string Fee { get; set; }
    }

    public static class TokenAmounts
    {
        public static long Parse(string value, int decimals)
        {
            if (decimals < 0 || decimals > 8 || string.IsNullOrWhiteSpace(value) || value.Length > 40)
                throw new ArgumentException("Informe uma quantidade positiva com até 8 casas decimais.");
            string[] parts = value.Trim().Replace(',', '.').Split('.');
            if (parts.Length > 2 || parts.Any(part => part.Length == 0 || part.Any(c => c < '0' || c > '9')) ||
                (parts.Length == 2 && parts[1].Length > decimals))
                throw new ArgumentException("A quantidade excede as casas decimais escolhidas. Use vírgula ou ponto, sem separador de milhares.");
            string atomic = parts[0] + (parts.Length == 2 ? parts[1] : "").PadRight(decimals, '0');
            long amount;
            if (!long.TryParse(atomic, NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount <= 0)
                throw new ArgumentException("A quantidade deve ser positiva e caber em 64 bits.");
            return amount;
        }

        public static string Format(long amount, int decimals)
        {
            string value = amount.ToString(CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
            return decimals == 0 ? value : value.Insert(value.Length - decimals, ".");
        }
    }
}
