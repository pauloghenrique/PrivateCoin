using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>Canonical public protocol configuration committed by the genesis block.</summary>
    public static class CoreConfiguration
    {
        public const int ManifestVersion = 2;

        // Cache only immutable text. Never expose a shared mutable genesis configuration.
        public static string Manifest { get; } = BuildManifest();
        public static string Hash { get; } = Crypto.Sha256(Manifest);

        private static string BuildManifest()
        {
            var entries = new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (Type type in typeof(Blockchain).Assembly.GetTypes().Where(t => t.Namespace == "PrivateCoin.Core"))
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.DeclaredOnly).Where(f => f.IsLiteral && !f.IsInitOnly))
            {
                // These are outputs of the genesis commitment, not inputs to it.
                if (type == typeof(Blockchain) && (field.Name == "GenesisHash" ||
                    field.Name == "NetworkId" || field.Name == "GenesisNonce")) continue;
                object value = field.GetRawConstantValue();
                entries.Add(type.Name + "." + field.Name, Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            foreach (RewardPhase phase in ProofOfStake.RewardPhases)
            {
                string prefix = "RewardPhase." + phase.Number.ToString(CultureInfo.InvariantCulture) + ".";
                entries.Add(prefix + "FirstHeight", phase.FirstHeight.ToString(CultureInfo.InvariantCulture));
                entries.Add(prefix + "LastHeight", phase.LastHeight.ToString(CultureInfo.InvariantCulture));
                entries.Add(prefix + "RewardPerBlock", phase.RewardPerBlock.ToString(CultureInfo.InvariantCulture));
                entries.Add(prefix + "RemainderAtomicUnits", phase.RemainderAtomicUnits.ToString(CultureInfo.InvariantCulture));
                entries.Add(prefix + "TotalReward", phase.TotalReward.ToString(CultureInfo.InvariantCulture));
            }
            var result = new StringBuilder("privatecoin-core-configuration-v2|");
            foreach (var entry in entries)
                result.Append(entry.Key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(entry.Key)
                    .Append(entry.Value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(entry.Value);
            return result.ToString();
        }
    }
}
