using System;
using System.Globalization;

namespace PrivateCoin.Desktop
{
    internal static class TokenAmount
    {
        public static long Parse(string value, int decimals)
        {
            if (decimals < 0 || decimals > 8) throw new ArgumentOutOfRangeException(nameof(decimals));
            string[] parts = (value ?? string.Empty).Trim().Split(
                new[] { CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator }, StringSplitOptions.None);
            // Check significant fractional digits before decimal.TryParse can round a long input.
            if (parts.Length == 2 && parts[1].TrimEnd('0').Length > decimals)
                throw new InvalidOperationException("Este token aceita no máximo " + decimals + " casa(s) decimal(is).");
            decimal quantity;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out quantity) || quantity <= 0)
                throw new InvalidOperationException("Informe uma quantidade de tokens maior que zero.");
            decimal multiplier = 1;
            for (int i = 0; i < decimals; i++) multiplier *= 10;
            decimal atomic;
            try { atomic = checked(quantity * multiplier); }
            catch (OverflowException) { throw new InvalidOperationException("A quantidade ultrapassa o limite permitido."); }
            if (decimal.Truncate(atomic) != atomic)
                throw new InvalidOperationException("Este token aceita no máximo " + decimals + " casa(s) decimal(is).");
            if (atomic > long.MaxValue)
                throw new InvalidOperationException("A quantidade ultrapassa o limite permitido.");
            return (long)atomic;
        }

        public static string Format(long amount, int decimals)
        {
            if (amount < 0 || decimals < 0 || decimals > 8) throw new ArgumentOutOfRangeException();
            decimal divisor = 1;
            for (int i = 0; i < decimals; i++) divisor *= 10;
            return ((decimal)amount / divisor).ToString("N" + decimals, CultureInfo.CurrentCulture);
        }
    }
}
