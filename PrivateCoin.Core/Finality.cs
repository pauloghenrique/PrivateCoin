using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace PrivateCoin.Core
{
    [DataContract]
    public sealed class FinalityVote
    {
        [DataMember(Order = 1)] public int Height { get; set; }
        [DataMember(Order = 2)] public string BlockHash { get; set; }
        [DataMember(Order = 3)] public string PublicKey { get; set; }
        [DataMember(Order = 4)] public string Signature { get; set; }
    }

    public sealed class FinalityPolicy
    {
        public int AnchorHeight { get; }
        public string AnchorHash { get; }
        public string Id { get; }

        public FinalityPolicy(int anchorHeight, string anchorHash)
        {
            if (anchorHeight < 1 || !IsHash(anchorHash))
                throw new ArgumentException("Finality requires an agreed non-genesis checkpoint height and SHA-256 hash.");
            AnchorHeight = anchorHeight;
            AnchorHash = anchorHash;
            Id = Crypto.Sha256("povix-finality-v1|" + Blockchain.NetworkId + "|" + anchorHeight.ToString(CultureInfo.InvariantCulture) + "|" + anchorHash);
        }

        public static FinalityPolicy FromConfiguration()
        {
            string height = ConfigurationManager.AppSettings["FinalityAnchorHeight"];
            string hash = ConfigurationManager.AppSettings["FinalityAnchorHash"];
            if (string.IsNullOrWhiteSpace(height) && string.IsNullOrWhiteSpace(hash))
            {
                if (string.Equals(ConfigurationManager.AppSettings["RequireFinality"], "true", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Configure FinalityAnchorHeight e FinalityAnchorHash com o checkpoint acordado pelos validadores antes de iniciar a rede.");
                return null;
            }
            int parsed;
            if (!int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                throw new InvalidOperationException("Invalid FinalityAnchorHeight.");
            return new FinalityPolicy(parsed, hash);
        }

        internal static Block[] CopyBlocks(IEnumerable<Block> blocks)
        {
            using (var memory = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(Block[]));
                serializer.WriteObject(memory, blocks.ToArray());
                memory.Position = 0;
                return (Block[])serializer.ReadObject(memory);
            }
        }

        internal static string KeyIdentity(string publicKey)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(publicKey);
                return Crypto.Sha256(rsa.ToXmlString(false));
            }
        }

        internal static bool IsHash(string value)
        { return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }

        public string Payload(int height, string hash)
        {
            if (height <= AnchorHeight || !IsHash(hash)) throw new ArgumentException("Invalid finality vote target.");
            return "povix-finality-vote-v1|" + Id + "|" + height.ToString(CultureInfo.InvariantCulture) + "|" + hash;
        }

        // A strict >2/3 quorum of the stake BEFORE the proposed block.
        // Voters cannot introduce their own committee or count duplicate keys.
        internal bool HasQuorum(Block block, ValidatorStake[] committee)
        {
            if (block.FinalityVotes == null || block.FinalityVotes.Count == 0) return false;
            var registered = committee.ToDictionary(v => v.PublicKey, StringComparer.Ordinal);
            var counted = new HashSet<string>(StringComparer.Ordinal);
            decimal total = committee.Sum(v => (decimal)v.LockedAmount);
            decimal approved = 0;
            foreach (FinalityVote vote in block.FinalityVotes)
            {
                ValidatorStake validator;
                if (vote == null || vote.Height != block.Height || vote.BlockHash != block.Hash ||
                    vote.PublicKey == null || !registered.TryGetValue(vote.PublicKey, out validator) ||
                    !counted.Add(vote.PublicKey) || !ProofOfStake.VerifyVote(vote.PublicKey, Payload(vote.Height, vote.BlockHash), vote.Signature))
                    throw new InvalidOperationException("Invalid or duplicated finality vote.");
                approved += validator.LockedAmount;
            }
            return counted.Count >= 2 && approved * 3 > total * 2;
        }

        internal static ValidatorStake[] Committee(IEnumerable<Block> prefix)
        {
            ValidatorStake[] committee = new Blockchain(prefix, null, true).GetActiveValidators().ToArray();
            if (committee.Length < 2 || committee.Select(v => KeyIdentity(v.PublicKey)).Distinct(StringComparer.Ordinal).Count() != committee.Length)
                throw new InvalidOperationException("Finality requires at least two distinct registered validator keys.");
            return committee;
        }

        public int FinalizedHeight(IReadOnlyList<Block> chain)
        {
            if (chain.Count <= AnchorHeight) return -1;
            if (chain[AnchorHeight].Hash != AnchorHash) throw new InvalidOperationException("Chain conflicts with the agreed finality checkpoint.");
            Committee(chain.Take(AnchorHeight + 1));
            int finalized = AnchorHeight;
            for (int i = AnchorHeight + 1; i < chain.Count; i++)
            {
                if (!HasQuorum(chain[i], Committee(chain.Take(i))))
                {
                    // Certificates after a gap are not a continuous finality proof.
                    if (chain.Skip(i + 1).Any(b => b.FinalityVotes != null && b.FinalityVotes.Count > 0))
                        throw new InvalidOperationException("Finality certificate gap.");
                    break;
                }
                Committee(chain.Take(i + 1)); // Do not finalize a committee with <2 keys.
                finalized = i;
            }
            return finalized;
        }
    }

    // Persist BEFORE signing. One key cannot vote for different blocks at the
    // same height, including across restarts or a new finality configuration.
    // There are deliberately no timeout-based unlocks or unsafe voting rounds.
    public sealed class FinalityVoteJournal : IDisposable
    {
        private readonly FileStream stream;
        private readonly Dictionary<string, string> decisions = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object sync = new object();

        public FinalityVoteJournal(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, false, 1024, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] parts = line.Split('|');
                        int height;
                        if (parts.Length != 4 || !FinalityPolicy.IsHash(parts[0]) ||
                            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height) || height < 1 ||
                            !FinalityPolicy.IsHash(parts[2]) || parts[3] != Crypto.Sha256(string.Join("|", parts.Take(3))))
                            throw new InvalidDataException("Corrupt finality vote journal; restore the original journal before voting.");
                        string key = parts[0] + "|" + parts[1];
                        string previous;
                        if (decisions.TryGetValue(key, out previous) && previous != parts[2])
                            throw new InvalidDataException("Conflicting persisted finality votes.");
                        decisions[key] = parts[2];
                    }
                }
                stream.Position = stream.Length;
            }
            catch { stream.Dispose(); throw; }
        }

        public FinalityVote Sign(FinalityPolicy policy, Block block, ValidatorStake signer)
        {
            lock (sync)
            {
                string payload = policy.Payload(block.Height, block.Hash);
                if (signer.PublicKey == null || signer.ValidatorId != Crypto.Sha256(signer.PublicKey))
                    throw new InvalidOperationException("Invalid validator signing identity.");
                string key = FinalityPolicy.KeyIdentity(signer.PublicKey) + "|" + block.Height.ToString(CultureInfo.InvariantCulture);
                string hash;
                if (decisions.TryGetValue(key, out hash) && hash != block.Hash)
                    throw new InvalidOperationException("Validator already voted for a different block at this height.");
                if (hash == null)
                {
                    string line = key + "|" + block.Hash;
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "|" + Crypto.Sha256(line) + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                    decisions[key] = block.Hash;
                }
                return new FinalityVote { Height = block.Height, BlockHash = block.Hash,
                    PublicKey = signer.PublicKey, Signature = signer.CreateVote(payload) };
            }
        }

        public void Dispose() { lock (sync) stream.Dispose(); }
    }
}
