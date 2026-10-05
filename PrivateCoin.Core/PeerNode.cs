using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PrivateCoin.Core
{
    [DataContract]
    internal sealed class PeerMessage
    {
        [DataMember(Order = 1)] public string MessageId { get; set; }
        [DataMember(Order = 2)] public string Type { get; set; }
        [DataMember(Order = 3)] public Transaction Transaction { get; set; }
        [DataMember(Order = 4)] public Block[] Blocks { get; set; }
        [DataMember(Order = 5)] public int ListeningPort { get; set; }
        [DataMember(Order = 6)] public string[] Peers { get; set; }
        [DataMember(Order = 7)] public string NetworkId { get; set; }
        [DataMember(Order = 8)] public int ConsensusVersion { get; set; }
    }

    /// <summary>A TCP gossip node with Bitcoin-style bootstrap seeds and peer address exchange.</summary>
    public sealed class PeerNode : IDisposable
    {
        private const int MaximumConnections = 8;
        private readonly int listeningPort;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<TcpClient, SemaphoreSlim> peers = new ConcurrentDictionary<TcpClient, SemaphoreSlim>();
        private readonly ConcurrentDictionary<TcpClient, string> peerEndpoints = new ConcurrentDictionary<TcpClient, string>();
        private readonly ConcurrentDictionary<string, byte> connectedEndpoints = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> knownEndpoints = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> bootstrapEndpoints = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> connectingEndpoints = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> prioritizedEndpoints = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> recentlyLearnedEndpoints = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<string, byte> seen = new ConcurrentDictionary<string, byte>();
        private readonly UpnpPortMapper portMapper;
        private readonly PeerAddressBook addressBook;
        private Task acceptTask;
        private Task maintenanceTask;
        private string natTraversalStatus = "aguardando";

        public PeerNode(int port) : this(port, false, null)
        {
        }

        public PeerNode(int port, bool enableNatTraversal) : this(port, enableNatTraversal, null)
        {
        }

        public PeerNode(int port, bool enableNatTraversal, string peerCachePath)
        {
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException(nameof(port));
            listeningPort = port;
            listener = CreateListener(port);
            if (enableNatTraversal) portMapper = new UpnpPortMapper(port);
            addressBook = new PeerAddressBook(peerCachePath);
        }

        public event EventHandler<TransactionReceivedEventArgs> TransactionReceived;
        public event EventHandler<ChainReceivedEventArgs> ChainReceived;
        public event EventHandler SynchronizationRequested;
        public event EventHandler PeerCountChanged;
        public event EventHandler NatTraversalStatusChanged;

        public int ConnectedPeerCount => peers.Count;
        public string[] ConnectedPeers => peerEndpoints.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        public string[] KnownPeers => knownEndpoints.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        public string NatTraversalStatus => natTraversalStatus;

        public void Start()
        {
            Start(Enumerable.Empty<string>());
        }

        public void Start(IEnumerable<string> bootstrapPeers)
        {
            if (acceptTask != null) throw new InvalidOperationException("The node is already running.");
            if (bootstrapPeers == null) throw new ArgumentNullException(nameof(bootstrapPeers));
            foreach (string endpoint in addressBook.Load()) AddKnownEndpoint(endpoint);
            foreach (string endpoint in bootstrapPeers)
            {
                string normalized;
                if (TryNormalizeEndpoint(endpoint, out normalized))
                {
                    bootstrapEndpoints.TryAdd(normalized, 0);
                    knownEndpoints.TryAdd(normalized, 0);
                }
            }
            listener.Start();
            acceptTask = Task.Run(() => AcceptLoop(cancellation.Token));
            maintenanceTask = Task.Run(() => MaintenanceLoop(cancellation.Token));
            if (portMapper == null) SetNatTraversalStatus("desativado");
            else Task.Run(() => StartNatTraversal(cancellation.Token));
        }

        private async Task StartNatTraversal(CancellationToken token)
        {
            try
            {
                bool mapped = await portMapper.TryStartAsync(token).ConfigureAwait(false);
                SetNatTraversalStatus(mapped ? portMapper.Protocol + " ativo" : "UPnP/NAT-PMP indisponível");
            }
            catch (Exception error) when (error is HttpRequestException || error is IOException || error is SocketException || error is InvalidOperationException || error is System.Xml.XmlException || error is OperationCanceledException)
            {
                if (!token.IsCancellationRequested) SetNatTraversalStatus("UPnP/NAT-PMP indisponível");
            }
        }

        private void SetNatTraversalStatus(string status)
        {
            natTraversalStatus = status;
            NatTraversalStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task ConnectAsync(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("A peer host is required.", nameof(host));
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException(nameof(port));
            string endpoint = FormatEndpoint(host.Trim(), port);
            knownEndpoints.TryAdd(endpoint, 0);
            return ConnectKnownPeer(endpoint, true);
        }

        public Task BroadcastAsync(Transaction transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            return Broadcast(CreateMessage("transaction", transaction: transaction));
        }

        public Task BroadcastChainAsync(IEnumerable<Block> blocks)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            return Broadcast(CreateMessage("chain", blocks: blocks.ToArray()));
        }

        public Task RequestSynchronizationAsync()
        {
            return Broadcast(CreateMessage("sync-request"));
        }

        private Task Broadcast(PeerMessage message)
        {
            seen.TryAdd(message.MessageId, 0);
            return SendToAll(message, null);
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { Register(await listener.AcceptTcpClientAsync().ConfigureAwait(false), null); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (token.IsCancellationRequested) return; }
            }
        }

        private async Task MaintenanceLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // Always try configured bootstrap nodes first. A peers.dat full of
                // stale addresses must not push a newly configured, reachable seed
                // past the candidate limit and prevent the node from joining.
                int availableConnections = Math.Max(0, MaximumConnections - peers.Count - connectingEndpoints.Count);
                var recentlyLearned = new List<string>();
                string learnedEndpoint;
                while (availableConnections > 0 && recentlyLearned.Count < MaximumConnections * 4 &&
                    recentlyLearnedEndpoints.TryDequeue(out learnedEndpoint))
                    recentlyLearned.Add(learnedEndpoint);

                string[] candidates = bootstrapEndpoints.Keys
                    // Addresses received from a connected peer must be attempted
                    // before the persistent cache. Otherwise stale peers.dat entries
                    // can indefinitely hide the live addresses just discovered.
                    .Concat(recentlyLearned)
                    .Concat(addressBook.Select(MaximumConnections * 4))
                    .Concat(knownEndpoints.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(endpoint => !connectedEndpoints.ContainsKey(endpoint) && !connectingEndpoints.ContainsKey(endpoint))
                    .Take(availableConnections)
                    .ToArray();
                var selected = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
                foreach (string deferredEndpoint in recentlyLearned.Where(endpoint => !selected.Contains(endpoint)))
                    recentlyLearnedEndpoints.Enqueue(deferredEndpoint);
                await Task.WhenAll(candidates.Select(endpoint => ConnectKnownPeer(endpoint, false))).ConfigureAwait(false);

                if (peers.Count > 0)
                {
                    var addressMessage = CreateAddressMessage("addr");
                    seen.TryAdd(addressMessage.MessageId, 0);
                    await SendToAll(addressMessage, null).ConfigureAwait(false);
                }

                TrySaveAddressBook();

                // Retry bootstrap promptly while isolated, then reduce background
                // traffic after at least one working connection has been found.
                TimeSpan delay = peers.IsEmpty ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task ConnectKnownPeer(string endpoint, bool throwOnFailure)
        {
            string host;
            int port;
            if (!TryParseEndpoint(endpoint, out host, out port))
            {
                if (throwOnFailure) throw new FormatException("Invalid peer endpoint: " + endpoint);
                return;
            }
            if (IsSelf(host, port) || connectedEndpoints.ContainsKey(endpoint) || !connectingEndpoints.TryAdd(endpoint, 0)) return;

            TcpClient client = null;
            addressBook.Attempted(endpoint);
            try
            {
                client = await OpenConnectionAsync(host, port).ConfigureAwait(false);
                if (!connectedEndpoints.TryAdd(endpoint, 0)) { client.Dispose(); return; }
                addressBook.Succeeded(endpoint);
                Register(client, endpoint);
            }
            catch
            {
                addressBook.Failed(endpoint);
                if (client != null) client.Dispose();
                if (throwOnFailure) throw;
            }
            finally { connectingEndpoints.TryRemove(endpoint, out _); }
        }

        private void Register(TcpClient client, string endpoint)
        {
            client.NoDelay = true;
            peers.TryAdd(client, new SemaphoreSlim(1, 1));
            if (!string.IsNullOrEmpty(endpoint)) peerEndpoints.TryAdd(client, endpoint);
            Task.Run(() => ReadLoop(client, cancellation.Token));
            Task.Run(async () =>
            {
                await SendToPeer(client, CreateAddressMessage("hello")).ConfigureAwait(false);
                await SendToPeer(client, CreateMessage("sync-request")).ConfigureAwait(false);
            });
            PeerCountChanged?.Invoke(this, EventArgs.Empty);
        }

        private PeerMessage CreateAddressMessage(string type)
        {
            PeerMessage message = CreateMessage(type);
            message.ListeningPort = listeningPort;
            message.Peers = knownEndpoints.Keys.Take(256).ToArray();
            return message;
        }

        private static PeerMessage CreateMessage(string type, Transaction transaction = null, Block[] blocks = null)
        {
            return new PeerMessage
            {
                MessageId = Crypto.NewId(),
                Type = type,
                Transaction = transaction,
                Blocks = blocks,
                NetworkId = Blockchain.NetworkId,
                ConsensusVersion = Blockchain.ConsensusVersion
            };
        }

        private async Task ReadLoop(TcpClient client, CancellationToken token)
        {
            try
            {
                var stream = client.GetStream();
                while (!token.IsCancellationRequested)
                {
                    byte[] lengthBytes = await ReadExactly(stream, 4, token).ConfigureAwait(false);
                    int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes, 0));
                    if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Invalid peer message size.");
                    byte[] body = await ReadExactly(stream, length, token).ConfigureAwait(false);
                    PeerMessage message;
                    using (var memory = new MemoryStream(body))
                        message = (PeerMessage)new DataContractJsonSerializer(typeof(PeerMessage)).ReadObject(memory);
                    if (message == null || message.NetworkId != Blockchain.NetworkId ||
                        message.ConsensusVersion != Blockchain.ConsensusVersion)
                        throw new InvalidDataException("Peer belongs to an incompatible network or consensus version.");
                    if (message == null || string.IsNullOrEmpty(message.MessageId) || !seen.TryAdd(message.MessageId, 0)) continue;

                    if (message.Type == "hello" || message.Type == "addr") LearnAddresses(client, message);
                    if (message.Type == "hello") await SendToPeer(client, CreateAddressMessage("addr")).ConfigureAwait(false);
                    else if (message.Type == "transaction" && message.Transaction != null)
                        TransactionReceived?.Invoke(this, new TransactionReceivedEventArgs(message.Transaction));
                    else if (message.Type == "chain" && message.Blocks != null && message.Blocks.Length > 0)
                        ChainReceived?.Invoke(this, new ChainReceivedEventArgs(message.Blocks));
                    else if (message.Type == "sync-request")
                        SynchronizationRequested?.Invoke(this, EventArgs.Empty);

                    if (message.Type != "hello") await SendToAll(message, client).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException || error is SerializationException) { }
            finally { RemovePeer(client); }
        }

        private void LearnAddresses(TcpClient client, PeerMessage message)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote != null && message.ListeningPort > 0 && message.ListeningPort <= 65535)
            {
                IPAddress remoteAddress = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
                string advertised = FormatEndpoint(remoteAddress.ToString(), message.ListeningPort);
                // The sender is already connected. Store its advertised address for
                // future sessions, but do not let it displace newly learned peers in
                // the immediate outbound connection queue.
                AddKnownEndpoint(advertised, false);
                // Preserve the configured hostname for outbound connections.
                // Replacing it with the observed numeric address made the
                // maintenance loop believe the configured peer was disconnected
                // and open duplicate connections to it repeatedly.
                string previous;
                if (!peerEndpoints.TryGetValue(client, out previous))
                {
                    peerEndpoints[client] = advertised;
                    connectedEndpoints.TryAdd(advertised, 0);
                }
            }
            if (message.Peers != null)
                foreach (string endpoint in message.Peers.Take(256)) AddKnownEndpoint(endpoint, true);
        }

        private void AddKnownEndpoint(string endpoint)
        {
            AddKnownEndpoint(endpoint, false);
        }

        private void AddKnownEndpoint(string endpoint, bool prioritizeConnection)
        {
            string normalized;
            if (!TryNormalizeEndpoint(endpoint, out normalized)) return;
            if (knownEndpoints.Count >= 2048 && !knownEndpoints.ContainsKey(normalized)) return;
            knownEndpoints.TryAdd(normalized, 0);
            if (prioritizeConnection && !connectedEndpoints.ContainsKey(normalized) && prioritizedEndpoints.TryAdd(normalized, 0))
                recentlyLearnedEndpoints.Enqueue(normalized);
        }

        private bool TryNormalizeEndpoint(string endpoint, out string normalized)
        {
            normalized = null;
            string host;
            int port;
            if (!TryParseEndpoint(endpoint, out host, out port) || IsSelf(host, port)) return false;
            normalized = FormatEndpoint(host, port);
            addressBook.Seen(normalized);
            return true;
        }

        private void TrySaveAddressBook()
        {
            try { addressBook.Save(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        }

        private async Task SendToAll(PeerMessage message, TcpClient excluded)
        {
            foreach (TcpClient peer in peers.Keys)
                if (peer != excluded) await SendToPeer(peer, message).ConfigureAwait(false);
        }

        private async Task SendToPeer(TcpClient peer, PeerMessage message)
        {
            SemaphoreSlim writeLock;
            if (!peers.TryGetValue(peer, out writeLock)) return;
            byte[] body;
            using (var memory = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(PeerMessage)).WriteObject(memory, message);
                body = memory.ToArray();
            }
            byte[] length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(body.Length));
            var packet = new byte[length.Length + body.Length];
            Buffer.BlockCopy(length, 0, packet, 0, length.Length);
            Buffer.BlockCopy(body, 0, packet, length.Length, body.Length);
            try
            {
                await writeLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try { await peer.GetStream().WriteAsync(packet, 0, packet.Length, cancellation.Token).ConfigureAwait(false); }
                finally { writeLock.Release(); }
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException) { RemovePeer(peer); }
        }

        private void RemovePeer(TcpClient peer)
        {
            SemaphoreSlim writeLock;
            if (peers.TryRemove(peer, out writeLock)) writeLock.Dispose();
            string endpoint;
            if (peerEndpoints.TryRemove(peer, out endpoint)) connectedEndpoints.TryRemove(endpoint, out _);
            peer.Dispose();
            PeerCountChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool IsSelf(string host, int port)
        {
            if (port != listeningPort) return false;
            IPAddress address;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address));
        }

        private static string FormatEndpoint(string host, int port)
        {
            string value = host.Trim().Trim('[', ']');
            return (value.Contains(":") ? "[" + value + "]" : value.ToLowerInvariant()) + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        private static TcpListener CreateListener(int port)
        {
            try
            {
                var dualStack = new TcpListener(IPAddress.IPv6Any, port);
                dualStack.Server.DualMode = true;
                return dualStack;
            }
            catch (Exception error) when (error is SocketException || error is NotSupportedException)
            {
                return new TcpListener(IPAddress.Any, port);
            }
        }

        private static async Task<TcpClient> OpenConnectionAsync(string host, int port)
        {
            IPAddress literal;
            IPAddress[] addresses = IPAddress.TryParse(host, out literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            addresses = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork ||
                    address.AddressFamily == AddressFamily.InterNetworkV6)
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

            Exception lastError = null;
            foreach (IPAddress address in addresses)
            {
                var client = new TcpClient(address.AddressFamily);
                try
                {
                    // TcpClient.ConnectAsync on .NET Framework is implemented with
                    // BeginConnect/EndConnect. Some Framework/Mono combinations can
                    // pass a null IAsyncResult to EndConnect and fail inside
                    // System.Net.Logging before the connection result is reported.
                    // SocketAsyncEventArgs uses the event-based socket API instead
                    // and therefore avoids that framework-specific failure path.
                    Task connect = ConnectSocketAsync(client.Client, address, port);
                    if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false) != connect)
                    {
                        ObserveFault(connect);
                        throw new TimeoutException("The peer did not answer within 8 seconds.");
                    }
                    await connect.ConfigureAwait(false);
                    return client;
                }
                catch (Exception error) when (error is SocketException || error is TimeoutException || error is ObjectDisposedException)
                {
                    lastError = error;
                    client.Dispose();
                }
            }
            throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
        }

        private static async Task ConnectSocketAsync(Socket socket, IPAddress address, int port)
        {
            var completion = new TaskCompletionSource<SocketError>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var arguments = new SocketAsyncEventArgs { RemoteEndPoint = new IPEndPoint(address, port) })
            {
                EventHandler<SocketAsyncEventArgs> completed = null;
                completed = (sender, eventArguments) =>
                {
                    eventArguments.Completed -= completed;
                    completion.TrySetResult(eventArguments.SocketError);
                };
                arguments.Completed += completed;

                if (!socket.ConnectAsync(arguments))
                {
                    arguments.Completed -= completed;
                    completion.TrySetResult(arguments.SocketError);
                }

                SocketError result = await completion.Task.ConfigureAwait(false);
                if (result != SocketError.Success) throw new SocketException((int)result);
            }
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(completed => completed.Exception.Handle(error => true), TaskContinuationOptions.OnlyOnFaulted);
        }

        private static bool TryParseEndpoint(string endpoint, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            string value = endpoint.Trim();
            int separator;
            if (value[0] == '[')
            {
                int closing = value.IndexOf(']');
                if (closing < 2 || closing + 1 >= value.Length || value[closing + 1] != ':') return false;
                host = value.Substring(1, closing - 1);
                separator = closing + 1;
            }
            else
            {
                separator = value.LastIndexOf(':');
                if (separator <= 0) return false;
                host = value.Substring(0, separator);
            }
            return int.TryParse(value.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0 && port <= 65535;
        }

        private static async Task<byte[]> ReadExactly(Stream stream, int count, CancellationToken token)
        {
            var result = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(result, offset, count - offset, token).ConfigureAwait(false);
                if (read == 0) throw new IOException("Peer disconnected.");
                offset += read;
            }
            return result;
        }

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Stop();
            foreach (TcpClient peer in peers.Keys) RemovePeer(peer);
            TrySaveAddressBook();
            if (portMapper != null) portMapper.Dispose();
            cancellation.Dispose();
        }
    }

    public sealed class TransactionReceivedEventArgs : EventArgs
    {
        public TransactionReceivedEventArgs(Transaction transaction) { Transaction = transaction; }
        public Transaction Transaction { get; }
    }

    public sealed class ChainReceivedEventArgs : EventArgs
    {
        public ChainReceivedEventArgs(Block[] blocks) { Blocks = blocks; }
        public Block[] Blocks { get; }
    }
}
