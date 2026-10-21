using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Povix.Bridge.CSharp {
    // The exchange is untrusted PUBLIC data, never a keystore transport.
    public static class AutomationFiles {
        public static void NoLinks(string path) {
            string full = Path.GetFullPath(path);
            FileSystemInfo item = Directory.Exists(full) ? (FileSystemInfo)new DirectoryInfo(full) : new FileInfo(full);
            while (item != null) {
                if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Automation paths cannot contain symbolic links/reparse points.");
                var dir = item as DirectoryInfo; item = dir == null ? ((FileInfo)item).Directory : dir.Parent;
            }
        }
        public static JObject Read(string path) {
            NoLinks(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.Length > 262144) throw new IOException("Automation JSON exceeds 256 KiB.");
                using (var buffer = new MemoryStream()) {
                    byte[] chunk = new byte[8192]; int count;
                    while ((count = stream.Read(chunk, 0, chunk.Length)) > 0) { if (buffer.Length + count > 262144) throw new IOException("Automation JSON exceeds 256 KiB."); buffer.Write(chunk, 0, count); }
                    using (var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(buffer.ToArray()))) { MaxDepth = 32, DateParseHandling = DateParseHandling.None }) {
                    var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new IOException("Trailing JSON data."); return value;
                    }
                }
            }
        }
        public static void Put(string path, JObject value) {
            NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) { if (Native.Canonical(Read(path)) != Native.Canonical(value)) throw new IOException("Conflicting immutable exchange file."); return; }
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                    byte[] bytes = Encoding.UTF8.GetBytes(Native.Canonical(value)); if (bytes.Length > 262144) throw new IOException("Automation JSON exceeds 256 KiB.");
                    s.Write(bytes, 0, bytes.Length); s.Flush(true);
                }
                try { File.Move(temp, path); } catch (IOException) { if (!File.Exists(path) || Native.Canonical(Read(path)) != Native.Canonical(value)) throw; }
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static string[] List(string directory) {
            NoLinks(directory); if (!Directory.Exists(directory)) return new string[0];
            var files = Directory.EnumerateFiles(directory, "*.json").Take(1001).ToArray();
            if (files.Length > 1000) throw new IOException("Exchange directory exceeds 1000 files; archive completed jobs using the documented procedure.");
            return files.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }
        public static string Scope(BridgeConfig c) { return BridgeNative.Hash(Native.Canonical(new JArray(c.ChainId, c["bridge"], c["nativeNetwork"], c["nativeToken"]))); }
        public static JObject Job(BridgeConfig c, string kind, JObject body) { return new JObject { ["version"] = 1, ["scope"] = Scope(c), ["kind"] = kind, ["body"] = body.DeepClone() }; }
        public static string Id(JObject job) { return BridgeNative.Hash(Native.Canonical(job)); }
        public static void ValidateJob(BridgeConfig c, string path, JObject job) {
            if (job["version"] == null || job["version"].Type != JTokenType.Integer || (int?)job["version"] != 1 || (string)job["scope"] != Scope(c) || job["body"] == null || job["body"].Type != JTokenType.Object || Path.GetFileNameWithoutExtension(path) != Id(job))
                throw new InvalidOperationException("Wrong job identity, version or bridge scope.");
        }
    }
    public sealed class AutomationConfig {
        public string Exchange, State, NativeKey, EvmKey, RelayKey;
        public string[] Peers;
        public int PollSeconds;
        public long StartBlock, MaxGasPriceWei;
        public int MaxGasLimit;
        public bool Releases;
        public static AutomationConfig Load(string file, bool coordinator) {
            var j = AutomationFiles.Read(file);
            if (j["enabled"] == null || j["enabled"].Type != JTokenType.Boolean || (bool?)j["enabled"] != true) throw new InvalidOperationException("Explicitly enable automation after configuring the bridge and local keys.");
            foreach (string name in new[] { "exchangeDirectory", "stateDirectory" })
                if (j[name] == null || j[name].Type != JTokenType.String || !Path.IsPathRooted((string)j[name])) throw new InvalidOperationException("Use absolute automation paths: " + name);
            if (j["processReleases"] != null && j["processReleases"].Type != JTokenType.Boolean) throw new InvalidOperationException("processReleases must be a boolean.");
            var a = new AutomationConfig { Exchange = Path.GetFullPath((string)j["exchangeDirectory"]), State = Path.GetFullPath((string)j["stateDirectory"]),
                NativeKey = (string)j["nativeOperatorKeystore"], EvmKey = (string)j["evmOperatorKeystore"], RelayKey = (string)j["relayKeystore"], Releases = (bool?)j["processReleases"] == true,
                PollSeconds = Number(j, "pollSeconds", 5, 300),
                StartBlock = coordinator ? Number(j, "startBlock", 0, int.MaxValue) : 0,
                MaxGasPriceWei = GasPrice(j), MaxGasLimit = j["maxGasLimit"] == null ? 1000000 : Number(j, "maxGasLimit", 21000, 5000000),
                Peers = coordinator ? ((JArray)j["nativePeers"]).Values<string>().ToArray() : new string[0] };
            if (coordinator && (a.NativeKey != null || a.EvmKey != null) || !coordinator && (a.NativeKey == null || a.RelayKey != null || a.Releases && a.EvmKey == null)) throw new InvalidOperationException("Separate coordinator relay and operator keystores.");
            if (coordinator && (a.Peers.Length == 0 || a.Releases && a.RelayKey == null)) throw new InvalidOperationException("Configure native peers and a separate relay keystore for releases.");
            foreach (var p in a.Peers) { var u = new Uri("tcp://" + p); if (u.Port < 1 || u.Port > 65535 || u.AbsolutePath != "/" || u.UserInfo != "" || u.Query != "") throw new InvalidOperationException("Invalid native peer endpoint."); }
            foreach (string path in new[] { a.State, a.NativeKey, a.EvmKey, a.RelayKey }.Where(p => p != null)) {
                if (!Path.IsPathRooted(path)) throw new InvalidOperationException("Use absolute state and keystore paths.");
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                string exchange = a.Exchange.TrimEnd(Path.DirectorySeparatorChar);
                if (full.Equals(exchange, StringComparison.OrdinalIgnoreCase) || full.StartsWith(exchange + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Keep local state and private keystores outside the public exchange.");
                AutomationFiles.NoLinks(full);
            }
            AutomationFiles.NoLinks(a.Exchange); Directory.CreateDirectory(a.Exchange); Directory.CreateDirectory(a.State); return a;
        }
        static long GasPrice(JObject j) {
            if (j["maxGasPriceWei"] == null) return 5000000000L;
            long value;
            if (j["maxGasPriceWei"].Type != JTokenType.String || !long.TryParse((string)j["maxGasPriceWei"], out value) || value < 1 || value > 1000000000000L) throw new InvalidOperationException("Use a positive decimal-string maxGasPriceWei, at most 1000 gwei.");
            return value;
        }
        static int Number(JObject j, string name, int min, int max) {
            long n; if (j[name] == null || j[name].Type != JTokenType.Integer || !long.TryParse(j[name].ToString(), out n) || n < min || n > max) throw new InvalidOperationException("Invalid automation " + name + "."); return (int)n;
        }
    }
}
