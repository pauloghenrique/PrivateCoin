using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PrivateCoin.Core
{
    [DataContract]
    internal sealed class PeerAddressEntry
    {
        [DataMember(Order = 1)] public string Endpoint { get; set; }
        [DataMember(Order = 2)] public DateTime LastSeenUtc { get; set; }
        [DataMember(Order = 3)] public DateTime LastAttemptUtc { get; set; }
        [DataMember(Order = 4)] public DateTime LastSuccessUtc { get; set; }
        [DataMember(Order = 5)] public int Failures { get; set; }
    }

    /// <summary>Persistent peer address cache inspired by Bitcoin Core's peers.dat.</summary>
    internal sealed class PeerAddressBook
    {
        private const int MaximumEntries = 2048;
        private readonly object sync = new object();
        private readonly object saveSync = new object();
        private readonly string filePath;
        private readonly Dictionary<string, PeerAddressEntry> entries =
            new Dictionary<string, PeerAddressEntry>(StringComparer.OrdinalIgnoreCase);

        public PeerAddressBook(string filePath)
        {
            this.filePath = filePath;
        }

        public string[] Load()
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return new string[0];
            try
            {
                PeerAddressEntry[] loaded;
                using (var stream = File.OpenRead(filePath))
                    loaded = (PeerAddressEntry[])new DataContractJsonSerializer(typeof(PeerAddressEntry[])).ReadObject(stream);
                lock (sync)
                {
                    foreach (PeerAddressEntry entry in loaded ?? new PeerAddressEntry[0])
                        if (entry != null && !string.IsNullOrWhiteSpace(entry.Endpoint)) entries[entry.Endpoint] = entry;
                    Trim();
                    return entries.Keys.ToArray();
                }
            }
            catch (Exception error) when (error is IOException || error is SerializationException || error is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        public void Seen(string endpoint)
        {
            lock (sync)
            {
                PeerAddressEntry entry = GetOrCreate(endpoint);
                entry.LastSeenUtc = DateTime.UtcNow;
                Trim();
            }
        }

        public void Attempted(string endpoint)
        {
            lock (sync) GetOrCreate(endpoint).LastAttemptUtc = DateTime.UtcNow;
        }

        public void Succeeded(string endpoint)
        {
            lock (sync)
            {
                PeerAddressEntry entry = GetOrCreate(endpoint);
                entry.LastSeenUtc = entry.LastSuccessUtc = DateTime.UtcNow;
                entry.Failures = 0;
            }
        }

        public void Failed(string endpoint)
        {
            lock (sync)
            {
                PeerAddressEntry entry = GetOrCreate(endpoint);
                if (entry.Failures < int.MaxValue) entry.Failures++;
            }
        }

        public string[] Select(int count)
        {
            lock (sync)
                return entries.Values
                    .OrderBy(entry => entry.Failures)
                    .ThenByDescending(entry => entry.LastSuccessUtc)
                    .ThenBy(entry => entry.LastAttemptUtc)
                    .Take(count)
                    .Select(entry => entry.Endpoint)
                    .ToArray();
        }

        public void Save()
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            lock (saveSync)
            {
                PeerAddressEntry[] snapshot;
                lock (sync) snapshot = entries.Values.ToArray();
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temporary = filePath + ".tmp";
                using (var stream = File.Create(temporary))
                    new DataContractJsonSerializer(typeof(PeerAddressEntry[])).WriteObject(stream, snapshot);
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(temporary, filePath);
            }
        }

        private PeerAddressEntry GetOrCreate(string endpoint)
        {
            PeerAddressEntry entry;
            if (!entries.TryGetValue(endpoint, out entry))
            {
                entry = new PeerAddressEntry { Endpoint = endpoint, LastSeenUtc = DateTime.UtcNow };
                entries[endpoint] = entry;
            }
            return entry;
        }

        private void Trim()
        {
            if (entries.Count <= MaximumEntries) return;
            foreach (string endpoint in entries.Values.OrderByDescending(entry => entry.Failures)
                .ThenBy(entry => entry.LastSeenUtc).Take(entries.Count - MaximumEntries).Select(entry => entry.Endpoint).ToArray())
                entries.Remove(endpoint);
        }
    }
}
