using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>Read-only migration of public chain data; never starts a peer or reads wallet keys.</summary>
    public static class PublicBlockchainSnapshot
    {
        [DataContract] private sealed class Envelope
        {
            [DataMember] public string Data { get; set; }
            [DataMember] public string Sha256 { get; set; }
            [DataMember] public List<Block> Blocks { get; set; }
        }
        [DataContract] private sealed class Ledger
        { [DataMember] public List<Block> Blocks { get; set; } }

        public static Blockchain Read(string path, FinalityPolicy policy = null)
        {
            byte[] input = File.ReadAllBytes(path);
            List<Block> blocks;
            if (Encoding.UTF8.GetString(input).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("[", StringComparison.Ordinal))
                blocks = Deserialize<List<Block>>(input);
            else
            {
                Envelope envelope = Deserialize<Envelope>(input);
                if (envelope == null) throw new InvalidDataException("Arquivo público da rede vazio.");
                blocks = envelope.Blocks;
                if (envelope.Data != null)
                {
                    byte[] data = Convert.FromBase64String(envelope.Data);
                    using (var sha = SHA256.Create())
                        if (!string.Equals(string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2"))), envelope.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("A verificação SHA-256 do arquivo público da rede falhou.");
                    blocks = Deserialize<Ledger>(data)?.Blocks;
                }
            }
            if (blocks == null) throw new InvalidDataException("O arquivo público não contém blocos.");
            return new Blockchain(blocks, policy);
        }

        private static T Deserialize<T>(byte[] data)
        { using (var stream = new MemoryStream(data)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
    }
}
