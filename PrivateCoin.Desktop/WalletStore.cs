using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace PrivateCoin.Desktop
{
    internal sealed class NamedWallet : IDisposable
    {
        public NamedWallet(string name, Wallet wallet)
        {
            Name = name;
            Wallet = wallet;
        }

        public string Name { get; private set; }
        public Wallet Wallet { get; private set; }
        public override string ToString() => Name;
        public void Dispose() => Wallet.Dispose();
    }

    /// <summary>
    /// Keeps private wallet material separate from the public, distributable network state.
    /// </summary>
    internal sealed class WalletStore
    {
        private static readonly byte[] Entropy = { 80, 114, 105, 118, 97, 116, 101, 67, 111, 105, 110 };
        private readonly string walletFilePath;
        private readonly string networkFilePath;

        public WalletStore()
        {
            string directory = FindProjectDirectory();
            walletFilePath = Path.Combine(directory, "wallets.dat");
            networkFilePath = Path.Combine(directory, "Blockchain.json");
            string previousNetworkFilePath = Path.Combine(directory, "blockchain.json");
            if (!File.Exists(networkFilePath) && File.Exists(previousNetworkFilePath))
                File.Move(previousNetworkFilePath, networkFilePath);
        }

        public bool WalletExists => File.Exists(walletFilePath);
        public bool NetworkExists => File.Exists(networkFilePath);
        public bool NetworkNeedsUpgrade { get; private set; }

        private static string FindProjectDirectory()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PrivateCoin.Desktop.csproj")))
                    return directory.FullName;

                string projectDirectory = Path.Combine(directory.FullName, "PrivateCoin.Desktop");
                if (File.Exists(Path.Combine(projectDirectory, "PrivateCoin.Desktop.csproj")))
                    return projectDirectory;

                directory = directory.Parent;
            }

            // In a published copy there is no project file, so the executable directory
            // is the closest equivalent to the project directory.
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public void Save(IEnumerable<NamedWallet> wallets, Blockchain blockchain)
        {
            if (wallets == null) throw new ArgumentNullException(nameof(wallets));
            if (blockchain == null) throw new ArgumentNullException(nameof(blockchain));

            // Store the public file first. This also makes migration from the old combined
            // wallets.dat safe: that file is not replaced until the chain has been written.
            byte[] networkData = Serialize(blockchain.Blocks.ToList());
            WriteJson(networkFilePath, new StoredNetwork
            {
                Data = Convert.ToBase64String(networkData),
                Sha256 = CalculateSha256(networkData)
            }, false);
            NetworkNeedsUpgrade = false;
            WriteJson(walletFilePath, new StoredWalletCollection
            {
                Wallets = wallets.Select(item => new StoredWallet
                {
                    Name = item.Name,
                    PrivateKeys = item.Wallet.ExportPrivateKeys().ToList()
                }).ToList()
            }, true);
        }

        public List<NamedWallet> LoadWallets()
        {
            StoredWalletCollection state = ReadJson<StoredWalletCollection>(walletFilePath, true);
            if (state == null || state.Wallets == null || state.Wallets.Count == 0)
                throw new SerializationException("O arquivo da carteira local está incompleto.");

            var wallets = new List<NamedWallet>();
            try
            {
                foreach (StoredWallet item in state.Wallets)
                    wallets.Add(new NamedWallet(item.Name, Wallet.FromPrivateKeys(item.PrivateKeys)));
                return wallets;
            }
            catch
            {
                foreach (NamedWallet wallet in wallets) wallet.Dispose();
                throw;
            }
        }

        public Blockchain LoadNetwork()
        {
            StoredNetwork state = ReadJson<StoredNetwork>(networkFilePath, false);
            if (state == null)
                throw new SerializationException("O arquivo da rede está incompleto.");

            List<Block> blocks;
            if (!string.IsNullOrWhiteSpace(state.Data) && !string.IsNullOrWhiteSpace(state.Sha256))
            {
                byte[] networkData;
                try { networkData = Convert.FromBase64String(state.Data); }
                catch (FormatException error) { throw new SerializationException("Os dados da rede não estão em Base64 válido.", error); }

                string calculatedHash = CalculateSha256(networkData);
                if (!string.Equals(calculatedHash, state.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SerializationException("A verificação SHA-256 do arquivo da rede falhou.");

                blocks = Deserialize<List<Block>>(networkData);
                NetworkNeedsUpgrade = false;
            }
            else if (state.Blocks != null)
            {
                // Compatibility with blockchain.json files created before the SHA-256 envelope.
                blocks = state.Blocks;
                NetworkNeedsUpgrade = true;
            }
            else throw new SerializationException("O arquivo da rede está incompleto.");

            return new Blockchain(blocks);
        }

        /// <summary>Reads the original combined wallets.dat so existing installations can be migrated.</summary>
        public bool TryLoadLegacy(out List<NamedWallet> wallets, out Blockchain blockchain)
        {
            wallets = null;
            blockchain = null;
            if (!WalletExists || NetworkExists) return false;

            LegacyStoredState state;
            try { state = ReadJson<LegacyStoredState>(walletFilePath, true); }
            catch (SerializationException) { return false; }
            if (state == null || state.Wallets == null || state.Wallets.Count == 0 || state.Blocks == null)
                return false;

            var loadedWallets = new List<NamedWallet>();
            try
            {
                foreach (StoredWallet item in state.Wallets)
                    loadedWallets.Add(new NamedWallet(item.Name, Wallet.FromPrivateKeys(item.PrivateKeys)));
                blockchain = new Blockchain(state.Blocks);
                wallets = loadedWallets;
                return true;
            }
            catch
            {
                foreach (NamedWallet wallet in loadedWallets) wallet.Dispose();
                throw;
            }
        }

        private static void WriteJson<T>(string path, T value, bool protect)
        {
            byte[] contents = Serialize(value);
            if (protect) contents = ProtectedData.Protect(contents, Entropy, DataProtectionScope.CurrentUser);

            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, contents);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static T ReadJson<T>(string path, bool protectedData)
        {
            byte[] contents = File.ReadAllBytes(path);
            if (protectedData) contents = ProtectedData.Unprotect(contents, Entropy, DataProtectionScope.CurrentUser);
            return Deserialize<T>(contents);
        }

        private static byte[] Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        private static T Deserialize<T>(byte[] contents)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(contents)) return (T)serializer.ReadObject(stream);
        }

        private static string CalculateSha256(byte[] contents)
        {
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(contents).Select(value => value.ToString("x2")));
        }

        [DataContract]
        private sealed class StoredWalletCollection
        {
            [DataMember(Order = 1)] public List<StoredWallet> Wallets { get; set; }
        }

        [DataContract]
        private sealed class StoredNetwork
        {
            [DataMember(Order = 1, EmitDefaultValue = false)] public string Data { get; set; }
            [DataMember(Order = 2, EmitDefaultValue = false)] public string Sha256 { get; set; }
            [DataMember(Order = 3, EmitDefaultValue = false)] public List<Block> Blocks { get; set; }
        }

        [DataContract]
        private sealed class LegacyStoredState
        {
            [DataMember(Order = 1)] public List<StoredWallet> Wallets { get; set; }
            [DataMember(Order = 2)] public List<Block> Blocks { get; set; }
        }

        [DataContract]
        private sealed class StoredWallet
        {
            [DataMember(Order = 1)] public string Name { get; set; }
            [DataMember(Order = 2)] public List<string> PrivateKeys { get; set; }
        }
    }
}
