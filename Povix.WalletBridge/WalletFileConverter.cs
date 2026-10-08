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
        public static object ConvertFile(byte[] contents, string password) => ConvertFile(contents, password,
            value => ProtectedData.Unprotect(value, Encoding.UTF8.GetBytes("PrivateCoin"), DataProtectionScope.CurrentUser));

        public static object ConvertFile(byte[] contents, string password, Func<byte[], byte[]> unprotect)
        {
            if (contents == null || contents.Length < 20 || contents.Length > MaximumFileBytes)
                throw new ArgumentException("Escolha um arquivo de carteiras de até 6 MB.");
            if (password == null || password.Length < 10 || password.Length > 1024)
                throw new ArgumentException("Defina uma senha entre 10 e 1024 caracteres para a cópia local.");
            byte[] clear = null, payload = null, key = null, passwordBytes = null, aesKey = null, macKey = null;
            try
            {
                clear = unprotect(contents);
                var json = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 16 };
                var collection = json.Deserialize<WalletCollection>(Encoding.UTF8.GetString(clear));
                if (collection?.Wallets == null || collection.Wallets.Count < 1 || collection.Wallets.Count > 100 ||
                    collection.Wallets.Any(wallet => wallet == null || wallet.PrivateKeys == null || wallet.PrivateKeys.Count < 1 ||
                        wallet.PrivateKeys.Count > 1000 || wallet.PrivateKeys.Any(xml => string.IsNullOrEmpty(xml) || xml.Length > 5000)))
                    throw new ArgumentException("O arquivo não contém carteiras válidas.");
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
            finally
            {
                foreach (byte[] bytes in new[] { clear, payload, key, passwordBytes, aesKey, macKey })
                    if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            }
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
}
