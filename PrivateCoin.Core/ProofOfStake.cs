using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PrivateCoin.Core
{
    /// <summary>An immutable phase in the validator reward schedule.</summary>
    public sealed class RewardPhase
    {
        internal RewardPhase(int number, int firstHeight, int lastHeight, long rewardPerBlock)
        {
            Number = number;
            FirstHeight = firstHeight;
            LastHeight = lastHeight;
            RewardPerBlock = rewardPerBlock;
        }

        public int Number { get; }
        public int FirstHeight { get; }
        public int LastHeight { get; }
        public long RewardPerBlock { get; }
        public int BlockCount => LastHeight - FirstHeight + 1;
        public long TotalReward => checked((long)BlockCount * RewardPerBlock);
    }

    /// <summary>A validator's coins locked as collateral for consensus.</summary>
    public sealed class ValidatorStake
    {
        public ValidatorStake(string validatorId, string rewardAddress, long lockedAmount)
            : this(validatorId, rewardAddress, lockedAmount, new[] { rewardAddress })
        {
        }

        public ValidatorStake(string validatorId, string rewardAddress, long lockedAmount, IEnumerable<string> ownedAddresses)
        {
            if (string.IsNullOrWhiteSpace(validatorId)) throw new ArgumentException("A validator id is required.", nameof(validatorId));
            if (string.IsNullOrWhiteSpace(rewardAddress)) throw new ArgumentException("A reward address is required.", nameof(rewardAddress));
            if (lockedAmount <= 0) throw new ArgumentOutOfRangeException(nameof(lockedAmount));
            if (ownedAddresses == null) throw new ArgumentNullException(nameof(ownedAddresses));
            string[] addresses = ownedAddresses.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            if (addresses.Length == 0 || addresses.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("At least one valid owned address is required.", nameof(ownedAddresses));
            if (addresses.Distinct(StringComparer.Ordinal).Count() != addresses.Length)
                throw new ArgumentException("Owned addresses must be unique.", nameof(ownedAddresses));
            if (!addresses.Contains(rewardAddress, StringComparer.Ordinal))
                throw new ArgumentException("The reward address must belong to the validator.", nameof(ownedAddresses));
            ValidatorId = validatorId;
            RewardAddress = rewardAddress;
            LockedAmount = lockedAmount;
            OwnedAddresses = addresses;
        }

        public string ValidatorId { get; }
        public string RewardAddress { get; }
        public long LockedAmount { get; }
        public IReadOnlyCollection<string> OwnedAddresses { get; }
    }

    /// <summary>The portion of a block reward assigned by the protocol.</summary>
    public sealed class ValidatorReward
    {
        internal ValidatorReward(string validatorId, string rewardAddress, long amount, bool isCreator)
        {
            ValidatorId = validatorId;
            RewardAddress = rewardAddress;
            Amount = amount;
            IsCreator = isCreator;
        }

        public string ValidatorId { get; }
        public string RewardAddress { get; }
        public long Amount { get; }
        public bool IsCreator { get; }
    }

    /// <summary>
    /// Monetary policy and deterministic stake-weighted validator selection.
    /// Heights are one-based: height zero remains the genesis block.
    /// </summary>
    public static class ProofOfStake
    {
        public const int BlocksPerPhase = 2000000;
        public const int RewardedBlockCount = 4 * BlocksPerPhase;
        public const int CreatorPercentage = 30;
        public const int ConfirmerPercentage = 70;
        public const long MaximumSupply = 17820000L * Blockchain.OneCoin;

        private static readonly RewardPhase[] rewardPhases =
        {
            new RewardPhase(1, 1, BlocksPerPhase, 361L * Blockchain.OneCoin / 100L),
            new RewardPhase(2, BlocksPerPhase + 1, 2 * BlocksPerPhase, 280L * Blockchain.OneCoin / 100L),
            new RewardPhase(3, 2 * BlocksPerPhase + 1, 3 * BlocksPerPhase, 150L * Blockchain.OneCoin / 100L),
            new RewardPhase(4, 3 * BlocksPerPhase + 1, RewardedBlockCount, Blockchain.OneCoin)
        };

        /// <summary>Returns a copy of the complete, auditable emission schedule.</summary>
        public static IReadOnlyList<RewardPhase> RewardPhases => rewardPhases.ToArray();

        /// <summary>Total amount issued by all rewarded blocks in atomic units.</summary>
        public static long ScheduledIssuance => rewardPhases.Aggregate(0L, (total, phase) => checked(total + phase.TotalReward));

        public static long GetBlockReward(int height)
        {
            RewardPhase phase = rewardPhases.FirstOrDefault(item => height >= item.FirstHeight && height <= item.LastHeight);
            return phase == null ? 0 : phase.RewardPerBlock;
        }

        /// <summary>
        /// Selects the creator from the locked stake. The same validator may be selected
        /// at a later height as creator or confirmer; there are no permanent roles.
        /// </summary>
        public static ValidatorStake SelectCreator(IEnumerable<ValidatorStake> validators, string previousBlockHash, int height)
        {
            ValidatorStake[] ordered = Normalize(validators);
            if (string.IsNullOrWhiteSpace(previousBlockHash)) throw new ArgumentException("The previous block hash is required.", nameof(previousBlockHash));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            long totalStake = ordered.Aggregate(0L, (total, validator) => checked(total + validator.LockedAmount));
            string seed = Crypto.Sha256(previousBlockHash + "|" + height.ToString(CultureInfo.InvariantCulture));
            long ticket = HexModulo(seed, totalStake);
            long cursor = 0;
            foreach (ValidatorStake validator in ordered)
            {
                cursor = checked(cursor + validator.LockedAmount);
                if (ticket < cursor) return validator;
            }
            throw new InvalidOperationException("Could not select a block creator.");
        }

        /// <summary>
        /// Assigns 30% to the selected creator and 70% among confirming validators in
        /// proportion to their locked stake. Atomic-unit rounding is assigned in stable
        /// validator-id order, so every node obtains exactly the same result.
        /// </summary>
        public static IReadOnlyList<ValidatorReward> DistributeReward(
            int height,
            ValidatorStake creator,
            IEnumerable<ValidatorStake> confirmingValidators)
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            ValidatorStake[] confirmers = Normalize(confirmingValidators);
            if (confirmers.Any(item => item.ValidatorId == creator.ValidatorId))
                throw new ArgumentException("The creator cannot also confirm the same block.", nameof(confirmingValidators));

            long reward = GetBlockReward(height);
            if (reward == 0) return new ValidatorReward[0];
            long creatorAmount = reward * CreatorPercentage / 100L;
            long confirmerPool = reward - creatorAmount;
            long totalStake = confirmers.Aggregate(0L, (total, validator) => checked(total + validator.LockedAmount));
            var result = new List<ValidatorReward>
            {
                new ValidatorReward(creator.ValidatorId, creator.RewardAddress, creatorAmount, true)
            };
            long distributed = 0;
            foreach (ValidatorStake confirmer in confirmers)
            {
                long amount = (long)((decimal)confirmerPool * confirmer.LockedAmount / totalStake);
                distributed = checked(distributed + amount);
                result.Add(new ValidatorReward(confirmer.ValidatorId, confirmer.RewardAddress, amount, false));
            }
            long remainder = confirmerPool - distributed;
            for (int index = 1; remainder > 0; index++, remainder--)
            {
                int target = 1 + ((index - 1) % confirmers.Length);
                ValidatorReward old = result[target];
                result[target] = new ValidatorReward(old.ValidatorId, old.RewardAddress, old.Amount + 1, false);
            }
            return result;
        }

        private static ValidatorStake[] Normalize(IEnumerable<ValidatorStake> validators)
        {
            if (validators == null) throw new ArgumentNullException(nameof(validators));
            ValidatorStake[] ordered = validators.OrderBy(item => item == null ? null : item.ValidatorId, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0) throw new ArgumentException("At least one validator is required.", nameof(validators));
            if (ordered.Any(item => item == null)) throw new ArgumentException("A validator cannot be null.", nameof(validators));
            if (ordered.Select(item => item.ValidatorId).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
                throw new ArgumentException("Validator ids must be unique.", nameof(validators));
            return ordered;
        }

        private static long HexModulo(string hexadecimal, long divisor)
        {
            long remainder = 0;
            foreach (char character in hexadecimal)
            {
                int digit = character <= '9' ? character - '0' : character - 'a' + 10;
                remainder = (long)(((decimal)remainder * 16 + digit) % divisor);
            }
            return remainder;
        }
    }
}
