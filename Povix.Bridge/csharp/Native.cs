using System;
using System.IO;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nethereum.Util;

namespace Povix.Bridge.CSharp {
    public static class Native {
        public const string ProtocolToken = "c834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d";
        public static string Representation(long chain, string asset) { return "b9b0" + BridgeNative.Hash("povix-bnb-token-v1|" + chain.ToString(CultureInfo.InvariantCulture) + "|" + asset.ToLowerInvariant()).Substring(4); }
        public static string Canonical(JToken value) {
            var obj = value as JObject;
            if (obj != null) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, JToken.Parse(Canonical(p.Value))))).ToString(Formatting.None);
            var arr = value as JArray;
            if (arr != null) return new JArray(arr.Select(v => JToken.Parse(Canonical(v)))).ToString(Formatting.None);
            return value.ToString(Formatting.None);
        }
        public static string Keccak(byte[] data) { return "0x" + new Sha3Keccack().CalculateHash(data).ToHexLower(); }
        public static string ToHexLower(this byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
        public static byte[] Bytes(string hex) { hex = hex.StartsWith("0x", StringComparison.Ordinal) ? hex.Substring(2) : hex; if (hex.Length % 2 != 0) throw new FormatException("Invalid hex."); return Enumerable.Range(0, hex.Length / 2).Select(i => byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber)).ToArray(); }
        // Runs the existing C# consensus adapter in-process; never starts Node.js or a shell.
        public static JToken Call(BridgeConfig config, string command, JToken request, string keys = null, string snapshot = null) {
            snapshot = snapshot ?? config["nativeSnapshot"];
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(snapshot) > TimeSpan.FromMinutes(5) || !File.Exists(snapshot)) throw new InvalidOperationException("Refresh the independent native snapshot.");
            string dir = Path.Combine(Path.GetTempPath(), "povix-csharp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var originalOut = Console.Out; var originalError = Console.Error;
            try {
                string cfg = Path.Combine(dir, "config.json"), req = Path.Combine(dir, "request.json");
                File.WriteAllText(cfg, config.Json.ToString(Formatting.None)); File.WriteAllText(req, (request ?? new JObject()).ToString(Formatting.None));
                using (var output = new StringWriter(CultureInfo.InvariantCulture)) using (var error = new StringWriter(CultureInfo.InvariantCulture)) {
                    Console.SetOut(output); Console.SetError(error);
                    int code = BridgeNative.Main(new[] { command, snapshot, cfg, req }.Concat(keys == null ? new string[0] : new[] { keys }).ToArray());
                    if (code != 0) throw new InvalidOperationException(error.ToString().Trim());
                    return JToken.Parse(output.ToString());
                }
            } finally { Console.SetOut(originalOut); Console.SetError(originalError); Directory.Delete(dir, true); }
        }
        public static JToken Review(BridgeConfig config, string command, JToken request) {
            var results = config.Strings("nativeSnapshots").Select(s => Call(config, command, request, snapshot: s)).ToArray();
            if (results.Any(r => Canonical(r) != Canonical(results[0]))) throw new InvalidOperationException("Independent POVIX nodes disagree. Stop signing.");
            return results[0];
        }
    }
}
