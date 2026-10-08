using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Povix.WalletBridge
{
    /// <summary>Opens the Desktop's DPAPI collection locally and returns an authenticated encrypted copy.</summary>
    public static class WalletFileConverter
    {
        public const int MaximumFileBytes = 6000000;
        public const int Iterations = 210000;
        private static byte[] Unprotect(byte[] value) => ProtectedData.Unprotect(value, Encoding.UTF8.GetBytes("PrivateCoin"), DataProtectionScope.CurrentUser);
        public static object ConvertFile(byte[] contents, string password) => ConvertFile(contents, password,
            Unprotect);

        public static WalletFileSummary InspectFile(byte[] contents) => InspectFile(contents, Unprotect);

        public static WalletFileSummary InspectFile(byte[] contents, Func<byte[], byte[]> unprotect)
        {
            WalletCollection collection = OpenCollection(contents, unprotect);
            try { return new WalletFileSummary(collection.Wallets.Count, collection.Wallets.Sum(wallet => wallet.PrivateKeys.Count)); }
            finally { ClearKeys(collection); }
        }

        public static object ConvertFile(byte[] contents, string password, Func<byte[], byte[]> unprotect)
        {
            if (password == null || password.Length < 10 || password.Length > 1024)
                throw new WalletImportException("copy_password", "Defina uma senha entre 10 e 1024 caracteres para a cópia local do DEX. O wallets.dat é aberto pelo Windows, sem essa senha.");
            WalletCollection collection = OpenCollection(contents, unprotect);
            byte[] payload = null, key = null, passwordBytes = null, aesKey = null, macKey = null;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 16 };
                // Export the whole collection, without selecting or changing the original Desktop file.
                payload = Encoding.UTF8.GetBytes(json.Serialize(collection));
                byte[] salt = RandomBytes(16), iv = RandomBytes(16), cipher, tag;
                passwordBytes = Encoding.UTF8.GetBytes(password);
                using (var derivation = new Rfc2898DeriveBytes(passwordBytes, salt, Iterations, HashAlgorithmName.SHA256))
                    key = derivation.GetBytes(64);
                aesKey = key.Take(32).ToArray(); macKey = key.Skip(32).ToArray();
                using (var aes = Aes.Create())
                {
                    aes.Key = aesKey; aes.IV = iv;
                    using (var encryptor = aes.CreateEncryptor()) cipher = encryptor.TransformFinalBlock(payload, 0, payload.Length);
                }
                using (var hmac = new HMACSHA256(macKey)) tag = hmac.ComputeHash(salt.Concat(iv).Concat(cipher).ToArray());
                return new { format = "povix-dex-wallet-v1", iterations = Iterations,
                    salt = System.Convert.ToBase64String(salt), iv = System.Convert.ToBase64String(iv),
                    data = System.Convert.ToBase64String(cipher), hmac = System.Convert.ToBase64String(tag) };
            }
            catch (Exception error) when (error is CryptographicException || error is NotSupportedException || error is InvalidOperationException || error is ArgumentException)
            {
                throw new WalletImportException("copy_encryption", "O arquivo foi aberto, mas não foi possível cifrar a cópia local. Confira a instalação do .NET Framework 4.8 e atualize o Povix.WalletBridge.", error);
            }
            finally
            {
                ClearKeys(collection);
                foreach (byte[] bytes in new[] { payload, key, passwordBytes, aesKey, macKey })
                    if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private static WalletCollection OpenCollection(byte[] contents, Func<byte[], byte[]> unprotect)
        {
            if (contents == null || contents.Length < 20)
                throw new WalletImportException("wallet_file", "O arquivo está vazio ou incompleto. Escolha o wallets.dat usado pelo PrivateCoin.Desktop.");
            if (contents.Length > MaximumFileBytes)
                throw new WalletImportException("file_size", "O wallets.dat ultrapassa o limite de 6 MB do auxiliar local.");
            byte[] clear = null;
            WalletCollection collection = null;
            try
            {
                try { clear = unprotect(contents); }
                catch (CryptographicException error)
                {
                    throw new WalletImportException("windows_protection", "O Windows não conseguiu descriptografar este arquivo. No Desktop, use Pasta da carteira e selecione o wallets.dat em uso. Execute o auxiliar com o mesmo usuário Windows do Desktop. A senha da cópia local não desbloqueia a proteção do arquivo.", error);
                }
                try
                {
                    if (clear == null || clear.Length == 0 || clear.Length > MaximumFileBytes) throw new ArgumentException();
                    var json = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 64 };
                    collection = json.Deserialize<WalletCollection>(new UTF8Encoding(false, true).GetString(clear).TrimStart('\uFEFF'));
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is FormatException)
                {
                    throw new WalletImportException("wallet_format", "O Windows abriu o arquivo, mas o conteúdo não corresponde ao formato de carteiras do PrivateCoin.Desktop.", error);
                }
                if (collection?.Wallets == null || collection.Wallets.Count == 0)
                    throw new WalletImportException("wallet_empty", "O arquivo aberto não contém uma coleção de carteiras. Escolha o wallets.dat em uso pelo Desktop.");
                if (collection.Wallets.Count > 100)
                    throw new WalletImportException("wallet_limit", "O arquivo contém mais de 100 carteiras, acima do limite do DEX.");
                if (collection.Wallets.Any(wallet => wallet == null || wallet.PrivateKeys == null || wallet.PrivateKeys.Count == 0))
                    throw new WalletImportException("wallet_keys", "Há uma carteira sem chaves no arquivo. Abra a pasta pelo Desktop e escolha seu wallets.dat atualizado.");
                if (collection.Wallets.Any(wallet => wallet.PrivateKeys.Count > 1000))
                    throw new WalletImportException("key_limit", "Uma carteira contém mais de 1.000 chaves, acima do limite do DEX.");
                if (collection.Wallets.Any(wallet => wallet.PrivateKeys.Any(xml => string.IsNullOrEmpty(xml) || xml.Length > 5000)))
                    throw new WalletImportException("wallet_keys", "O arquivo contém uma chave vazia ou inválida. Confira o arquivo usado pelo Desktop.");
                return collection;
            }
            catch { ClearKeys(collection); throw; }
            finally { if (clear != null) Array.Clear(clear, 0, clear.Length); }
        }

        private static void ClearKeys(WalletCollection collection)
        {
            if (collection?.Wallets == null) return;
            foreach (SavedWallet wallet in collection.Wallets) wallet?.PrivateKeys?.Clear();
        }

        private static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        public sealed class WalletCollection { public List<SavedWallet> Wallets { get; set; } }
        public sealed class SavedWallet { public string Name { get; set; } public List<string> PrivateKeys { get; set; } }
    }

    public sealed class WalletFileSummary
    {
        internal WalletFileSummary(int wallets, int addresses) { WalletCount = wallets; AddressCount = addresses; }
        public int WalletCount { get; }
        public int AddressCount { get; }
    }

    public sealed class WalletImportException : Exception
    {
        public WalletImportException(string code, string message, Exception inner = null) : base(message, inner) { Code = code; }
        public string Code { get; }
    }
}
