using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Povix.WalletBridge;
using PrivateCoin.Core;

internal static class PovixWalletFileRegression
{
    public static int Main(string[] args)
    {
        try
        {
            string directory = args[0];
            Directory.CreateDirectory(directory);
            var json = new JavaScriptSerializer();
            using (var first = new Wallet()) using (var second = new Wallet())
            {
                string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
                string desktopAssembly = args.Length > 2 ? args[2] : "PrivateCoin.Desktop/bin/Release/PrivateCoin.Desktop.exe";
                byte[] clear = SerializeDesktop(desktopAssembly, first, second, false);
                // No real wallet file is read. DPAPI is replaced at its boundary because tests run on Linux.
                byte[] desktopFile = new byte[] { 1, 0, 0, 0, 208, 140, 157, 223, 1, 21, 209, 17, 140, 122, 0, 192, 79, 194, 151, 235 };
                byte[] original = (byte[])desktopFile.Clone();
                object converted = WalletFileConverter.ConvertFile(desktopFile, "local-test-password", bytes => (byte[])clear.Clone());
                Check(desktopFile.SequenceEqual(original), "original Desktop file bytes are unchanged");
                Check(!json.Serialize(converted).Contains("RSAKeyValue"), "native helper returns only an encrypted collection");
                bool rejected = false;
                try { WalletFileConverter.ConvertFile(desktopFile, "short", bytes => (byte[])clear.Clone()); }
                catch (WalletImportException error) { rejected = error.Code == "copy_password"; }
                Check(rejected, "copy password is validated before opening private data");
                rejected = false;
                try { WalletFileConverter.ConvertFile(desktopFile, "local-test-password", bytes => { throw new CryptographicException(); }); }
                catch (WalletImportException error) { rejected = error.Code == "windows_protection"; }
                Check(rejected, "Windows DPAPI failure has its own code without fallback to plaintext");
                WalletFileSummary summary = WalletFileConverter.InspectFile(desktopFile, bytes => (byte[])clear.Clone());
                Check(summary.WalletCount == 2 && summary.AddressCount == 2, "the exact Desktop DataContract serializer and private metadata are accepted");
                byte[] legacy = SerializeDesktop(desktopAssembly, first, second, true);
                try { Check(WalletFileConverter.InspectFile(desktopFile, bytes => (byte[])legacy.Clone()).WalletCount == 2, "the legacy Desktop collection with blockchain data remains accepted"); }
                finally { Array.Clear(legacy, 0, legacy.Length); }
                byte[] bom = Encoding.UTF8.GetPreamble().Concat(clear).ToArray();
                try { Check(WalletFileConverter.InspectFile(desktopFile, bytes => (byte[])bom.Clone()).WalletCount == 2, "a UTF-8 BOM in the opened Desktop payload is accepted"); }
                finally { Array.Clear(bom, 0, bom.Length); }
                rejected = false;
                try { WalletFileConverter.InspectFile(desktopFile, bytes => Encoding.UTF8.GetBytes("{private-payload-that-must-not-leak")); }
                catch (WalletImportException error) { rejected = error.Code == "wallet_format" && !error.Message.Contains("private-payload"); }
                Check(rejected, "format failures are distinguished from DPAPI without disclosing payload data");
                var socket = new TcpListener(IPAddress.Loopback, 0);
                socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
                using (var server = new LoopbackWalletServer("https://dex.example", port,
                    (bytes, password) => WalletFileConverter.ConvertFile(bytes, password, value => {
                        if (value.Length == 21 && value[20] == 1) throw new CryptographicException("private-material-that-must-not-leak");
                        if (value.Length == 21 && value[20] == 2) return Encoding.UTF8.GetBytes("{private-json-that-must-not-leak");
                        return (byte[])clear.Clone();
                    })))
                {
                    server.Start();
                    File.WriteAllText(Path.Combine(directory, "wallet-files.json"), json.Serialize(new {
                        firstAddress, secondAddress, portable = converted,
                        desktopFile = Convert.ToBase64String(desktopFile),
                        helperUrl = "http://127.0.0.1:" + port + "/import", helperOrigin = server.AllowedOrigin }));
                    var start = new ProcessStartInfo(args[1]) { UseShellExecute = false,
                        Arguments = "Tests/PovixWalletFileRegression.js " + directory };
                    using (var process = Process.Start(start)) { process.WaitForExit(); Check(process.ExitCode == 0, "file reading and wallet selection regression"); }
                }
                Array.Clear(clear, 0, clear.Length);
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static byte[] SerializeDesktop(string assemblyPath, Wallet first, Wallet second, bool legacy)
    {
        // Invoke the actual Desktop serializer and models; do not recreate its JSON in the test.
        Type store = Assembly.LoadFrom(assemblyPath).GetType("PrivateCoin.Desktop.WalletStore", true);
        Type walletType = store.GetNestedType("StoredWallet", BindingFlags.NonPublic);
        Type collectionType = store.GetNestedType(legacy ? "LegacyStoredState" : "StoredWalletCollection", BindingFlags.NonPublic);
        object collection = Activator.CreateInstance(collectionType, true);
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(walletType));
        Wallet[] wallets = { first, second };
        for (int i = 0; i < wallets.Length; i++)
        {
            object item = Activator.CreateInstance(walletType, true);
            walletType.GetProperty("Name").SetValue(item, i == 0 ? "Carteira principal" : "Carteira de tokens", null);
            walletType.GetProperty("PrivateKeys").SetValue(item, wallets[i].ExportPrivateKeys().ToList(), null);
            walletType.GetProperty("Addresses").SetValue(item, wallets[i].OwnedOneTimeAddresses.ToList(), null);
            walletType.GetProperty("LockedStake").SetValue(item, Blockchain.OneCoin, null);
            walletType.GetProperty("ValidatorRewardAddress").SetValue(item, wallets[i].OwnedOneTimeAddresses.First(), null);
            walletType.GetProperty("RecoveryPhrase").SetValue(item, "Test-only private recovery metadata", null);
            walletType.GetProperty("RecoveryVersion").SetValue(item, 1, null);
            list.Add(item);
        }
        collectionType.GetProperty("Wallets").SetValue(collection, list, null);
        if (legacy) collectionType.GetProperty("Blocks").SetValue(collection, new Blockchain().Blocks.ToList(), null);
        return (byte[])store.GetMethod("Serialize", BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(collectionType).Invoke(null, new[] { collection });
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
