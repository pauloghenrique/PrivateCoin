using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Povix.WalletBridge
{
    /// <summary>A bounded loopback endpoint; it never listens on external interfaces or calls remote services.</summary>
    public sealed class LoopbackWalletServer : IDisposable
    {
        public const int DefaultPort = 4781;
        private const int MaximumRequestBytes = 8100000;
        private readonly string origin;
        private readonly string host;
        private readonly TcpListener listener;
        private readonly SemaphoreSlim workers = new SemaphoreSlim(2);
        private readonly Func<byte[], string, object> convert;
        private bool stopped;
        public string AllowedOrigin => origin;

        public LoopbackWalletServer(string allowedOrigin, int port = DefaultPort, Func<byte[], string, object> converter = null)
        {
            Uri uri;
            if (!Uri.TryCreate(allowedOrigin, UriKind.Absolute, out uri) || (uri.Scheme != "https" && (uri.Scheme != "http" || !uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Informe o endereço HTTPS do DEX (ou localhost para desenvolvimento).");
            origin = uri.GetLeftPart(UriPartial.Authority);
            host = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            listener = new TcpListener(IPAddress.Loopback, port);
            convert = converter ?? WalletFileConverter.ConvertFile;
        }

        public void Start()
        {
            listener.Start();
            Task.Run(() => AcceptLoop());
        }

        private async Task AcceptLoop()
        {
            while (!stopped)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is SocketException || error is ObjectDisposedException) { return; }
                if (!workers.Wait(0)) { client.Dispose(); continue; }
                Queue(client);
            }
        }

        private void Queue(TcpClient client)
        {
            Task.Run(() => { try { Handle(client); } finally { workers.Release(); } });
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 10000; client.SendTimeout = 10000;
                NetworkStream stream = client.GetStream();
                bool authorized = false;
                try
                {
                    string[] lines = ReadHeader(stream).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] request = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
                    {
                        int separator = lines[i].IndexOf(':');
                        if (separator <= 0) throw new ArgumentException();
                        headers.Add(lines[i].Substring(0, separator), lines[i].Substring(separator + 1).Trim());
                    }
                    string suppliedOrigin, suppliedHost;
                    if (!headers.TryGetValue("Origin", out suppliedOrigin) || suppliedOrigin != origin ||
                        !headers.TryGetValue("Host", out suppliedHost) || suppliedHost != host)
                    { Respond(stream, 403, new { error = "Este site não está autorizado no auxiliar local." }, false); return; }
                    authorized = true;
                    if (request.Length != 3 || request[1] != "/import" || (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0"))
                    { Respond(stream, 404, new { error = "Operação local não encontrada." }, true); return; }
                    if (request[0] == "OPTIONS") { Respond(stream, 200, new { ready = true }, true); return; }
                    string lengthHeader, contentType;
                    int length;
                    if (request[0] != "POST" || headers.ContainsKey("Transfer-Encoding") ||
                        !headers.TryGetValue("Content-Length", out lengthHeader) || !int.TryParse(lengthHeader, NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                        length < 1 || length > MaximumRequestBytes || !headers.TryGetValue("Content-Type", out contentType) ||
                        (contentType != "application/json" && !contentType.StartsWith("application/json;", StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException();
                    byte[] body = new byte[length];
                    try
                    {
                        int offset = 0;
                        while (offset < body.Length)
                        {
                            int count = stream.Read(body, offset, body.Length - offset);
                            if (count == 0) throw new IOException();
                            offset += count;
                        }
                        var input = new JavaScriptSerializer { MaxJsonLength = MaximumRequestBytes, RecursionLimit = 4 }
                            .Deserialize<ImportRequest>(Encoding.UTF8.GetString(body));
                        if (input == null) throw new ArgumentException();
                        byte[] file = System.Convert.FromBase64String(input?.file ?? string.Empty);
                        try { Respond(stream, 200, new { wallet = convert(file, input.password) }, true); }
                        finally { Array.Clear(file, 0, file.Length); }
                    }
                    finally { Array.Clear(body, 0, body.Length); }
                }
                catch (Exception error) when (error is ArgumentException || error is FormatException || error is CryptographicException || error is InvalidOperationException)
                {
                    Respond(stream, 400, new { error = "Não foi possível abrir as carteiras. Use o mesmo usuário Windows que criou o arquivo e confira o arquivo e a senha da cópia local." }, authorized);
                }
                catch (IOException) { }
            }
        }

        private static string ReadHeader(Stream stream)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 8192)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new IOException();
                bytes.Add((byte)value);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                    return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new ArgumentException();
        }

        private void Respond(Stream stream, int status, object value, bool authorized)
        {
            byte[] body = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = MaximumRequestBytes }.Serialize(value));
            string headers = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Error") + "\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n";
            if (authorized) headers += "Access-Control-Allow-Origin: " + origin + "\r\nVary: Origin\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Private-Network: true\r\n";
            byte[] head = Encoding.ASCII.GetBytes(headers + "\r\n");
            try { stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length); }
            catch (IOException) { }
        }

        public void Dispose() { stopped = true; listener.Stop(); }
        private sealed class ImportRequest { public string file { get; set; } public string password { get; set; } }
    }
}
