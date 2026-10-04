using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PrivateCoin.Core
{
    /// <summary>Creates a temporary TCP port mapping using UPnP IGD or NAT-PMP.</summary>
    internal sealed class UpnpPortMapper : IDisposable
    {
        private const int LeaseSeconds = 3600;
        private readonly int port;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Uri controlUri;
        private string serviceType;
        private string localAddress;
        private Task renewalTask;
        private IPAddress natPmpGateway;

        public string Protocol { get; private set; }

        public UpnpPortMapper(int port)
        {
            this.port = port;
        }

        public async Task<bool> TryStartAsync(CancellationToken token)
        {
            try
            {
                if (await TryStartUpnpAsync(token).ConfigureAwait(false))
                {
                    Protocol = "UPnP";
                    renewalTask = Task.Run(() => RenewalLoop(cancellation.Token));
                    return true;
                }
            }
            catch (Exception error) when (error is HttpRequestException || error is IOException ||
                error is SocketException || error is InvalidOperationException || error is System.Xml.XmlException)
            {
                // A router may advertise an incomplete UPnP implementation. NAT-PMP
                // is an independent fallback and should still be attempted.
            }

            natPmpGateway = FindDefaultGateway();
            if (natPmpGateway == null || !await SetNatPmpMappingAsync(LeaseSeconds, token).ConfigureAwait(false)) return false;
            Protocol = "NAT-PMP";
            renewalTask = Task.Run(() => RenewalLoop(cancellation.Token));
            return true;
        }

        private async Task<bool> TryStartUpnpAsync(CancellationToken token)
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
                    if (natPmpGateway != null)
                        await SetNatPmpMappingAsync(LeaseSeconds, token).ConfigureAwait(false);
                    else
                        using (var client = CreateHttpClient())
                            await AddMappingAsync(client, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException || error is IOException || error is SocketException || error is OperationCanceledException)
                {
                    if (token.IsCancellationRequested) return;
                }
            }
        }

        private async Task<bool> SetNatPmpMappingAsync(int lifetime, CancellationToken token)
        {
            // RFC 6886: version 0, opcode 2 (TCP), reserved, internal port,
            // requested external port and lifetime, all integers in network order.
            var request = new byte[12];
            request[1] = 2;
            WriteUInt16(request, 4, (ushort)port);
            WriteUInt16(request, 6, (ushort)port);
            WriteUInt32(request, 8, (uint)lifetime);

            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            using (token.Register(udp.Close))
            {
                await udp.SendAsync(request, request.Length, new IPEndPoint(natPmpGateway, 5351)).ConfigureAwait(false);
                Task<UdpReceiveResult> receive = udp.ReceiveAsync();
                Task completed = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(3), token)).ConfigureAwait(false);
                if (completed != receive) return false;
                byte[] response = receive.Result.Buffer;
                return response.Length >= 16 && response[0] == 0 && response[1] == 130 &&
                    ReadUInt16(response, 2) == 0 && ReadUInt16(response, 8) == port;
            }
        }

        private static IPAddress FindDefaultGateway()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                        network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(network => network.GetIPProperties().GatewayAddresses)
                    .Select(gateway => gateway.Address)
                    .FirstOrDefault(address => address != null && address.AddressFamily == AddressFamily.InterNetwork &&
                        !address.Equals(IPAddress.Any));
            }
            catch (NetworkInformationException)
            {
                return null;
            }
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static int ReadUInt16(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
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
                "<NewPortMappingDescription>POVIX P2P</NewPortMappingDescription>" +
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
            if (Protocol == "UPnP" && controlUri != null)
            {
                try
                {
                    using (var client = CreateHttpClient())
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        DeleteMappingAsync(client, timeout.Token).GetAwaiter().GetResult();
                }
                catch (Exception error) when (error is HttpRequestException || error is IOException || error is SocketException || error is OperationCanceledException) { }
            }
            else if (Protocol == "NAT-PMP" && natPmpGateway != null)
            {
                try
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        SetNatPmpMappingAsync(0, timeout.Token).GetAwaiter().GetResult();
                }
                catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException) { }
            }
            cancellation.Dispose();
        }
    }
}
