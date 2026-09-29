using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Desktop
{
    internal sealed class NamedWallet : IDisposable
    {
        public NamedWallet(string name, Wallet wallet, long lockedStake = 0, string validatorRewardAddress = null, string recoveryPhrase = null, bool isDeterministic = false)
        {
            if (lockedStake < 0) throw new ArgumentOutOfRangeException(nameof(lockedStake));
            if (lockedStake > 0 && string.IsNullOrWhiteSpace(validatorRewardAddress))
                throw new ArgumentException("Um endereço de recompensa é necessário para restaurar um validador.", nameof(validatorRewardAddress));
            Name = name;
            Wallet = wallet;
            LockedStake = lockedStake;
            ValidatorRewardAddress = validatorRewardAddress;
            RecoveryPhrase = recoveryPhrase;
            IsDeterministic = isDeterministic;
        }

        public string Name { get; private set; }
        public Wallet Wallet { get; private set; }
        public long LockedStake { get; private set; }
        public string ValidatorRewardAddress { get; private set; }
        public string RecoveryPhrase { get; private set; }
        public bool IsDeterministic { get; private set; }
        public bool IsValidator => LockedStake > 0;
        public ValidatorStake Validator => IsValidator
            ? new ValidatorStake(Name, ValidatorRewardAddress, LockedStake, Wallet.OwnedOneTimeAddresses)
            : null;

        public ValidatorStake ActivateValidator(long amount, string rewardAddress)
        {
            if (IsValidator) throw new InvalidOperationException("Esta carteira já está ativa como validadora.");
            var stake = new ValidatorStake(Name, rewardAddress, amount, Wallet.OwnedOneTimeAddresses);
            LockedStake = amount;
            ValidatorRewardAddress = rewardAddress;
            return stake;
        }

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
        private readonly string recoveryFilePath;
        private readonly Dictionary<string, StoredNetworkWallet> knownNetworkWallets =
            new Dictionary<string, StoredNetworkWallet>(StringComparer.Ordinal);

        public WalletStore()
        {
            string directory = FindProjectDirectory();
            walletFilePath = Path.Combine(directory, "wallets.dat");
            networkFilePath = Path.Combine(directory, "Blockchain.json");
            recoveryFilePath = Path.Combine(directory, "recovery.dat");
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

        public void Save(IEnumerable<NamedWallet> wallets, Blockchain blockchain, IEnumerable<Transaction> pendingTransactions)
        {
            if (wallets == null) throw new ArgumentNullException(nameof(wallets));
            if (blockchain == null) throw new ArgumentNullException(nameof(blockchain));
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));

            // Store the public file first. This also makes migration from the old combined
            // wallets.dat safe: that file is not replaced until the chain has been written.
            var pending = pendingTransactions.ToList();
            foreach (NamedWallet wallet in wallets)
            {
                List<string> addresses = wallet.Wallet.OwnedOneTimeAddresses.ToList();
                string recoveryId = GetRecoveryId(wallet.RecoveryPhrase);
                string id = recoveryId ?? (addresses.Count == 0 ? null : addresses[0]);
                if (string.IsNullOrWhiteSpace(id)) continue;
                knownNetworkWallets[id] = new StoredNetworkWallet
                {
                    Id = id,
                    Name = wallet.Name,
                    Addresses = addresses,
                    TokenBalance = blockchain.GetBalance(addresses, pending),
                    RecoveryId = recoveryId,
                    LockedStake = wallet.LockedStake,
                    ValidatorRewardAddress = wallet.ValidatorRewardAddress
                };
            }
            foreach (StoredNetworkWallet wallet in knownNetworkWallets.Values)
                wallet.TokenBalance = blockchain.GetBalance(wallet.Addresses, pending);

            byte[] networkData = Serialize(new StoredNetworkData
            {
                Blocks = blockchain.Blocks.ToList(),
                PendingTransactions = pending,
                Wallets = knownNetworkWallets.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList()
            });
            WriteJson(networkFilePath, new StoredNetwork
            {
                SchemaVersion = 3,
                Data = Convert.ToBase64String(networkData),
                Sha256 = CalculateSha256(networkData)
            }, false);
            NetworkNeedsUpgrade = false;
            WriteJson(walletFilePath, new StoredWalletCollection
            {
                Wallets = wallets.Select(item => new StoredWallet
                {
                    Name = item.Name,
                    PrivateKeys = item.Wallet.ExportPrivateKeys().ToList(),
                    LockedStake = item.LockedStake,
                    ValidatorRewardAddress = item.ValidatorRewardAddress,
                    RecoveryPhrase = item.RecoveryPhrase,
                    RecoveryVersion = item.IsDeterministic ? 1 : 0,
                    Addresses = item.Wallet.OwnedOneTimeAddresses.ToList()
                }).ToList()
            }, true);
            SaveRecoveryCopies(wallets.Where(item => !item.IsDeterministic && !string.IsNullOrWhiteSpace(item.RecoveryPhrase)));
        }

        public NamedWallet Recover(string phrase, string requestedName, Blockchain blockchain)
        {
            if (blockchain == null) throw new ArgumentNullException(nameof(blockchain));
            string normalized;
            if (!RecoveryPhraseGenerator.TryNormalize(phrase, out normalized))
                throw new ArgumentException("A frase deve conter exatamente as 12 palavras válidas, na ordem original.", nameof(phrase));
            string recoveryId = GetRecoveryId(normalized);
            StoredNetworkWallet networkWallet = knownNetworkWallets.Values.FirstOrDefault(item =>
                string.Equals(item.RecoveryId, recoveryId, StringComparison.Ordinal));
            if (networkWallet != null)
            {
                int addressCount = networkWallet.Addresses == null ? 0 : networkWallet.Addresses.Count;
                Wallet restoredWallet = Wallet.FromSeed(RecoveryPhraseGenerator.ToSeed(normalized), addressCount);
                if (!restoredWallet.OwnedOneTimeAddresses.SequenceEqual(networkWallet.Addresses ?? new List<string>(), StringComparer.Ordinal))
                {
                    restoredWallet.Dispose();
                    throw new SerializationException("Os endereços públicos registrados para a carteira não conferem com a frase.");
                }
                string restoredName = string.IsNullOrWhiteSpace(requestedName) ? networkWallet.Name : requestedName.Trim();
                return new NamedWallet(restoredName, restoredWallet, networkWallet.LockedStake,
                    networkWallet.ValidatorRewardAddress, normalized, true);
            }
            StoredRecoveryCollection collection = File.Exists(recoveryFilePath)
                ? ReadJson<StoredRecoveryCollection>(recoveryFilePath, false) : null;
            StoredRecovery record = collection == null || collection.Wallets == null
                ? null : collection.Wallets.FirstOrDefault(item => item.Id == CalculateSha256(Encoding.UTF8.GetBytes(normalized)));
            if (record != null)
            {
                StoredRecoveryWallet restored = DecryptRecovery(record, normalized);
                string legacyName = string.IsNullOrWhiteSpace(requestedName) ? restored.Name : requestedName.Trim();
                return new NamedWallet(legacyName, Wallet.FromPrivateKeys(restored.PrivateKeys), restored.LockedStake,
                    restored.ValidatorRewardAddress, normalized);
            }

            var knownAddresses = new HashSet<string>(blockchain.Blocks
                .SelectMany(block => block.Transactions)
                .SelectMany(transaction => transaction.Outputs)
                .Select(output => output.OneTimeAddress), StringComparer.Ordinal);
            byte[] seed = RecoveryPhraseGenerator.ToSeed(normalized);
            var recovered = Wallet.FromSeed(seed, 0);
            int unused = 0;
            while (unused < 20)
            {
                string address = recovered.CreateReceiveAddress();
                unused = knownAddresses.Contains(address) ? 0 : unused + 1;
            }
            string name = string.IsNullOrWhiteSpace(requestedName) ? "Carteira recuperada" : requestedName.Trim();
            return new NamedWallet(name, recovered, 0, null, normalized, true);
        }

        private void SaveRecoveryCopies(IEnumerable<NamedWallet> recoverableWallets)
        {
            StoredRecoveryCollection collection = File.Exists(recoveryFilePath)
                ? ReadJson<StoredRecoveryCollection>(recoveryFilePath, false) : new StoredRecoveryCollection();
            if (collection == null) collection = new StoredRecoveryCollection();
            if (collection.Wallets == null) collection.Wallets = new List<StoredRecovery>();
            foreach (NamedWallet wallet in recoverableWallets)
            {
                string normalized;
                if (!RecoveryPhraseGenerator.TryNormalize(wallet.RecoveryPhrase, out normalized)) continue;
                string id = CalculateSha256(Encoding.UTF8.GetBytes(normalized));
                collection.Wallets.RemoveAll(item => item.Id == id);
                collection.Wallets.Add(EncryptRecovery(new StoredRecoveryWallet
                {
                    Name = wallet.Name,
                    PrivateKeys = wallet.Wallet.ExportPrivateKeys().ToList(),
                    LockedStake = wallet.LockedStake,
                    ValidatorRewardAddress = wallet.ValidatorRewardAddress
                }, normalized, id));
            }
            WriteJson(recoveryFilePath, collection, false);
        }

        private static StoredRecovery EncryptRecovery(StoredRecoveryWallet wallet, string phrase, string id)
        {
            byte[] salt = RandomBytes(16), iv = RandomBytes(16), material;
            using (var derive = new Rfc2898DeriveBytes(phrase, salt, 100000)) material = derive.GetBytes(64);
            byte[] cipher;
            byte[] plain = Serialize(wallet);
            using (Aes aes = Aes.Create())
            {
                aes.Key = material.Take(32).ToArray(); aes.IV = iv;
                using (ICryptoTransform encryptor = aes.CreateEncryptor()) cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            }
            byte[] authenticated = salt.Concat(iv).Concat(cipher).ToArray();
            byte[] mac;
            using (var hmac = new HMACSHA256(material.Skip(32).ToArray())) mac = hmac.ComputeHash(authenticated);
            return new StoredRecovery { Id = id, Salt = Convert.ToBase64String(salt), Iv = Convert.ToBase64String(iv), Data = Convert.ToBase64String(cipher), Hmac = Convert.ToBase64String(mac) };
        }

        private static StoredRecoveryWallet DecryptRecovery(StoredRecovery record, string phrase)
        {
            byte[] salt = Convert.FromBase64String(record.Salt), iv = Convert.FromBase64String(record.Iv), cipher = Convert.FromBase64String(record.Data), material;
            using (var derive = new Rfc2898DeriveBytes(phrase, salt, 100000)) material = derive.GetBytes(64);
            byte[] authenticated = salt.Concat(iv).Concat(cipher).ToArray(), expected;
            using (var hmac = new HMACSHA256(material.Skip(32).ToArray())) expected = hmac.ComputeHash(authenticated);
            if (!SlowEquals(expected, Convert.FromBase64String(record.Hmac))) throw new CryptographicException("A cópia de recuperação está corrompida.");
            using (Aes aes = Aes.Create())
            {
                aes.Key = material.Take(32).ToArray(); aes.IV = iv;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    return Deserialize<StoredRecoveryWallet>(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
            }
        }

        private static byte[] RandomBytes(int count) { var bytes = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return bytes; }
        private static bool SlowEquals(byte[] left, byte[] right) { if (left.Length != right.Length) return false; int difference = 0; for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i]; return difference == 0; }

        public List<NamedWallet> LoadWallets()
        {
            StoredWalletCollection state = ReadJson<StoredWalletCollection>(walletFilePath, true);
            if (state == null || state.Wallets == null || state.Wallets.Count == 0)
                throw new SerializationException("O arquivo da carteira local está incompleto.");

            var wallets = new List<NamedWallet>();
            try
            {
                foreach (StoredWallet item in state.Wallets)
                {
                    Wallet wallet = item.RecoveryVersion == 1
                        ? Wallet.FromSeed(RecoveryPhraseGenerator.ToSeed(item.RecoveryPhrase), item.PrivateKeys.Count)
                        : Wallet.FromPrivateKeys(item.PrivateKeys);
                    if (item.Addresses != null && item.Addresses.Count > 0 &&
                        !wallet.OwnedOneTimeAddresses.SequenceEqual(item.Addresses, StringComparer.Ordinal))
                    {
                        wallet.Dispose();
                        throw new SerializationException("Os endereços gravados em wallets.dat não conferem com as chaves da carteira.");
                    }
                    wallets.Add(new NamedWallet(item.Name, wallet, item.LockedStake,
                        item.ValidatorRewardAddress, item.RecoveryPhrase, item.RecoveryVersion == 1));
                }
                return wallets;
            }
            catch
            {
                foreach (NamedWallet wallet in wallets) wallet.Dispose();
                throw;
            }
        }

        public Blockchain LoadNetwork(out List<Transaction> pendingTransactions)
        {
            pendingTransactions = new List<Transaction>();
            StoredNetwork state = ReadJson<StoredNetwork>(networkFilePath, false);
            if (state == null)
                throw new SerializationException("O arquivo da rede está incompleto.");

            List<Block> blocks;
            bool validatePublicBalances = false;
            if (!string.IsNullOrWhiteSpace(state.Data) && !string.IsNullOrWhiteSpace(state.Sha256))
            {
                byte[] networkData;
                try { networkData = Convert.FromBase64String(state.Data); }
                catch (FormatException error) { throw new SerializationException("Os dados da rede não estão em Base64 válido.", error); }

                string calculatedHash = CalculateSha256(networkData);
                if (!string.Equals(calculatedHash, state.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SerializationException("A verificação SHA-256 do arquivo da rede falhou.");

                if (state.SchemaVersion >= 2)
                {
                    StoredNetworkData storedData = Deserialize<StoredNetworkData>(networkData);
                    if (storedData == null || storedData.Blocks == null)
                        throw new SerializationException("Os dados da rede estão incompletos.");
                    blocks = storedData.Blocks;
                    pendingTransactions = storedData.PendingTransactions ?? new List<Transaction>();
                    if (storedData.Wallets != null)
                    {
                        foreach (StoredNetworkWallet wallet in storedData.Wallets)
                        {
                            if (wallet == null || string.IsNullOrWhiteSpace(wallet.Id) || wallet.Addresses == null)
                                throw new SerializationException("O cadastro público de carteiras da rede está incompleto.");
                            knownNetworkWallets[wallet.Id] = wallet;
                        }
                    }
                    validatePublicBalances = state.SchemaVersion >= 3;
                }
                else
                {
                    // Version 1 stored only the confirmed blocks in the signed payload.
                    blocks = Deserialize<List<Block>>(networkData);
                    NetworkNeedsUpgrade = true;
                }
                NetworkNeedsUpgrade = state.SchemaVersion < 3;
            }
            else if (state.Blocks != null)
            {
                // Compatibility with blockchain.json files created before the SHA-256 envelope.
                blocks = state.Blocks;
                NetworkNeedsUpgrade = true;
            }
            else throw new SerializationException("O arquivo da rede está incompleto.");

            var blockchain = new Blockchain(blocks);
            blockchain.ValidatePendingTransactions(pendingTransactions);
            if (validatePublicBalances)
            {
                foreach (StoredNetworkWallet wallet in knownNetworkWallets.Values)
                {
                    long calculatedBalance = blockchain.GetBalance(wallet.Addresses, pendingTransactions);
                    if (wallet.TokenBalance != calculatedBalance)
                        throw new SerializationException("O saldo público de uma carteira não confere com a blockchain.");
                }
            }
            if (pendingTransactions.Any(transaction => transaction == null) ||
                pendingTransactions.Select(transaction => transaction.Id).Distinct(StringComparer.Ordinal).Count() != pendingTransactions.Count)
                throw new SerializationException("A fila de transações pendentes da rede é inválida.");
            return blockchain;
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

        private static string GetRecoveryId(string phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return null;
            string normalized;
            if (!RecoveryPhraseGenerator.TryNormalize(phrase, out normalized)) return null;
            byte[] seed = RecoveryPhraseGenerator.ToSeed(normalized);
            return CalculateSha256(Encoding.UTF8.GetBytes("PrivateCoin wallet recovery\n").Concat(seed).ToArray());
        }

        [DataContract]
        private sealed class StoredWalletCollection
        {
            [DataMember(Order = 1)] public List<StoredWallet> Wallets { get; set; }
        }

        [DataContract]
        private sealed class StoredNetwork
        {
            [DataMember(Order = 1, EmitDefaultValue = false)] public int SchemaVersion { get; set; }
            [DataMember(Order = 2, EmitDefaultValue = false)] public string Data { get; set; }
            [DataMember(Order = 3, EmitDefaultValue = false)] public string Sha256 { get; set; }
            [DataMember(Order = 4, EmitDefaultValue = false)] public List<Block> Blocks { get; set; }
        }

        [DataContract]
        private sealed class StoredNetworkData
        {
            [DataMember(Order = 1)] public List<Block> Blocks { get; set; }
            [DataMember(Order = 2)] public List<Transaction> PendingTransactions { get; set; }
            [DataMember(Order = 3, EmitDefaultValue = false)] public List<StoredNetworkWallet> Wallets { get; set; }
        }

        [DataContract]
        private sealed class StoredNetworkWallet
        {
            [DataMember(Order = 1)] public string Id { get; set; }
            [DataMember(Order = 2)] public string Name { get; set; }
            [DataMember(Order = 3)] public List<string> Addresses { get; set; }
            [DataMember(Order = 4)] public long TokenBalance { get; set; }
            [DataMember(Order = 5, EmitDefaultValue = false)] public string RecoveryId { get; set; }
            [DataMember(Order = 6, EmitDefaultValue = false)] public long LockedStake { get; set; }
            [DataMember(Order = 7, EmitDefaultValue = false)] public string ValidatorRewardAddress { get; set; }
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
            [DataMember(Order = 3, EmitDefaultValue = false)] public long LockedStake { get; set; }
            [DataMember(Order = 4, EmitDefaultValue = false)] public string ValidatorRewardAddress { get; set; }
            [DataMember(Order = 5, EmitDefaultValue = false)] public string RecoveryPhrase { get; set; }
            [DataMember(Order = 6, EmitDefaultValue = false)] public int RecoveryVersion { get; set; }
            [DataMember(Order = 7, EmitDefaultValue = false)] public List<string> Addresses { get; set; }
        }

        [DataContract] private sealed class StoredRecoveryCollection { [DataMember(Order = 1)] public List<StoredRecovery> Wallets { get; set; } = new List<StoredRecovery>(); }
        [DataContract] private sealed class StoredRecovery { [DataMember(Order = 1)] public string Id { get; set; } [DataMember(Order = 2)] public string Salt { get; set; } [DataMember(Order = 3)] public string Iv { get; set; } [DataMember(Order = 4)] public string Data { get; set; } [DataMember(Order = 5)] public string Hmac { get; set; } }
        [DataContract] private sealed class StoredRecoveryWallet { [DataMember(Order = 1)] public string Name { get; set; } [DataMember(Order = 2)] public List<string> PrivateKeys { get; set; } [DataMember(Order = 3, EmitDefaultValue = false)] public long LockedStake { get; set; } [DataMember(Order = 4, EmitDefaultValue = false)] public string ValidatorRewardAddress { get; set; } }
    }
}
