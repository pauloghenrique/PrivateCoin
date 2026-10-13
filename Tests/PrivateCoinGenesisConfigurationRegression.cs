using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PrivateCoin.Core;

internal static class PrivateCoinGenesisConfigurationRegression
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static Block Copy(Block block)
    {
        var serializer = new DataContractJsonSerializer(typeof(Block));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, block);
            stream.Position = 0;
            return (Block)serializer.ReadObject(stream);
        }
    }

    private static void SetManifest(Block block, string manifest)
    {
        typeof(GenesisRules).GetProperty("CoreConfigurationManifest").GetSetMethod(true)
            .Invoke(block.Rules, new object[] { manifest });
    }

    private static void Reject(Block block, string name)
    {
        bool rejected = false;
        try { new Blockchain(new[] { block }); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, name);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task RejectedPeer(int port, string configurationHash, string name, bool validHelloFirst = false)
    {
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            string json = "{\"MessageId\":\"" + Guid.NewGuid() + "\",\"Type\":\"sync-request\",\"NetworkId\":\"" +
                Blockchain.NetworkId + "\",\"ConsensusVersion\":" + Blockchain.ConsensusVersion +
                (configurationHash == null ? "" : ",\"RulesHash\":\"" + configurationHash + "\"") + "}";
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] size = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(body.Length));
            var stream = client.GetStream();
            if (validHelloFirst)
            {
                string hello = "{\"MessageId\":\"" + Guid.NewGuid() + "\",\"Type\":\"hello\",\"NetworkId\":\"" +
                    Blockchain.NetworkId + "\",\"ConsensusVersion\":" + Blockchain.ConsensusVersion +
                    ",\"RulesHash\":\"" + Blockchain.RulesHash + "\"}";
                byte[] helloBody = Encoding.UTF8.GetBytes(hello);
                byte[] helloSize = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(helloBody.Length));
                await stream.WriteAsync(helloSize, 0, helloSize.Length);
                await stream.WriteAsync(helloBody, 0, helloBody.Length);
            }
            await stream.WriteAsync(size, 0, size.Length);
            await stream.WriteAsync(body, 0, body.Length);
            var drain = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try { while (await stream.ReadAsync(buffer, 0, buffer.Length) != 0) { } }
                catch (IOException) { }
            });
            Check(await Task.WhenAny(drain, Task.Delay(5000)) == drain, name);
            await drain;
        }
    }

    private static void Main() { Run().GetAwaiter().GetResult(); }

    private static async Task Run()
    {
        var chain = new Blockchain();
        Block genesis = chain.Blocks[0];
        Check(chain.IsValid() && ProofOfWork.MeetsTarget(genesis.Hash), "canonical genesis and proof of work");
        Check(genesis.Rules.CoreConfigurationManifest == CoreConfiguration.Manifest &&
            CoreConfiguration.Manifest.Contains("PoolFeeBasisPoints") &&
            CoreConfiguration.Manifest.Contains("RewardPhase.4.RewardPerBlock") &&
            CoreConfiguration.Manifest.Contains("MaximumMessageBytes") &&
            CoreConfiguration.Manifest.Contains("MaximumNameLength"), "genesis contains full fixed-parameter manifest");
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
        try
        {
            var build = typeof(CoreConfiguration).GetMethod("BuildManifest", BindingFlags.Static | BindingFlags.NonPublic);
            Check((string)build.Invoke(null, null) == CoreConfiguration.Manifest, "manifest independent of culture");
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
        Check(new Blockchain(new[] { Copy(genesis) }).IsValid(), "serialized manifest restored");
        var extended = new Blockchain();
        extended.AddBlock(new[] { extended.CreateWalletCreationTransaction("configuration-regression", new Transaction[0]) });
        Check(extended.IsValid() && extended.Blocks[1].Rules == null,
            "subsequent blocks preserve genesis configuration without repeating it");
        Block extra = Copy(extended.Blocks[1]);
        extra.Rules = Copy(genesis).Rules;
        typeof(ProofOfWork).GetMethod("Mine", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { extra, CancellationToken.None });
        bool rejectedExtra = false;
        try { new Blockchain(new[] { Copy(genesis), extra }); }
        catch (InvalidOperationException) { rejectedExtra = true; }
        Check(rejectedExtra, "later blocks cannot redefine configuration even with valid work");
        var changed = Copy(genesis);
        SetManifest(changed, CoreConfiguration.Manifest + "altered");
        Reject(changed, "altered manifest rejected");
        typeof(ProofOfWork).GetMethod("Mine", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { changed, CancellationToken.None });
        Reject(changed, "altered manifest rejected even after remining");
        changed = Copy(genesis); SetManifest(changed, null);
        Reject(changed, "missing manifest rejected");
        Reject(new Block { PreviousHash = new string('0', 64), TimestampUtcTicks = 639028224000000000L,
            Nonce = 4995L, Hash = "0008127acee1ee9328acdc860e2497340d4923426da9b391088d5c83ef46b673" }, "old genesis rejected");
        int port = FreePort();
        using (var receiver = new PeerNode(port))
        {
            int received = 0;
            receiver.SynchronizationRequested += (s, e) => Interlocked.Increment(ref received);
            receiver.TransactionReceived += (s, e) => Interlocked.Increment(ref received);
            receiver.ChainReceived += (s, e) => Interlocked.Increment(ref received);
            receiver.Start();
            await RejectedPeer(port, new string('0', 64), "different configuration disconnected");
            await RejectedPeer(port, null, "missing configuration disconnected");
            await RejectedPeer(port, new string('0', 64), "changed configuration disconnected after valid hello", true);
            Check(received == 0, "incompatible messages do not reach application handlers");
            int senderPort = FreePort();
            using (var sender = new PeerNode(senderPort))
            {
                var synchronized = new TaskCompletionSource<bool>();
                receiver.ChainReceived += (s, e) => synchronized.TrySetResult(new Blockchain(e.Blocks).IsValid());
                sender.Start();
                await sender.ConnectAsync("127.0.0.1", port);
                await sender.BroadcastChainAsync(chain.Blocks);
                Check(await Task.WhenAny(synchronized.Task, Task.Delay(5000)) == synchronized.Task &&
                    await synchronized.Task, "matching configurations synchronize over TCP");
            }
        }
    }
}
