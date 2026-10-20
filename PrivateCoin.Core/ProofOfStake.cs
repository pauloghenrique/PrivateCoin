using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>An immutable phase in the validator reward schedule.</summary>
    public sealed class RewardPhase
    {
        internal RewardPhase(int number, int firstHeight, int lastHeight, long rewardPerBlock, long remainderAtomicUnits = 0)
        {
            Number = number;
            FirstHeight = firstHeight;
            LastHeight = lastHeight;
            RewardPerBlock = rewardPerBlock;
            RemainderAtomicUnits = remainderAtomicUnits;
        }

        public int Number { get; }
        public int FirstHeight { get; }
        public int LastHeight { get; }
        public long RewardPerBlock { get; }
        public int BlockCount => LastHeight - FirstHeight + 1;
        /// <summary>One extra atomic unit is paid on each of the last N blocks of this phase.</summary>
        public long RemainderAtomicUnits { get; }
        public long TotalReward => checked((long)BlockCount * RewardPerBlock + RemainderAtomicUnits);

        public long GetReward(int rewardIndex)
        {
            if (rewardIndex < FirstHeight || rewardIndex > LastHeight) return 0;
            return RewardPerBlock + (rewardIndex > LastHeight - RemainderAtomicUnits ? 1L : 0L);
        }
    }

    /// <summary>A validator's coins locked as collateral for consensus.</summary>
    public sealed class ValidatorStake
    {
        public ValidatorStake(string validatorId, string rewardAddress, long lockedAmount)
            : this(validatorId, rewardAddress, lockedAmount, new[] { rewardAddress })
        {
        }

        public ValidatorStake(string validatorId, string rewardAddress, long lockedAmount, IEnumerable<string> ownedAddresses)
            : this(validatorId, rewardAddress, lockedAmount, ownedAddresses, null, null)
        {
        }

        public ValidatorStake(string validatorId, string rewardAddress, long lockedAmount, IEnumerable<string> ownedAddresses,
            string publicKey, Func<string, string> signVote)
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
            PublicKey = publicKey;
            SignVote = signVote;
        }

        public string ValidatorId { get; }
        public string RewardAddress { get; }
        public long LockedAmount { get; }
        public IReadOnlyCollection<string> OwnedAddresses { get; }
        public string PublicKey { get; }
        internal Func<string, string> SignVote { get; }

        internal string CreateVote(string payload)
        {
            if (string.IsNullOrWhiteSpace(PublicKey) || SignVote == null)
                throw new InvalidOperationException("The validator cannot produce a consensus vote signature.");
            return SignVote(payload);
        }

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
    /// Reward indices are one-based and count only remunerated validator blocks, not chain heights.
    /// </summary>
    public static class ProofOfStake
    {
        // Five Julian years (365.25 days/year) at the ten-second target per remunerated block.
        public const int BlocksPerPhase = 15778800;
        public const int EmissionConsensusVersion = 35;
        public const int RewardedBlockCount = 4 * BlocksPerPhase;
        public const int CreatorPercentage = 30;
        public const int ConfirmerPercentage = 70;
        public const long MaximumSupply = 17820000L * Blockchain.OneCoin;

        private static RewardPhase CreatePhase(int number, long budgetCoins)
        {
            long budget = checked(budgetCoins * Blockchain.OneCoin);
            return new RewardPhase(number, (number - 1) * BlocksPerPhase + 1, number * BlocksPerPhase,
                budget / BlocksPerPhase, budget % BlocksPerPhase);
        }

        private static readonly RewardPhase[] rewardPhases =
        {
            CreatePhase(1, 7220000L),
            CreatePhase(2, 5600000L),
            CreatePhase(3, 3000000L),
            CreatePhase(4, 2000000L)
        };

        internal static bool VerifyVote(string publicKey, string payload, string signature)
        {
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(signature)) return false;
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKey);
                    return rsa.VerifyData(Encoding.UTF8.GetBytes(payload), CryptoConfig.MapNameToOID(Crypto.SignatureHashAlgorithm), Convert.FromBase64String(signature));
                }
            }
            catch (FormatException) { return false; }
            catch (CryptographicException) { return false; }
        }

        /// <summary>Returns a copy of the complete, auditable emission schedule.</summary>
        public static IReadOnlyList<RewardPhase> RewardPhases => rewardPhases.ToArray();

        /// <summary>Total amount issued by all rewarded blocks in atomic units.</summary>
        public static long ScheduledIssuance => rewardPhases.Aggregate(0L, (total, phase) => checked(total + phase.TotalReward));

        /// <summary>Subsidy for a remunerated block ordinal, never the chain height.</summary>
        public static long GetBlockReward(int rewardIndex, int consensusVersion = Blockchain.ConsensusVersion)
        {
            if (consensusVersion < EmissionConsensusVersion) return GetLegacyBlockReward(rewardIndex);
            RewardPhase phase = rewardPhases.FirstOrDefault(item => rewardIndex >= item.FirstHeight && rewardIndex <= item.LastHeight);
            return phase == null ? 0 : phase.GetReward(rewardIndex);
        }

        internal static long GetLegacyBlockReward(int height)
        {
            if (height <= 0 || height > 8000000) return 0;
            long[] rewards = { 361L * Blockchain.OneCoin / 100, 280L * Blockchain.OneCoin / 100,
                150L * Blockchain.OneCoin / 100, Blockchain.OneCoin };
            return rewards[(height - 1) / 2000000];
        }

        /// <summary>Exact scheduled issuance after N remunerated blocks, including atomic remainders.</summary>
        public static long GetScheduledIssuance(int rewardedBlockCount)
        {
            if (rewardedBlockCount < 0) throw new ArgumentOutOfRangeException(nameof(rewardedBlockCount));
            long total = 0;
            foreach (RewardPhase phase in rewardPhases)
            {
                int count = Math.Max(0, Math.Min(phase.BlockCount, rewardedBlockCount - phase.FirstHeight + 1));
                total = checked(total + (long)count * phase.RewardPerBlock +
                    Math.Max(0L, count - (phase.BlockCount - phase.RemainderAtomicUnits)));
            }
            return total;
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
            int rewardIndex,
            ValidatorStake creator,
            IEnumerable<ValidatorStake> confirmingValidators,
            int consensusVersion = Blockchain.ConsensusVersion)
        {
            return DistributeRewardAmount(GetBlockReward(rewardIndex, consensusVersion), creator, confirmingValidators);
        }

        internal static IReadOnlyList<ValidatorReward> DistributeRewardAmount(long reward,
            ValidatorStake creator, IEnumerable<ValidatorStake> confirmingValidators)
        {
            if (reward < 0) throw new ArgumentOutOfRangeException(nameof(reward));
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            if (confirmingValidators == null) throw new ArgumentNullException(nameof(confirmingValidators));
            var candidates = confirmingValidators.ToArray();
            ValidatorStake[] confirmers = candidates.Length == 0 ? candidates : Normalize(candidates);
            if (confirmers.Any(item => item.ValidatorId == creator.ValidatorId))
                throw new ArgumentException("The creator cannot also confirm the same block.", nameof(confirmingValidators));

            if (reward == 0) return new ValidatorReward[0];
            if (confirmers.Length == 0) return new[] { new ValidatorReward(creator.ValidatorId, creator.RewardAddress, reward, true) };
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
