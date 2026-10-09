using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>A public, random node identifier kept separately from chain snapshots.</summary>
    public static class NodeIdentity
    {
        public static string LoadOrCreate(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A node identity path is required.", nameof(path));
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporaryPath = path + "." + Crypto.NewId() + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, Crypto.NewId(), Encoding.ASCII);
                    // Publish a complete file without overwriting another instance's identity.
                    try { File.Move(temporaryPath, path); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            }
            string nodeId = File.ReadAllText(path).Trim();
            if (!IsValid(nodeId)) throw new InvalidOperationException("The stored node identity is invalid.");
            return nodeId;
        }

        internal static bool IsValid(string nodeId)
        {
            return nodeId != null && nodeId.Length == 64 &&
                nodeId.All(value => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'));
        }
    }
}
