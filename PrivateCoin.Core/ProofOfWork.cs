using System;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace PrivateCoin.Core
{
    /// <summary>Work is derived from the required target, never from lucky extra zeroes.</summary>
    public static class ProofOfWork
    {
        public const int DifficultyBits = 12;
        public const int SuggestedConfirmations = 6;
        public static BigInteger Target => (BigInteger.One << (256 - DifficultyBits)) - 1;
        public static BigInteger WorkPerBlock => (BigInteger.One << 256) / (Target + 1);
        public static long GetBlockReward(int height) => ProofOfStake.GetBlockReward(height);

        public static bool MeetsTarget(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char value in hash)
                if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) return false;
            return BigInteger.Parse("0" + hash, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) <= Target;
        }

        public static BigInteger GetBlockWork(Block block)
        {
            if (block == null || block.Hash != block.CalculateHash() || !MeetsTarget(block.Hash))
                throw new InvalidOperationException("Invalid proof of work.");
            return WorkPerBlock;
        }

        internal static void Mine(Block block, CancellationToken cancellation)
        {
            do
            {
                cancellation.ThrowIfCancellationRequested();
                block.Nonce = checked(block.Nonce + 1);
                block.Hash = block.CalculateHash();
            } while (!MeetsTarget(block.Hash));
        }
    }
}
