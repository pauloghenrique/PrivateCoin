using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using PrivateCoin.Core;

// Read-only migration helper. Reads PUBLIC network data, never wallets.dat.
internal static class PrivateCoinFinalityCheckpoint
{
    [DataContract] private sealed class Envelope
    {
        [DataMember] public string Data { get; set; }
        [DataMember] public string Sha256 { get; set; }
        [DataMember] public List<Block> Blocks { get; set; }
    }
    [DataContract] private sealed class Ledger
    { [DataMember] public List<Block> Blocks { get; set; } }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 && args.Length != 3 && args.Length != 5)
                throw new ArgumentException("Usage: PrivateCoinFinalityCheckpoint.exe Blockchain.json [--export candidate.json [--height N] | --verify candidate.json]");
            bool exporting = args.Length >= 3 && args[1] == "--export";
            bool verifying = args.Length == 3 && args[1] == "--verify";
            if (args.Length >= 3 && !exporting && !verifying)
                throw new ArgumentException("Use --export or --verify.");
            int? height = null;
            if (args.Length == 5)
            {
                int selected;
                if (args[3] != "--height" || !int.TryParse(args[4], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out selected))
                    throw new ArgumentException("Use --height with an existing block height.");
                height = selected;
            }
            FinalityCheckpoint received = null;
            if (verifying)
            {
                if (new FileInfo(args[2]).Length > 1024 * 1024) throw new InvalidDataException("Checkpoint exceeds 1 MiB.");
                received = FinalityCheckpoint.FromJson(File.ReadAllBytes(args[2]));
            }
            byte[] input = File.ReadAllBytes(args[0]);
            Envelope envelope = System.Text.Encoding.UTF8.GetString(input).TrimStart().StartsWith("[", StringComparison.Ordinal)
                ? new Envelope { Blocks = Read<List<Block>>(input) } : Read<Envelope>(input);
            List<Block> blocks = envelope.Blocks;
            if (envelope.Data != null)
            {
                byte[] data = Convert.FromBase64String(envelope.Data);
                using (var sha = SHA256.Create())
                    if (string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2"))) != envelope.Sha256?.ToLowerInvariant())
                        throw new InvalidDataException("Public network file checksum mismatch.");
                blocks = Read<Ledger>(data).Blocks;
            }
            FinalityPolicy configured = FinalityPolicy.FromConfiguration();
            if (received != null && configured != null && configured.Id != received.PolicyId)
                throw new InvalidOperationException("The candidate differs from the configured activation policy.");
            var chain = new Blockchain(blocks, configured ?? received?.ToPolicy());
            FinalityCheckpoint checkpoint = received ?? FinalityCheckpoint.Create(chain, height);
            checkpoint.ValidateAgainst(chain);
            if (exporting)
            {
                string target = Path.GetFullPath(args[2]);
                if (string.Equals(Path.GetFullPath(args[0]), target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The candidate cannot overwrite the source blockchain file.");
                byte[] bytes = checkpoint.ToJson();
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    File.Move(temporary, target); // Never overwrite existing data.
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                Console.WriteLine("Public candidate saved: " + target);
            }
            Console.WriteLine(verifying ? "Candidate matches the local block and registered stakes; this is not validator approval."
                : "Candidate from this file; operators must agree on the SAME reference before activation:");
            Console.WriteLine(checkpoint.ConfigurationSnippet());
            Console.WriteLine("Registered validator keys: " + checkpoint.Committee.Count);
            Console.WriteLine("Policy ID: " + checkpoint.PolicyId);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static T Read<T>(byte[] bytes)
    { using (var stream = new MemoryStream(bytes)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
}
