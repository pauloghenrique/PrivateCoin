using System;
using System.Globalization;

namespace PrivateCoin.Desktop
{
    internal static class TokenAmount
    {
        public static string Format(long amount, int decimals)
        {
            if (amount < 0 || decimals < 0 || decimals > 8) throw new ArgumentOutOfRangeException();
            decimal divisor = 1;
            for (int i = 0; i < decimals; i++) divisor *= 10;
            return ((decimal)amount / divisor).ToString("N" + decimals, CultureInfo.CurrentCulture);
        }
    }
}
