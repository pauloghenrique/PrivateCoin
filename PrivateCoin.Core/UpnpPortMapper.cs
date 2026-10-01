using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PrivateCoin.Core
{
    /// <summary>Creates a temporary TCP port mapping on UPnP IGD compatible routers.</summary>
    internal sealed class UpnpPortMapper : IDisposable
    {
        private const int LeaseSeconds = 3600;
        private readonly int port;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Uri controlUri;
        private string serviceType;
        private string localAddress;
        private Task renewalTask;

        public UpnpPortMapper(int port)
        {
            this.port = port;
        }

        public async Task<bool> TryStartAsync(CancellationToken token)
        {
            string descriptionLocation = await DiscoverGatewayAsync(token).ConfigureAwait(false);
            if (descriptionLocation == null) return false;

            using (var client = CreateHttpClient())
            {
                string description = await client.GetStringAsync(descriptionLocation).ConfigureAwait(false);
                XDocument document = XDocument.Parse(description);
                XElement service = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName == "service" && element.Elements().Any(child =>
                        child.Name.LocalName == "serviceType" &&
                        (child.Value.Contains("WANIPConnection") || child.Value.Contains("WANPPPConnection"))));
                if (service == null) return false;

                serviceType = service.Elements().First(element => element.Name.LocalName == "serviceType").Value;
                string controlPath = service.Elements().First(element => element.Name.LocalName == "controlURL").Value;
                controlUri = new Uri(new Uri(descriptionLocation), controlPath);
                localAddress = FindLocalAddress(controlUri.Host);
                if (localAddress == null) return false;

                await AddMappingAsync(client, token).ConfigureAwait(false);
            }

            renewalTask = Task.Run(() => RenewalLoop(cancellation.Token));
            return true;
        }

        private async Task RenewalLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(30), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    using (var client = CreateHttpClient())
                        await AddMappingAsync(client, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException || error is IOException || error is SocketException || error is OperationCanceledException)
                {
                    if (token.IsCancellationRequested) return;
                }
            }
        }

        private Task AddMappingAsync(HttpClient client, CancellationToken token)
        {
            string arguments =
                "<NewRemoteHost></NewRemoteHost>" +
                "<NewExternalPort>" + port + "</NewExternalPort>" +
                "<NewProtocol>TCP</NewProtocol>" +
                "<NewInternalPort>" + port + "</NewInternalPort>" +
                "<NewInternalClient>" + SecurityElementEscape(localAddress) + "</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>PrivateCoin P2P</NewPortMappingDescription>" +
                "<NewLeaseDuration>" + LeaseSeconds + "</NewLeaseDuration>";
            return SendSoapAsync(client, "AddPortMapping", arguments, token);
        }

        private Task DeleteMappingAsync(HttpClient client, CancellationToken token)
        {
            string arguments =
                "<NewRemoteHost></NewRemoteHost>" +
                "<NewExternalPort>" + port + "</NewExternalPort>" +
                "<NewProtocol>TCP</NewProtocol>";
            return SendSoapAsync(client, "DeletePortMapping", arguments, token);
        }

        private async Task SendSoapAsync(HttpClient client, string action, string arguments, CancellationToken token)
        {
            string body = "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:" + action + " xmlns:u=\"" + serviceType + "\">" + arguments +
                "</u:" + action + "></s:Body></s:Envelope>";
            using (var request = new HttpRequestMessage(HttpMethod.Post, controlUri))
            {
                request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + serviceType + "#" + action + "\"");
                request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
                using (HttpResponseMessage response = await client.SendAsync(request, token).ConfigureAwait(false))
                    response.EnsureSuccessStatusCode();
            }
        }

        private static async Task<string> DiscoverGatewayAsync(CancellationToken token)
        {
            const string request = "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
            byte[] payload = Encoding.ASCII.GetBytes(request);
            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            using (token.Register(udp.Close))
            {
                await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);
                Task<UdpReceiveResult> receive = udp.ReceiveAsync();
                Task completed = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(3), token)).ConfigureAwait(false);
                if (completed == receive)
                    return ParseLocation(Encoding.ASCII.GetString(receive.Result.Buffer));
            }
            return null;
        }

        private static string ParseLocation(string response)
        {
            foreach (string line in response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf(':');
                if (separator > 0 && string.Equals(line.Substring(0, separator).Trim(), "LOCATION", StringComparison.OrdinalIgnoreCase))
                {
                    Uri location;
                    string value = line.Substring(separator + 1).Trim();
                    return Uri.TryCreate(value, UriKind.Absolute, out location) ? location.AbsoluteUri : null;
                }
            }
            return null;
        }

        private static string FindLocalAddress(string gatewayHost)
        {
            IPAddress gateway;
            if (!IPAddress.TryParse(gatewayHost, out gateway)) return null;
            using (var socket = new Socket(gateway.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Connect(new IPEndPoint(gateway, 1900));
                var endpoint = socket.LocalEndPoint as IPEndPoint;
                return endpoint == null ? null : endpoint.Address.ToString();
            }
        }

        private static HttpClient CreateHttpClient()
        {
            return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        private static string SecurityElementEscape(string value)
        {
            return System.Security.SecurityElement.Escape(value);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            if (renewalTask != null)
            {
                try { renewalTask.Wait(TimeSpan.FromSeconds(1)); }
                catch (AggregateException) { }
            }
            if (controlUri != null)
            {
                try
                {
                    using (var client = CreateHttpClient())
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        DeleteMappingAsync(client, timeout.Token).GetAwaiter().GetResult();
                }
                catch (Exception error) when (error is HttpRequestException || error is IOException || error is SocketException || error is OperationCanceledException) { }
            }
            cancellation.Dispose();
        }
    }
}
