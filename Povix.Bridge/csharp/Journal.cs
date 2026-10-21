using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Povix.Bridge.CSharp {
    // Separate format from the legacy SQLite journal: no silent conversion or deletion.
    // One exclusive process lock, hash-chained records and fsync before any signature is returned.
    public sealed class Journal : IDisposable {
        [DllImport("libc", SetLastError = true)] static extern int flock(int fd, int operation);
        readonly FileStream stream;
        readonly Dictionary<string, JToken> entries = new Dictionary<string, JToken>(StringComparer.Ordinal);
        string head = new string('0', 64);
        public Journal(string filename, BridgeConfig config) {
            if (!filename.EndsWith(".journal", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use a new .journal file. Legacy SQLite journals require explicit reconciliation, not automatic migration.");
            stream = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
            try {
                // flock is tied to this open description. POSIX fcntl locks can be
                // released when another failed FileShare probe closes the same inode.
                if (Environment.OSVersion.Platform == PlatformID.Win32NT) stream.Lock(0, 1);
                else if (flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 6) != 0) throw new IOException("Another process holds this journal. Stop the second signer.");
                if (stream.Length > 64 * 1024 * 1024) throw new InvalidOperationException("Archive the journal using an independently reviewed procedure.");
                byte[] bytes = new byte[(int)stream.Length]; int count = 0, n;
                while ((n = stream.Read(bytes, count, bytes.Length - count)) > 0) count += n;
                string text = new UTF8Encoding(false, true).GetString(bytes);
                if (text.Length != 0 && !text.EndsWith("\n", StringComparison.Ordinal)) throw new InvalidOperationException("Incomplete journal record. Stop and reconcile; do not truncate automatically.");
                foreach (string line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                    var record = JObject.Parse(line); string previous = (string)record["previous"], hash = (string)record["hash"]; record.Remove("hash");
                    if (previous != head || BridgeNative.Hash(Native.Canonical(record)) != hash) throw new InvalidOperationException("Journal integrity failure.");
                    entries[(string)record["key"]] = record["value"]; head = hash;
                }
                stream.Position = stream.Length;
                var scope = new JArray(config.ChainId, config["bridge"], config["nativeNetwork"], config["nativeToken"]);
                Record("scope", scope);
            } catch { stream.Dispose(); throw; }
        }
        public string[] Keys { get { return new System.Collections.Generic.List<string>(entries.Keys).ToArray(); } }
        public JToken Get(string key) { JToken value; return entries.TryGetValue(key, out value) ? value.DeepClone() : null; }
        public void Record(string key, JToken value) {
            var old = Get(key); if (old != null) { if (Native.Canonical(old) != Native.Canonical(value)) throw new InvalidOperationException("Conflicting journal decision or bridge scope."); return; }
            Replace(key, value);
        }
        public void Replace(string key, JToken value) {
            var record = new JObject { ["previous"] = head, ["key"] = key, ["value"] = value.DeepClone() };
            string hash = BridgeNative.Hash(Native.Canonical(record)); record["hash"] = hash;
            byte[] bytes = Encoding.UTF8.GetBytes(record.ToString(Formatting.None) + "\n"); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            head = hash; entries[key] = value.DeepClone();
        }
        public void Dispose() { stream.Dispose(); }
    }
}
