using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Nethereum.Util;

namespace Povix.Bridge.CSharp {
    public sealed class BridgeConfig {
        public JObject Json { get; private set; }
        public string this[string name] { get { return (string)Json[name]; } }
        public long ChainId { get { return (long)Json["chainId"]; } }
        public string[] Strings(string key) {
            var values = Json[key] as JArray;
            if (values == null || values.Any(v => v.Type != JTokenType.String)) throw new InvalidOperationException("Expected a string array for " + key + ".");
            return values.Values<string>().ToArray();
        }
        public int Number(string key) { return (int)Json[key]; }
        public BigInteger Amount(string key) { return BigInteger.Parse(this[key], System.Globalization.CultureInfo.InvariantCulture); }
        public static bool Hex(string value, int size, bool prefix = false) { return value != null && Regex.IsMatch(value, "^" + (prefix ? "0x" : "") + "[a-f0-9]{" + size + "}$") && value.TrimStart('0', 'x').Length != 0; }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public static BridgeConfig Load(string file) { return From(JObject.Parse(File.ReadAllText(file))); }
        public BridgeConfig With(string key, JToken value) { var copy = (JObject)Json.DeepClone(); copy[key] = value; return From(copy); }
        public static BridgeConfig From(JObject json) {
            var c = new BridgeConfig { Json = json };
            foreach (var field in new[] { "chainId", "threshold", "nativeThreshold", "nativeConfirmations", "bscConfirmations" })
                Require(json[field] != null && json[field].Type == JTokenType.Integer, "Expected an integer for " + field + ".");
            Require(json["nativeDecimals"] == null || json["nativeDecimals"].Type == JTokenType.Integer, "Native decimals must be an integer.");
            Require(json["reserveCap"] != null && json["reserveCap"].Type == JTokenType.String && json["dailyReleaseCap"] != null && json["dailyReleaseCap"].Type == JTokenType.String, "Express atomic limits as decimal strings.");
            Require(c["mode"] == "production", "The C# client requires explicit production mode (local/testnet or mainnet v2 contracts).");
            Require(new long[] { 56, 97, 31337 }.Contains(c.ChainId), "Unsupported chain.");
            Require(c["nativeNetwork"] != null && c["nativeNetwork"].StartsWith("povix-mainnet-rules-v1-", StringComparison.Ordinal), "Pin nativeNetwork.");
            foreach (var key in new[] { "operators", "nativeAdministrators" }) {
                var members = c.Strings(key); var threshold = c.Number(key == "operators" ? "threshold" : "nativeThreshold");
                Require((members.Length == 2 || members.Length >= 5) && members.Length <= (key == "operators" ? 10 : 16) && threshold > members.Length / 2 && threshold <= members.Length && members.Distinct(StringComparer.OrdinalIgnoreCase).Count() == members.Length, "Require two distinct unanimous operators or five-plus majority operators.");
                Require(members.All(a => Hex(key == "operators" ? a.ToLowerInvariant() : a, key == "operators" ? 40 : 64, key == "operators")), "Invalid administrator address.");
                if (key == "operators") Require(members.All(a => new AddressUtil().IsValidEthereumAddressHexFormat(a) && (a == a.ToLowerInvariant() || new AddressUtil().IsChecksumAddress(a))), "Invalid EVM address checksum.");
            }
            Require(Hex(c["asset"], 40, true) && (json["bridge"].Type == JTokenType.Null || Hex(c["bridge"], 40, true)), "Use lowercase asset/vault addresses.");
            Require(Hex(c["assetCodeHash"], 64, true) && (c["bridge"] == null || Hex(c["vaultCodeHash"], 64, true)), "Pin contract runtime hashes.");
            Require(c["factory"] == null || Hex(c["factory"], 40, true) && Hex(c["factoryCodeHash"], 64, true), "Pin factory and runtime hash.");
            Require(Hex(c["nativeToken"], 64) && (c["nativeToken"] == Native.ProtocolToken || c["nativeToken"] == Native.Representation(c.ChainId, c["asset"]) && c.Number("nativeDecimals") >= 0 && c.Number("nativeDecimals") <= 8 && Regex.IsMatch(c["nativeSymbol"] ?? "", "^p[A-Z0-9]{1,9}$")), "Invalid native representation.");
            Require(Regex.IsMatch(c["reserveCap"] ?? "", "^[1-9][0-9]*$") && Regex.IsMatch(c["dailyReleaseCap"] ?? "", "^[1-9][0-9]*$") && c.Amount("dailyReleaseCap") <= c.Amount("reserveCap") && c.Amount("reserveCap") <= long.MaxValue, "Invalid atomic limits.");
            Require(c.Number("nativeConfirmations") >= (c.ChainId == 31337 ? 12 : 120) && c.Number("nativeConfirmations") <= 100000 && c.Number("bscConfirmations") >= (c.ChainId == 31337 ? 15 : 64), "Insufficient confirmations.");
            var urls = c.Strings("rpcUrls").Select(s => new Uri(s)).ToArray();
            Require(urls.Length >= 2 && urls.Select(u => u.AbsoluteUri).Distinct().Count() == urls.Length && c.Strings("rpcUrls").Contains(c["rpcUrl"]) && urls.All(u => u.Scheme == "https" || c.ChainId == 31337 && u.Scheme == "http" && u.IsLoopback) && (c.ChainId != 56 || urls.Select(u => u.Host).Distinct().Count() >= 2), "Use independent HTTPS RPC providers.");
            var snapshots = c.Strings("nativeSnapshots").Select(Path.GetFullPath).ToArray();
            Require(snapshots.Length >= 2 && snapshots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == snapshots.Length && snapshots.Contains(Path.GetFullPath(c["nativeSnapshot"])), "Use snapshots from two independently operated nodes.");
            var publicKeys = c.Strings("nativeSigningPublicKeys");
            Require(publicKeys.Length >= c.Number("nativeThreshold") && publicKeys.Length <= c.Strings("nativeAdministrators").Length && publicKeys.Distinct().Count() == publicKeys.Length && publicKeys.All(k => c.Strings("nativeAdministrators").Contains(BridgeNative.Hash(k))), "Pin the public native signing quorum.");
            return c;
        }
    }
}
