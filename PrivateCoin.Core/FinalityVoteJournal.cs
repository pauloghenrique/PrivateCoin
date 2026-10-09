using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace PrivateCoin.Core
{
    /// <summary>Durable anti-double-signing state. Keep this file local to the signer.</summary>
    public sealed class FinalityVoteJournal
    {
        private readonly string path;
        private readonly object sync = new object();
        public FinalityVoteJournal(string path) { this.path = Path.GetFullPath(path); }

        public FinalityVote GetOrCreate(Blockchain chain, int height, ValidatorStake validator)
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Serializes separate application instances sharing the same wallet directory.
                using (var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    List<FinalityVote> votes = new List<FinalityVote>();
                    if (File.Exists(path))
                        using (var input = File.OpenRead(path))
                            votes = (List<FinalityVote>)new DataContractJsonSerializer(typeof(List<FinalityVote>)).ReadObject(input);
                    if (votes == null || votes.Any(v => v == null || v.Height <= 0 ||
                        !ProofOfStake.VerifyVote(v.PublicKey, v.Payload(), v.Signature)) ||
                        votes.Select(v => v.PublicKey).Distinct(StringComparer.Ordinal).Count() != votes.Count)
                        throw new InvalidOperationException("The local finality vote journal is invalid.");
                    FinalityVote old = votes.SingleOrDefault(v => v.PublicKey == validator.PublicKey);
                    if (old != null && old.Height >= height)
                    {
                        if (old.Height != height || old.BlockHash != chain.Blocks[height].Hash ||
                            old.PreviousHash != chain.Blocks[height - 1].Hash)
                            throw new InvalidOperationException("This validator has already voted for another history at this height or later.");
                        return old.Copy();
                    }
                    FinalityVote vote = chain.CreateFinalityVote(height, validator);
                    if (old != null) votes.Remove(old);
                    votes.Add(vote);
                    string temporary = path + "." + Crypto.NewId() + ".tmp";
                    try
                    {
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            new DataContractJsonSerializer(typeof(List<FinalityVote>)).WriteObject(output, votes);
                            output.Flush(true);
                        }
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    // The signature is only returned for broadcasting after durable storage.
                    return vote.Copy();
                }
            }
        }
    }
}
