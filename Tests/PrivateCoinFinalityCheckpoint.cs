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
            if (args.Length != 1) throw new ArgumentException("Usage: PrivateCoinFinalityCheckpoint.exe Blockchain.json");
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
            var chain = new Blockchain(blocks, null);
            ValidatorStake[] committee = chain.GetActiveValidators().ToArray();
            if (committee.Length < 2 || committee.Select(v => v.PublicKey).Distinct().Count() != committee.Length)
                throw new InvalidOperationException("Checkpoint requires at least two distinct registered validator keys.");
            var policy = new FinalityPolicy(chain.Blocks.Count - 1, chain.Blocks.Last().Hash);
            policy.FinalizedHeight(chain.Blocks); // Also rejects aliases of the same RSA key.
            Console.WriteLine("Suggested checkpoint from this file; validators must agree on the SAME checkpoint before activation:");
            Console.WriteLine("<add key=\"FinalityAnchorHeight\" value=\"" + policy.AnchorHeight + "\" />");
            Console.WriteLine("<add key=\"FinalityAnchorHash\" value=\"" + policy.AnchorHash + "\" />");
            Console.WriteLine("<add key=\"RequireFinality\" value=\"true\" />");
            Console.WriteLine("Registered validator keys: " + committee.Length);
            Console.WriteLine("Policy ID: " + policy.Id);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static T Read<T>(byte[] bytes)
    { using (var stream = new MemoryStream(bytes)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
}
