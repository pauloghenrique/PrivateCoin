using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PrivateCoin.Core
{
    [DataContract]
    public sealed class GenesisRewardPhase
    {
        [DataMember(Order = 1)] public int FirstHeight { get; private set; }
        [DataMember(Order = 2)] public int LastHeight { get; private set; }
        [DataMember(Order = 3)] public long RewardAtomicUnits { get; private set; }
        internal GenesisRewardPhase(RewardPhase phase)
        {
            FirstHeight = phase.FirstHeight;
            LastHeight = phase.LastHeight;
            RewardAtomicUnits = phase.RewardPerBlock;
        }
    }

    /// <summary>
    /// Public protocol manifest committed by the genesis hash. Numeric values are
    /// taken from the executable rules; the source commitment covers every Core rule.
    /// This is compatibility detection, not remote attestation of an untrusted node.
    /// </summary>
    [DataContract]
    public sealed class GenesisRules
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; private set; }
        [DataMember(Order = 2)] public int ConsensusVersion { get; private set; }
        [DataMember(Order = 3)] public int DecimalPlaces { get; private set; }
        [DataMember(Order = 4)] public long AtomicUnitsPerCoin { get; private set; }
        [DataMember(Order = 5)] public long TotalSupplyAtomicUnits { get; private set; }
        [DataMember(Order = 6)] public long InitialDistributionAtomicUnits { get; private set; }
        [DataMember(Order = 7)] public long WalletRewardAtomicUnits { get; private set; }
        [DataMember(Order = 8)] public int RewardedWalletLimit { get; private set; }
        [DataMember(Order = 9)] public long GenesisIssuanceAtomicUnits { get; private set; }
        [DataMember(Order = 10)] public long ValidatorSupplyAtomicUnits { get; private set; }
        [DataMember(Order = 11)] public List<GenesisRewardPhase> RewardPhases { get; private set; }
        [DataMember(Order = 12)] public int CreatorPercentage { get; private set; }
        [DataMember(Order = 13)] public int ConfirmerPercentage { get; private set; }
        [DataMember(Order = 14)] public int MaxOperationsPerBlock { get; private set; }
        [DataMember(Order = 15)] public int BlockWaitSeconds { get; private set; }
        [DataMember(Order = 16)] public int ProofOfWorkDifficultyBits { get; private set; }
        [DataMember(Order = 17)] public long TransferFeeStepAtomicUnits { get; private set; }
        [DataMember(Order = 18)] public long MaximumTransferFeeAtomicUnits { get; private set; }
        [DataMember(Order = 19)] public int PoolFeeBasisPoints { get; private set; }
        [DataMember(Order = 20)] public string BlockProductionRule { get; private set; }
        [DataMember(Order = 21)] public string BlockValidationRule { get; private set; }
        [DataMember(Order = 22)] public string InitialDistributionRule { get; private set; }
        [DataMember(Order = 23)] public string ImplementationHash { get; private set; }

        [DataMember(Order = 24)] public string CoreConfigurationManifest { get; private set; }
        [DataMember(Order = 25)] public long InitialLaunchReserveAtomicUnits { get; private set; }
        [DataMember(Order = 26)] public long InitialLaunchPrincipalAtomicUnits { get; private set; }
        [DataMember(Order = 27)] public int InitialLaunchPurchaseLimit { get; private set; }
        [DataMember(Order = 28)] public string LocalUsdcTokenId { get; private set; }
        [DataMember(Order = 29)] public string LocalUsdtTokenId { get; private set; }
        [DataMember(Order = 30)] public long LocalFundingSupplyAtomicUnits { get; private set; }

        [DataMember(Order = 31)] public long WalletUsdcRewardAtomicUnits { get; private set; }
        [DataMember(Order = 32)] public int UsdcRewardedWalletLimit { get; private set; }

        private static readonly string implementationHash = CalculateImplementationHash();

        internal static GenesisRules CreateCanonical()
        {
            return new GenesisRules
            {
                SchemaVersion = 6,
                WalletUsdcRewardAtomicUnits = InitialLaunch.WalletUsdcReward,
                UsdcRewardedWalletLimit = InitialLaunch.UsdcRewardedWalletLimit,
                InitialLaunchReserveAtomicUnits = InitialLaunch.ReserveSupply,
                InitialLaunchPrincipalAtomicUnits = InitialLaunch.PrincipalSupply,
                InitialLaunchPurchaseLimit = InitialLaunch.MaximumPurchases,
                LocalUsdcTokenId = InitialLaunch.UsdcTokenId,
                LocalUsdtTokenId = InitialLaunch.UsdtTokenId,
                LocalFundingSupplyAtomicUnits = InitialLaunch.FundingTokenSupply,
                ConsensusVersion = Blockchain.ConsensusVersion,
                DecimalPlaces = Blockchain.DecimalPlaces,
                AtomicUnitsPerCoin = Blockchain.OneCoin,
                TotalSupplyAtomicUnits = checked(Blockchain.MaximumSupply + ProofOfStake.MaximumSupply),
                InitialDistributionAtomicUnits = Blockchain.DistributionSupply,
                WalletRewardAtomicUnits = Blockchain.WalletCreationReward,
                RewardedWalletLimit = Blockchain.RewardedWalletLimit,
                GenesisIssuanceAtomicUnits = 0,
                ValidatorSupplyAtomicUnits = ProofOfStake.MaximumSupply,
                RewardPhases = ProofOfStake.RewardPhases.Select(phase => new GenesisRewardPhase(phase)).ToList(),
                CreatorPercentage = ProofOfStake.CreatorPercentage,
                ConfirmerPercentage = ProofOfStake.ConfirmerPercentage,
                MaxOperationsPerBlock = Blockchain.MaxOperationsPerBlock,
                BlockWaitSeconds = Blockchain.BlockWaitSeconds,
                ProofOfWorkDifficultyBits = ProofOfWork.DifficultyBits,
                TransferFeeStepAtomicUnits = Blockchain.TransferFeeStep,
                MaximumTransferFeeAtomicUnits = Blockchain.MaximumTransferFee,
                PoolFeeBasisPoints = TokenExchange.PoolFeeBasisPoints,
                BlockProductionRule = "Produce a nonempty eligible batch at the operation limit or after the wait from first local observation. The local monotonic wait is not remotely verifiable consensus.",
                BlockValidationRule = "Every block requires proof of work, valid ordered operations, supply and UTXO conservation. Wallet and collateral operations are self-validated. The first native initial-launch purchase may bootstrap without approval, or receive a signed approval and rewarded block from an eligible nonparticipant. Later launch purchases require approval and block votes from confirmed unspent locked launch receipts or the original locked 600 POVIX; collateral is aggregated per identity; a nonempty subset with at least one nonparticipant holder may produce a launch-only block. Single-validator launch blocks pay the full scheduled block reward to the creator, plus the purchase fee through its approving validator. These rights expire at launch activation. While the native launch is collecting, a block containing only fresh wallet registrations may be signed by a nonempty subset of confirmed eligible collateral, including the initial locked 600 POVIX; one creator receives the full scheduled block reward. A fresh registration with any confirmed nonparticipant eligible collateral during collection must be included in a signed rewarded block; local absence of private keys cannot authorize an unsigned fallback. This applies even if other operations are mixed with the registration. Registration receipts, the initial pool bootstrap and mixed ordinary blocks do not gain the one-validator exception. User liquidity pool creation, token launch pool creation and all liquidity additions require the confirmed native initial launch target; the automatic initial pool is exempt. The first purchase fee has no validator payout. All signature, UTXO, identity and limit checks remain mandatory. Other transfers, tokens and pool operations require signed collateral-backed approvals; stake blocks require the globally eligible validator set, deterministic creator selection and signed votes. Fork choice uses cumulative required work.",
                InitialDistributionRule = "No premine in genesis. The first registered wallet receives 600 POVIX once, locked until the native pool target; later registrations receive zero. These initial 600 and purchased locked receipts are automatic collateral eligible for validation, block creation and scheduled rewards while locked, with ownership proved by the matching public key and signature. At the target their automatic collateral ends and the coins become spendable; rewards remain separate outputs. The first rewarded registration automatically issues 359400 POVIX into an ownerless local-USDC launch escrow; one gross purchase of 6 per registered wallet identity for one protocol-defined local USDC unit (six decimals), deducting the native transaction fee from those 6 POVIX before assigning the locked net receipt; no preexisting native balance is needed. The first 10000 registered wallet identities each receive 100 local USDC, including the first; total USDC issuance is capped at 1000000 units and advances only on registration. The first wallet receives 1000000 local USDT. Purchased outputs unlock at 29950 purchases, leaving 179700 POVIX and all funding in permanently locked principal liquidity. No bridge or external backing is implied.",
                ImplementationHash = implementationHash,
                CoreConfigurationManifest = CoreConfiguration.Manifest
            };
        }

        internal string CanonicalPayload()
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(GenesisRules)).WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public string CalculateHash() => Crypto.Sha256(CanonicalPayload());

        internal bool IsCanonical() => CanonicalPayload() == CreateCanonical().CanonicalPayload();

        private static string CalculateImplementationHash()
        {
            var assembly = typeof(GenesisRules).Assembly;
            string[] resources = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("PrivateCoin.Core.", StringComparison.Ordinal) && name.EndsWith(".cs", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (resources.Length == 0)
                throw new InvalidOperationException("Core protocol source commitments are missing. Build using PrivateCoin.Core.csproj.");
            var payload = new StringBuilder("povix-core-source-v1");
            foreach (string name in resources)
            {
                string source;
                using (var reader = new StreamReader(assembly.GetManifestResourceStream(name), Encoding.UTF8, true))
                    source = reader.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n");
                payload.Append('|').Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name)
                    .Append('|').Append(Crypto.Sha256(source));
            }
            return Crypto.Sha256(payload.ToString());
        }
    }
}
