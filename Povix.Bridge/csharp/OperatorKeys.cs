using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Pkcs;
using Nethereum.KeyStore;
using Nethereum.Signer;

namespace Povix.Bridge.CSharp {
    public static class OperatorKeys {
        [DllImport("libc", SetLastError = true)] static extern int fchmod(int fd, uint mode);
        static bool Windows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static void RequirePrivate(string filename) {
            if ((File.GetAttributes(filename) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Keystore must not be a symbolic link.");
            if (Windows) {
                var user = WindowsIdentity.GetCurrent().User;
                var rules = File.GetAccessControl(filename).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
                if (rules.Any(r => r.AccessControlType == AccessControlType.Allow && !r.IdentityReference.Equals(user) && (r.FileSystemRights & FileSystemRights.ReadData) != 0)) throw new InvalidOperationException("Restrict keystore access to the current operator.");
            } else {
                // OS metadata only. No shell, password or key content is passed to this process.
                using (var p = Process.Start(new ProcessStartInfo("stat", "-c %a -- " + Quote(Path.GetFullPath(filename))) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })) {
                    string mode = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit();
                    if (p.ExitCode != 0 || mode.Length < 3 || mode.Substring(mode.Length - 2) != "00") throw new InvalidOperationException("Keystore must be private (chmod 600).");
                }
            }
        }
        static string Quote(string value) { return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        public static void WritePrivate(string filename, string json) {
            using (var stream = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                if (Windows) {
                    var acl = new FileSecurity(); var user = WindowsIdentity.GetCurrent().User; acl.SetOwner(user); acl.SetAccessRuleProtection(true, false); acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow)); File.SetAccessControl(filename, acl);
                } else if (fchmod(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 384) != 0) throw new IOException("Could not restrict keystore permissions.");
                byte[] bytes = Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); Array.Clear(bytes, 0, bytes.Length);
            }
        }
        static string Password(string variable, bool create) { var password = Environment.GetEnvironmentVariable(variable); if (string.IsNullOrEmpty(password) || create && password.Length < 16) throw new InvalidOperationException("Set " + variable + " locally; use at least 16 characters for creation."); return password; }
        static byte[] Random(int size) { var bytes = new byte[size]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return bytes; }
        static byte[] Derive(string password, byte[] salt) { var bytes = Encoding.UTF8.GetBytes(password); try { return SCrypt.Generate(bytes, salt, 131072, 8, 1, 32); } finally { Array.Clear(bytes, 0, bytes.Length); } }
        static byte[] Gcm(bool encrypt, byte[] key, byte[] iv, byte[] input) {
            var cipher = new GcmBlockCipher(new AesEngine()); cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, iv));
            var output = new byte[cipher.GetOutputSize(input.Length)]; int n = cipher.ProcessBytes(input, 0, input.Length, output, 0); n += cipher.DoFinal(output, n); return output.Take(n).ToArray();
        }
        public static JObject CreateNative(string filename) {
            string password = Password("NATIVE_KEYSTORE_PASSWORD", true); byte[] salt = Random(32), iv = Random(12), key = Derive(password, salt), plaintext = null;
            try {
                using (var rsa = new RSACryptoServiceProvider(3072)) {
                    rsa.PersistKeyInCsp = false; string publicKey = rsa.ToXmlString(false);
                    plaintext = PrivateKeyInfoFactory.CreatePrivateKeyInfo(DotNetUtilities.GetRsaKeyPair(rsa).Private).GetDerEncoded();
                    var encrypted = Gcm(true, key, iv, plaintext);
                    var json = new JObject { ["version"] = 1, ["kdf"] = "scrypt-N131072-r8-p1", ["salt"] = salt.ToHexLower(), ["iv"] = iv.ToHexLower(), ["tag"] = encrypted.Skip(encrypted.Length - 16).ToArray().ToHexLower(), ["ciphertext"] = encrypted.Take(encrypted.Length - 16).ToArray().ToHexLower(), ["publicKey"] = publicKey };
                    WritePrivate(filename, json.ToString(Formatting.None)); return new JObject { ["address"] = BridgeNative.Hash(publicKey), ["publicKey"] = publicKey };
                }
            } finally { Array.Clear(key, 0, key.Length); if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length); }
        }
        public static RSACryptoServiceProvider LoadNative(string filename) {
            RequirePrivate(filename); var j = JObject.Parse(File.ReadAllText(filename));
            if ((int)j["version"] != 1 || (string)j["kdf"] != "scrypt-N131072-r8-p1" || !BridgeConfig.Hex((string)j["salt"], 64) || !BridgeConfig.Hex((string)j["iv"], 24) || !BridgeConfig.Hex((string)j["tag"], 32) || ((string)j["ciphertext"]).Length > 32768) throw new InvalidOperationException("Invalid native keystore.");
            byte[] key = Derive(Password("NATIVE_KEYSTORE_PASSWORD", false), Native.Bytes((string)j["salt"])), plaintext = null;
            try {
                plaintext = Gcm(false, key, Native.Bytes((string)j["iv"]), Native.Bytes((string)j["ciphertext"] + (string)j["tag"]));
                var parameters = DotNetUtilities.ToRSAParameters((RsaPrivateCrtKeyParameters)PrivateKeyFactory.CreateKey(plaintext));
                var rsa = new RSACryptoServiceProvider(); rsa.PersistKeyInCsp = false;
                try { rsa.ImportParameters(parameters); if (rsa.KeySize < 3072 || rsa.KeySize > 4096 || rsa.ToXmlString(false) != (string)j["publicKey"]) throw new InvalidOperationException("Keystore public key mismatch."); return rsa; } catch { rsa.Dispose(); throw; }
            } finally { Array.Clear(key, 0, key.Length); if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length); }
        }
        public static JObject CreateEvm(string filename) {
            var key = EthECKey.GenerateKey(); byte[] bytes = key.GetPrivateKeyAsBytes();
            try {
                var parameters = new Nethereum.KeyStore.Model.ScryptParams { N = 131072, R = 8, P = 1, Dklen = 32 };
                WritePrivate(filename, new KeyStoreScryptService().EncryptAndGenerateKeyStoreAsJson(Password("BRIDGE_KEYSTORE_PASSWORD", true), bytes, key.GetPublicAddress(), parameters));
                return new JObject { ["address"] = key.GetPublicAddress() };
            } finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public static EthECKey LoadEvm(string filename) {
            RequirePrivate(filename); byte[] bytes = new KeyStoreService().DecryptKeyStoreFromJson(Password("BRIDGE_KEYSTORE_PASSWORD", false), File.ReadAllText(filename));
            try { return new EthECKey(bytes, true); } finally { Array.Clear(bytes, 0, bytes.Length); }
        }
    }
}
