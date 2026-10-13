using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace PrivateCoin.Core
{
    /// <summary>Creates reproducible RSA keys from a secret seed and an address index.</summary>
    internal static class DeterministicRsa
    {
        private const int PublicExponentValue = 65537;
        private const int PrimalityRounds = 40;
        private const int PrimeBytes = Crypto.RsaKeyBits / 16;
        private static readonly BigInteger PublicExponent = new BigInteger(PublicExponentValue);
        private static readonly int[] SmallPrimes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 };

        public static RSACryptoServiceProvider Create(byte[] seed, int index)
        {
            if (seed == null || seed.Length < Crypto.MinimumSeedBytes) throw new ArgumentException("A deterministic seed must contain at least 128 bits.", nameof(seed));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));

            BigInteger p = FindPrime(seed, index, 0);
            BigInteger q = FindPrime(seed, index, 1);
            if (p == q) q = FindPrime(seed, index, 2);
            if (p < q) { BigInteger swap = p; p = q; q = swap; }
            BigInteger modulus = p * q;
            BigInteger d = ModInverse(PublicExponent, (p - 1) * (q - 1));
            var parameters = new RSAParameters
            {
                Modulus = ToBigEndian(modulus, Crypto.RsaKeyBits / 8),
                Exponent = ToBigEndian(PublicExponent, 3),
                D = ToBigEndian(d, Crypto.RsaKeyBits / 8),
                P = ToBigEndian(p, PrimeBytes),
                Q = ToBigEndian(q, PrimeBytes),
                DP = ToBigEndian(d % (p - 1), PrimeBytes),
                DQ = ToBigEndian(d % (q - 1), PrimeBytes),
                InverseQ = ToBigEndian(ModInverse(q, p), PrimeBytes)
            };
            var rsa = new RSACryptoServiceProvider(Crypto.RsaKeyBits) { PersistKeyInCsp = false };
            rsa.ImportParameters(parameters);
            return rsa;
        }

        private static BigInteger FindPrime(byte[] seed, int index, int branch)
        {
            for (int attempt = 0; ; attempt++)
            {
                byte[] candidate = Expand(seed, index, branch, attempt, PrimeBytes);
                candidate[0] |= 0xC0;
                candidate[candidate.Length - 1] |= 1;
                BigInteger value = FromBigEndian(candidate);
                if (BigInteger.GreatestCommonDivisor(value - 1, PublicExponent) == 1 && IsProbablePrime(value)) return value;
            }
        }

        private static byte[] Expand(byte[] seed, int index, int branch, int attempt, int length)
        {
            byte[] result = new byte[length];
            using (var hmac = new HMACSHA256(seed))
            {
                for (int offset = 0, block = 0; offset < length; block++)
                {
                    byte[] input = BitConverter.GetBytes(index).Concat(BitConverter.GetBytes(branch))
                        .Concat(BitConverter.GetBytes(attempt)).Concat(BitConverter.GetBytes(block)).ToArray();
                    byte[] hash = hmac.ComputeHash(input);
                    int count = Math.Min(hash.Length, length - offset);
                    Buffer.BlockCopy(hash, 0, result, offset, count);
                    offset += count;
                }
            }
            return result;
        }

        private static bool IsProbablePrime(BigInteger value)
        {
            foreach (int small in SmallPrimes)
            {
                if (value == small) return true;
                if (value % small == 0) return false;
            }
            BigInteger d = value - 1;
            int power = 0;
            while (d.IsEven) { d >>= 1; power++; }
            byte[] encoded = ToBigEndian(value, PrimeBytes);
            for (int round = 0; round < PrimalityRounds; round++)
            {
                byte[] roundBytes = BitConverter.GetBytes(round);
                byte[] digest;
                using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(encoded.Concat(roundBytes).ToArray());
                BigInteger testBase = 2 + FromBigEndian(digest) % (value - 3);
                BigInteger x = BigInteger.ModPow(testBase, d, value);
                if (x == 1 || x == value - 1) continue;
                bool composite = true;
                for (int exponent = 1; exponent < power; exponent++)
                {
                    x = BigInteger.ModPow(x, 2, value);
                    if (x == value - 1) { composite = false; break; }
                }
                if (composite) return false;
            }
            return true;
        }

        private static BigInteger ModInverse(BigInteger value, BigInteger modulus)
        {
            BigInteger oldR = value, r = modulus, oldS = 1, s = 0;
            while (r != 0)
            {
                BigInteger quotient = oldR / r;
                BigInteger temporary = oldR - quotient * r; oldR = r; r = temporary;
                temporary = oldS - quotient * s; oldS = s; s = temporary;
            }
            return (oldS % modulus + modulus) % modulus;
        }

        private static BigInteger FromBigEndian(byte[] bytes)
        {
            return new BigInteger(bytes.Reverse().Concat(new byte[] { 0 }).ToArray());
        }

        private static byte[] ToBigEndian(BigInteger value, int length)
        {
            byte[] source = value.ToByteArray();
            if (source.Length > 1 && source[source.Length - 1] == 0) Array.Resize(ref source, source.Length - 1);
            if (source.Length > length) throw new CryptographicException("The generated RSA component is too large.");
            byte[] result = new byte[length];
            for (int i = 0; i < source.Length; i++) result[length - 1 - i] = source[i];
            return result;
        }
    }
}
