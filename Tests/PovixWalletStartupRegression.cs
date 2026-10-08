using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using System.Security.Cryptography;
using Povix.WalletBridge;

internal static class PovixWalletStartupRegression
{
    private const string Origin = "https://dex.example";

    public static int Main()
    {
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var prompted = new StartupReader(true);
            Check(Program.Run(new string[0], prompted, output, error, false) == 0 && prompted.Listening,
                "starting without arguments asks for the DEX origin and starts the loopback helper");
            Check(output.ToString().Contains("Endereço do DEX:") && error.ToString().Length == 0,
                "the prompt is visible without an argument error");

            var specified = new StartupReader(false);
            Check(Program.Run(new[] { "--origin", Origin }, specified, new StringWriter(), new StringWriter(), false) == 0 && specified.Listening,
                "the existing --origin launch starts the helper and releases the port on exit");

            error = new StringWriter();
            Check(Program.Run(new[] { "--origin", "http://remote.example" }, new StringReader(""), new StringWriter(), error, false) == 1 &&
                error.ToString().Contains("Endereço inválido"), "insecure remote origins remain rejected at startup");
            error = new StringWriter();
            Check(Program.Run(new[] { "--unknown" }, new StringReader(""), new StringWriter(), error, false) == 1 &&
                error.ToString().Contains("Uso:"), "invalid arguments report how to start the helper");

            var occupied = new TcpListener(IPAddress.Loopback, LoopbackWalletServer.DefaultPort);
            occupied.Start();
            try
            {
                var waiting = new CountingReader();
                output = new StringWriter(); error = new StringWriter();
                Check(Program.Run(new[] { "--origin", Origin }, waiting, output, error, true) == 1 &&
                    error.ToString().Contains("já está em uso") && waiting.Reads == 1 && output.ToString().Contains("Pressione Enter para fechar"),
                    "an occupied port shows a specific error and waits before closing the window");
            }
            finally { occupied.Stop(); }
            CheckDiagnostics();
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }

    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }

    private static void CheckDiagnostics()
    {
        string directory = Path.Combine("work", "wallet-diagnostic-regression");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "wallets.dat");
        byte[] original = new byte[20]; original[0] = 1;
        File.WriteAllBytes(path, original);
        var output = new StringWriter(); var error = new StringWriter();
        byte[] inspected = null;
        int result = Program.CheckWallet(path, output, error, bytes => {
            inspected = bytes;
            return WalletFileConverter.InspectFile(bytes, data => Encoding.UTF8.GetBytes("{\"Wallets\":[{\"Name\":\"Private name\",\"PrivateKeys\":[\"private-key-secret\"]}]}"));
        });
        Check(result == 0 && output.ToString().Contains("1 carteira(s), 1 endereço(s)") && !output.ToString().Contains("Private name") && !output.ToString().Contains("private-key-secret"),
            "local diagnosis returns only wallet/address counts, without private material");
        Check(File.ReadAllBytes(path).SequenceEqual(original) && inspected.All(value => value == 0),
            "local diagnosis leaves the selected file unchanged and clears its read buffer");
        error = new StringWriter();
        result = Program.CheckWallet(path, new StringWriter(), error, bytes => WalletFileConverter.InspectFile(bytes, data => { throw new CryptographicException("private-exception-detail"); }));
        Check(result == 1 && error.ToString().Contains("Código: windows_protection") && !error.ToString().Contains("private-exception-detail"),
            "local Windows-protection diagnostics are specific and redact exception contents");
        error = new StringWriter();
        Check(Program.CheckWallet(Path.Combine(directory, "missing.dat"), new StringWriter(), error) == 1 && error.ToString().Contains("file_access"),
            "local diagnosis distinguishes a missing file from decryption failure");
    }

    private sealed class CountingReader : TextReader
    {
        public int Reads { get; private set; }
        public override string ReadLine() { Reads++; return ""; }
    }

    private sealed class StartupReader : TextReader
    {
        private bool prompt;
        public bool Listening { get; private set; }
        public StartupReader(bool showPrompt) { prompt = showPrompt; }
        public override string ReadLine()
        {
            if (prompt) { prompt = false; return " " + Origin + " "; }
            using (var client = new TcpClient())
            {
                client.ReceiveTimeout = 5000;
                client.Connect(IPAddress.Loopback, LoopbackWalletServer.DefaultPort);
                using (var stream = client.GetStream())
                {
                    byte[] request = Encoding.ASCII.GetBytes("OPTIONS /import HTTP/1.1\r\nHost: 127.0.0.1:" +
                        LoopbackWalletServer.DefaultPort + "\r\nOrigin: " + Origin + "\r\nConnection: close\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    using (var reader = new StreamReader(stream))
                    {
                        string response = reader.ReadToEnd();
                        Listening = response.StartsWith("HTTP/1.1 200") && response.Contains("Access-Control-Allow-Origin: " + Origin + "\r\n");
                    }
                }
            }
            return "";
        }
    }
}
