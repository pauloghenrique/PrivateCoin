using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace PrivateCoin.Core
{
    /// <summary>Required targets, retargeting and cumulative work are verified by every node.</summary>
    public static class ProofOfWork
    {
        public const int DynamicConsensusVersion = 34;
        public const int DifficultyBits = 12; // Legacy/genesis difficulty and dynamic minimum.
        public const int InitialDifficultyBits = 14;
        public const int TargetBlockSeconds = 10;
        public const int RetargetWindow = 30;
        public const int MaximumAdjustmentFactor = 4;
        public const int MaximumFutureSeconds = 30;
        public const int SuggestedConfirmations = 6;
        public static BigInteger Target => (BigInteger.One << (256 - DifficultyBits)) - 1;
        public static BigInteger InitialTarget => (BigInteger.One << (256 - InitialDifficultyBits)) - 1;
        public static BigInteger WorkPerBlock => (BigInteger.One << 256) / (Target + 1);
        public static long GetBlockReward(int height) => ProofOfStake.GetBlockReward(height);

        public static string FormatTarget(BigInteger target)
        {
            if (target < 1 || target > Target) throw new ArgumentOutOfRangeException(nameof(target));
            return target.ToString("x64", CultureInfo.InvariantCulture);
        }

        private static bool IsHex(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char digit in value)
                if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f'))) return false;
            return true;
        }

        public static BigInteger GetBlockTarget(Block block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            if (block.ConsensusVersion < DynamicConsensusVersion)
            {
                if (block.ProofOfWorkTarget != null) throw new InvalidOperationException("Unexpected target in a legacy block.");
                return Target;
            }
            if (!IsHex(block.ProofOfWorkTarget)) throw new InvalidOperationException("Missing or malformed proof-of-work target.");
            BigInteger target = BigInteger.Parse("0" + block.ProofOfWorkTarget, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            if (target < 1 || target > Target) throw new InvalidOperationException("Proof-of-work target is outside the protocol bounds.");
            return target;
        }

        public static BigInteger GetRequiredTarget(IReadOnlyList<Block> history, int historyCount = -1)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            int count = historyCount < 0 ? history.Count : historyCount;
            if (count < 1 || count > history.Count) throw new ArgumentOutOfRangeException(nameof(historyCount));
            if (count <= RetargetWindow || history[count - 1].ConsensusVersion < DynamicConsensusVersion) return InitialTarget;
            BigInteger previous = GetBlockTarget(history[count - 1]);
            if ((count - 1) % RetargetWindow != 0) return previous;
            long expected = (RetargetWindow - 1L) * TargetBlockSeconds * TimeSpan.TicksPerSecond;
            long observed = checked(history[count - 1].TimestampUtcTicks - history[count - RetargetWindow].TimestampUtcTicks);
            observed = Math.Max(expected / MaximumAdjustmentFactor, Math.Min(expected * MaximumAdjustmentFactor, observed));
            BigInteger next = (previous + 1) * observed / expected - 1;
            return BigInteger.Max(BigInteger.One, BigInteger.Min(Target, next));
        }

        public static long GetNextTimestamp(IReadOnlyList<Block> history, DateTime utcNow)
        {
            if (history == null || history.Count == 0) throw new ArgumentException("Confirmed history is required.", nameof(history));
            long next = Math.Max(utcNow.Ticks, checked(history[history.Count - 1].TimestampUtcTicks + 1));
            if (next > LatestTimestamp(utcNow)) throw new InvalidOperationException("Local clock is behind the permitted block time.");
            return next;
        }

        private static long LatestTimestamp(DateTime utcNow)
        {
            long drift = MaximumFutureSeconds * TimeSpan.TicksPerSecond;
            return Math.Min(DateTime.MaxValue.Ticks - drift, utcNow.Ticks) + drift;
        }

        public static void ValidateHeader(Block block, IReadOnlyList<Block> history, DateTime utcNow, int historyCount = -1)
        {
            if (block == null || history == null) throw new ArgumentNullException();
            int count = historyCount < 0 ? history.Count : historyCount;
            if (count < 1 || count > history.Count || block.Height != count || block.PreviousHash != history[count - 1].Hash)
                throw new InvalidOperationException("Invalid proof-of-work history.");
            BigInteger declared = GetBlockTarget(block);
            if (block.ConsensusVersion >= DynamicConsensusVersion)
            {
                if (declared != GetRequiredTarget(history, count)) throw new InvalidOperationException("The block does not use the required proof-of-work target.");
                if (block.TimestampUtcTicks <= history[count - 1].TimestampUtcTicks || block.TimestampUtcTicks > LatestTimestamp(utcNow))
                    throw new InvalidOperationException("Block time must advance and cannot exceed the permitted future drift.");
            }
            if (block.Hash != block.CalculateHash() || !MeetsTarget(block.Hash, declared)) throw new InvalidOperationException("Invalid proof of work.");
        }

        public static bool MeetsTarget(string hash) => MeetsTarget(hash, Target);
        public static bool MeetsTarget(string hash, BigInteger target)
        {
            return target >= 1 && target <= Target && IsHex(hash) &&
                BigInteger.Parse("0" + hash, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) <= target;
        }

        public static BigInteger GetBlockWork(Block block)
        {
            BigInteger target = GetBlockTarget(block);
            if (block.Hash != block.CalculateHash() || !MeetsTarget(block.Hash, target)) throw new InvalidOperationException("Invalid proof of work.");
            return (BigInteger.One << 256) / (target + 1);
        }

        internal static void Mine(Block block, CancellationToken cancellation)
        {
            BigInteger target = GetBlockTarget(block);
            do
            {
                cancellation.ThrowIfCancellationRequested();
                block.Nonce = checked(block.Nonce + 1);
                block.Hash = block.CalculateHash();
            } while (!MeetsTarget(block.Hash, target));
        }
    }
}
