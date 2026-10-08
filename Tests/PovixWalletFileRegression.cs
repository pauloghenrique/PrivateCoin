using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
                byte[] clear = Encoding.UTF8.GetBytes(json.Serialize(new { Wallets = new[] {
                    new { Name = "Carteira principal", PrivateKeys = first.ExportPrivateKeys() },
                    new { Name = "Carteira de tokens", PrivateKeys = second.ExportPrivateKeys() } } }));
                // No real wallet file is read. DPAPI is replaced at its boundary because tests run on Linux.
                byte[] desktopFile = new byte[] { 1, 0, 0, 0, 208, 140, 157, 223, 1, 21, 209, 17, 140, 122, 0, 192, 79, 194, 151, 235 };
                byte[] original = (byte[])desktopFile.Clone();
                object converted = WalletFileConverter.ConvertFile(desktopFile, "local-test-password", bytes => (byte[])clear.Clone());
                Check(desktopFile.SequenceEqual(original), "original Desktop file bytes are unchanged");
                Check(!json.Serialize(converted).Contains("RSAKeyValue"), "native helper returns only an encrypted collection");
                bool rejected = false;
                try { WalletFileConverter.ConvertFile(desktopFile, "short", bytes => (byte[])clear.Clone()); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "copy password is validated before opening private data");
                rejected = false;
                try { WalletFileConverter.ConvertFile(desktopFile, "local-test-password", bytes => { throw new CryptographicException(); }); }
                catch (CryptographicException) { rejected = true; }
                Check(rejected, "Windows DPAPI failure is propagated without fallback to plaintext");
                var socket = new TcpListener(IPAddress.Loopback, 0);
                socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
                using (var server = new LoopbackWalletServer("https://dex.example", port,
                    (bytes, password) => WalletFileConverter.ConvertFile(bytes, password, value => (byte[])clear.Clone())))
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
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
