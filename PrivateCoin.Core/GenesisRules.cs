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

        private static readonly string implementationHash = CalculateImplementationHash();

        internal static GenesisRules CreateCanonical()
        {
            return new GenesisRules
            {
                SchemaVersion = 1,
                ConsensusVersion = Blockchain.ConsensusVersion,
                DecimalPlaces = Blockchain.DecimalPlaces,
                AtomicUnitsPerCoin = Blockchain.OneCoin,
                TotalSupplyAtomicUnits = checked(Blockchain.DistributionSupply + ProofOfStake.MaximumSupply),
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
                BlockValidationRule = "Every block requires proof of work, valid ordered operations, supply and UTXO conservation. Wallet and collateral operations are self-validated. Transfers, tokens and pools require signed collateral-backed approvals; stake blocks require the globally eligible validator set, deterministic creator selection and signed votes. Fork choice uses cumulative required work.",
                InitialDistributionRule = "No premine in genesis. One confirmed wallet reward per address, up to the rewarded-wallet limit; later wallets receive zero. No replay of historical distribution receipts.",
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
