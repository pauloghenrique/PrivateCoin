using System;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Core
{
    internal static class Crypto
    {
        internal static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
        }

        internal static string ToHex(byte[] value)
        {
            var result = new StringBuilder(value.Length * 2);
            foreach (byte item in value) result.Append(item.ToString("x2"));
            return result.ToString();
        }

        internal static string NewId()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return ToHex(bytes);
        }
    }
}
