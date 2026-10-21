using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Threading;
using PrivateCoin.Core;

namespace Povix.Bridge.CSharp {
    // Outbound client for the EXISTING framed PeerMessage protocol. No Core/genesis changes.
    public static class AutomationNative {
        [DataContract] sealed class Frame {
            [DataMember] public string MessageId;
            [DataMember] public string Type;
            [DataMember] public Transaction Transaction;
            [DataMember] public int ListeningPort;
            [DataMember] public string[] Peers;
            [DataMember] public string NetworkId;
            [DataMember] public int ConsensusVersion;
            [DataMember] public string RulesHash;
        }
        static Frame Message(string type, Transaction tx = null) {
            return new Frame { Type = type, MessageId = BridgeNative.Hash(Guid.NewGuid().ToString("N")), Transaction = tx,
                NetworkId = Blockchain.NetworkId, ConsensusVersion = Blockchain.ConsensusVersion, RulesHash = Blockchain.RulesHash, ListeningPort = 0, Peers = new string[0] };
        }
        static DataContractJsonSerializer Serializer() { return new DataContractJsonSerializer(typeof(Frame), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 8192 }); }
        static void Write(Stream stream, Frame message) {
            using (var buffer = new MemoryStream()) {
                Serializer().WriteObject(buffer, message); byte[] body = buffer.ToArray();
                if (body.Length > 262144) throw new InvalidOperationException("Native relay frame exceeds 256 KiB.");
                byte[] length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(body.Length)); stream.Write(length, 0, 4); stream.Write(body, 0, body.Length); stream.Flush();
            }
        }
        static byte[] Read(Stream stream, int count) {
            byte[] bytes = new byte[count]; int offset = 0;
            while (offset < count) { int n = stream.Read(bytes, offset, count - offset); if (n == 0) throw new IOException("Native peer disconnected before acknowledging receipt."); offset += n; } return bytes;
        }
        static void Barrier(Stream stream) {
            for (int i = 0; i < 16; i++) {
                int count = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(Read(stream, 4), 0));
                if (count <= 0 || count > 262144) throw new InvalidOperationException("Native peer response exceeds frame limit.");
                Frame message; using (var buffer = new MemoryStream(Read(stream, count))) message = (Frame)Serializer().ReadObject(buffer);
                if (message == null || message.NetworkId != Blockchain.NetworkId || message.ConsensusVersion != Blockchain.ConsensusVersion || message.RulesHash != Blockchain.RulesHash) throw new InvalidOperationException("Native peer belongs to another network or consensus version.");
                if (message.Type == "addr") return;
            }
            throw new InvalidOperationException("Native peer did not finish the protocol handshake.");
        }
        public static async Task Deliver(string[] peers, Transaction tx) {
            int delivered = 0;
            foreach (string peer in peers) {
                try {
                    var endpoint = new Uri("tcp://" + peer);
                    using (var client = new TcpClient { NoDelay = true, ReceiveTimeout = 15000, SendTimeout = 15000 }) {
                        var connect = client.ConnectAsync(endpoint.Host, endpoint.Port);
                        if (await Task.WhenAny(connect, Task.Delay(15000)) != connect) throw new TimeoutException("Native peer connection timed out."); await connect;
                        client.ReceiveTimeout = 15000; client.SendTimeout = 15000;
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        using (deadline.Token.Register(() => client.Close()))
                        using (var stream = client.GetStream()) {
                            Write(stream, Message("hello")); Barrier(stream);
                            Write(stream, Message("transaction", tx));
                            // The existing peer reads messages in order and replies to hello with addr.
                            // A second round trip ensures it handled the tx before this short-lived client closes.
                            // This is transport receipt only; confirmation is checked in the independent snapshots.
                            Write(stream, Message("hello")); Barrier(stream); delivered++;
                        }
                    }
                } catch (Exception error) { Console.Error.WriteLine("Native delivery failed for " + peer + ": " + error.Message); }
            }
            if (delivered == 0) throw new InvalidOperationException("No compatible native peer acknowledged receipt; the persisted transaction will be retried.");
        }
    }
}
