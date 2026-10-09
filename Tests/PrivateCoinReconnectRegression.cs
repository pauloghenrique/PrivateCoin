using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using PrivateCoin.Core;
using PrivateCoin.Desktop;

internal static class PrivateCoinReconnectRegression
{
    public static int Main(string[] args)
    {
        try
        {
            string directory = args.Length > 0 ? args[0] : Path.Combine("work", "reconnect-regression");
            Directory.CreateDirectory(directory);
            string nodePath = Path.Combine(directory, "local-node-id.dat");
            string localNodeId = NodeIdentity.LoadOrCreate(nodePath);
            string remoteNodeId = NodeIdentity.LoadOrCreate(Path.Combine(directory, "remote-node-id.dat"));
            Check(localNodeId == NodeIdentity.LoadOrCreate(nodePath) && localNodeId != remoteNodeId,
                "node identity persists across restarts and differs between installations");
            var readiness = new NetworkReadiness();
            bool connected = false;
            var local = new Blockchain(localNodeId);
            var remote = new Blockchain(remoteNodeId);
            Check(!readiness.IsReady, "startup requires synchronization");
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            connected = true;
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            long staleEpoch = readiness.Epoch;
            Check(readiness.Accept(staleEpoch, () => connected, () => Synchronize(local, remote.Blocks)),
                "an identical validated chain releases the node");
            Block localBlock = readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0]));
            Check(localBlock.CreatorNodeId == localNodeId, "new blocks record the local creating node");
            connected = false;
            readiness.Disconnect();
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            connected = true;
            Check(!readiness.Accept(staleEpoch, () => connected, () => { throw new Exception("stale callback ran"); }),
                "a queued response from before disconnection cannot release the node");
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            remote.TryReplaceChain(local.Blocks);
            remote.AddBlock(new Transaction[0]);
            Check(readiness.Accept(readiness.Epoch, () => connected, () => Synchronize(local, remote.Blocks)) &&
                local.Blocks[local.Blocks.Count - 1].Hash == remote.Blocks[remote.Blocks.Count - 1].Hash,
                "reconnection adopts a longer chain whose shared heights match");
            Check(local.LocalNodeId == localNodeId && local.Blocks.Last().CreatorNodeId == remoteNodeId,
                "synchronization preserves block origins without replacing the local node identity");
            readiness.Disconnect();
            var invalid = new Blockchain().Blocks;
            invalid[invalid.Count - 1].Hash = "invalid";
            Reject(() => readiness.Accept(readiness.Epoch, () => connected, () => Synchronize(local, invalid)));
            Check(!readiness.IsReady, "invalid chain cannot release synchronization");
            CheckHistoryProtection(localNodeId, remoteNodeId);
            CheckOrigins(directory, localNodeId, remoteNodeId);
            CheckDesktopPersistence(directory);
            CheckPeerReconnect(directory, localNodeId, remoteNodeId, false).GetAwaiter().GetResult();
            CheckPeerReconnect(directory, localNodeId, remoteNodeId, true).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void CheckHistoryProtection(string localNodeId, string remoteNodeId)
    {
        using (var voter = new Wallet())
        {
        ValidatorStake signer;
        var common = FinalityTestSupport.ActiveChain(voter, out signer);
        var smaller = new Blockchain(common.Blocks, localNodeId);
        smaller.AddBlock(new Transaction[0]);
        FinalityTestSupport.FinalizeAvailable(smaller, signer);
        var returning = new Blockchain(common.Blocks, remoteNodeId);
        returning.AddBlock(new Transaction[0]);
        FinalityTestSupport.FinalizeAvailable(returning, signer);
        returning.AddBlock(new Transaction[0]);
        returning.AddBlock(new Transaction[0]);
        string smallerTip = smaller.Blocks.Last().Hash;
        string returningTip = returning.Blocks.Last().Hash;
        Reject(() => smaller.TryReplaceChain(returning.Blocks));
        Check(smaller.Blocks.Last().Hash == smallerTip,
            "a longer divergent chain cannot replace the protected network history");
        var readiness = new NetworkReadiness();
        Reject(() => readiness.Accept(readiness.Epoch, () => true, () => Synchronize(returning, smaller.Blocks)));
        Check(returning.Blocks.Last().Hash == returningTip && !readiness.IsReady,
            "a shorter divergent chain cannot replace local history or release a reconnecting node");
        Reject(() => readiness.Execute(() => true, () => returning.AddBlock(new Transaction[0])));
        Check(!readiness.Accept(readiness.Epoch, () => true, () => Synchronize(returning, common.Blocks)) &&
            !readiness.IsReady && returning.Blocks.Last().Hash == returningTip,
            "a valid older prefix preserves local blocks and cannot complete synchronization");
        bool changed;
        Check(returning.TrySynchronizeChain(returning.Blocks, out changed) && !changed &&
            !returning.TryReplaceChain(returning.Blocks), "an identical chain is accepted without rewriting history");
        var continuation = new Blockchain(returning.Blocks, localNodeId, returning.GetFinalityState());
        continuation.AddBlock(new Transaction[0]);
        Block[] protectedBlocks = returning.Blocks.ToArray();
        Check(readiness.Accept(readiness.Epoch, () => true, () => Synchronize(returning, continuation.Blocks)) &&
            protectedBlocks.Select((block, height) => ReferenceEquals(block, returning.Blocks[height])).All(value => value),
            "a complete matching continuation releases synchronization and preserves existing block objects");
        Check(readiness.Execute(() => true, () => returning.AddBlock(new Transaction[0])).CreatorNodeId == remoteNodeId,
            "blocks created after successful reconnection retain the local node identity");

        var sameLength = new Blockchain(common.Blocks);
        sameLength.AddBlock(new Transaction[0]);
        FinalityTestSupport.FinalizeAvailable(sameLength, signer);
        var left = new Blockchain(smaller.Blocks, smaller.LocalNodeId, smaller.GetFinalityState());
        var right = new Blockchain(sameLength.Blocks, sameLength.LocalNodeId, sameLength.GetFinalityState());
        string leftTip = left.Blocks.Last().Hash, rightTip = right.Blocks.Last().Hash;
        Reject(() => left.TryReplaceChain(right.Blocks));
        Reject(() => right.TryReplaceChain(left.Blocks));
        Check(left.Blocks.Last().Hash == leftTip && right.Blocks.Last().Hash == rightTip,
            "a divergent equal-length chain cannot win through a smaller tip hash");

        // Divergence can start earlier than the shorter chain's last block.
        var deeper = new Blockchain(common.Blocks);
        deeper.AddBlock(new Transaction[0]);
        FinalityTestSupport.FinalizeAvailable(deeper, signer);
        deeper.AddBlock(new Transaction[0]);
        var longer = new Blockchain(common.Blocks);
        for (int i = 0; i < 4; i++) longer.AddBlock(new Transaction[0]);
        FinalityTestSupport.FinalizeAvailable(longer, signer);
        string longerTip = longer.Blocks.Last().Hash;
        Reject(() => longer.TryReplaceChain(deeper.Blocks));
        Check(longer.Blocks.Last().Hash == longerTip,
            "divergence before the shorter tip is rejected without changing history");

        Block[] badShorter = Copy(common.Blocks.ToArray());
        badShorter[1].Hash = "invalid";
        string currentTip = longer.Blocks.Last().Hash;
        Reject(() => longer.TryReplaceChain(badShorter));
        Check(longer.Blocks.Last().Hash == currentTip, "an invalid shorter chain never replaces the current chain");
        }
    }

    private static void CheckOrigins(string directory, string localNodeId, string remoteNodeId)
    {
        var chain = new Blockchain(localNodeId);
        chain.AddBlock(new Transaction[0]);
        var restored = new Blockchain(Copy(chain.Blocks.ToArray()), NodeIdentity.LoadOrCreate(Path.Combine(directory, "local-node-id.dat")));
        Check(restored.LocalNodeId == localNodeId && restored.Blocks[1].CreatorNodeId == localNodeId &&
            restored.AddBlock(new Transaction[0]).CreatorNodeId == localNodeId,
            "serialization and restart preserve origins for old and newly created blocks");
        Block[] altered = Copy(chain.Blocks.ToArray());
        altered[1].CreatorNodeId = remoteNodeId;
        Reject(() => new Blockchain(altered));
        altered[1].CreatorNodeId = null;
        Reject(() => new Blockchain(altered));
        Check(true, "altering or removing a recorded origin invalidates the block hash");

        // Old snapshots have no origin field and retain their original hash encoding.
        Block[] legacy = Copy(chain.Blocks.ToArray());
        legacy[1].CreatorNodeId = null;
        Mine(legacy[1]);
        var upgraded = new Blockchain(legacy, localNodeId);
        Check(upgraded.IsValid() && upgraded.Blocks[0].Hash == Blockchain.GenesisHash &&
            upgraded.AddBlock(new Transaction[0]).CreatorNodeId == localNodeId,
            "legacy blocks load unchanged and subsequent blocks record the node identity");
        legacy[1].CreatorNodeId = "invalid";
        Mine(legacy[1]);
        Reject(() => new Blockchain(legacy));
        Check(true, "invalid node identifier encoding is rejected even with a recomputed proof");
    }

    private static async Task CheckPeerReconnect(string directory, string localNodeId, string remoteNodeId, bool divergent)
    {
        using (var voter = new Wallet())
        {
        ValidatorStake signer;
        var common = FinalityTestSupport.ActiveChain(voter, out signer);
        var network = new Blockchain(common.Blocks, localNodeId);
        var returning = new Blockchain(common.Blocks, remoteNodeId);
        int networkPort = Port(), returningPort = Port();
        while (returningPort == networkPort) returningPort = Port();
        using (var networkPeer = new PeerNode(networkPort, false, Path.Combine(directory,
            divergent ? "conflict-peers.dat" : "continuation-peers.dat")))
        {
            var networkOutcome = new TaskCompletionSource<bool>();
            networkPeer.ChainReceived += (sender, args) => {
                try { networkOutcome.TrySetResult(Synchronize(network, args.Blocks, args.Finality)); }
                catch (InvalidOperationException) { networkOutcome.TrySetResult(false); }
            };
            networkPeer.SynchronizationRequested += (sender, args) => networkPeer.BroadcastChainAsync(network);
            networkPeer.Start();
            using (var initialPeer = new PeerNode(returningPort, false))
            {
                var initialSync = new TaskCompletionSource<bool>();
                initialPeer.ChainReceived += (sender, args) => {
                    try { initialSync.TrySetResult(Synchronize(returning, args.Blocks, args.Finality)); }
                    catch (Exception error) { initialSync.TrySetException(error); }
                };
                initialPeer.Start();
                await initialPeer.ConnectAsync("127.0.0.1", networkPort);
                await Complete(initialSync.Task);
            }
            network.AddBlock(new Transaction[0]);
            if (divergent)
            {
                returning.AddBlock(new Transaction[0]);
                returning.AddBlock(new Transaction[0]);
            }
            else network.AddBlock(new Transaction[0]);
            FinalityTestSupport.FinalizeAvailable(network, signer);
            if (divergent) FinalityTestSupport.FinalizeAvailable(returning, signer);
            string expected = network.Blocks.Last().Hash;
            string returningTip = returning.Blocks.Last().Hash;
            using (var reconnectedPeer = new PeerNode(returningPort, false))
            {
                var readiness = new NetworkReadiness();
                var resynchronized = new TaskCompletionSource<bool>();
                reconnectedPeer.ChainReceived += (sender, args) => {
                    try { resynchronized.TrySetResult(readiness.Accept(readiness.Epoch,
                        () => reconnectedPeer.ConnectedPeerCount > 0, () => Synchronize(returning, args.Blocks, args.Finality))); }
                    catch (InvalidOperationException) { resynchronized.TrySetResult(false); }
                };
                reconnectedPeer.SynchronizationRequested += (sender, args) => reconnectedPeer.BroadcastChainAsync(returning);
                reconnectedPeer.Start();
                await reconnectedPeer.ConnectAsync("127.0.0.1", networkPort);
                await Complete(resynchronized.Task);
                if (divergent)
                {
                    await Complete(networkOutcome.Task);
                    Check(!resynchronized.Task.Result && !networkOutcome.Task.Result && !readiness.IsReady &&
                        network.Blocks.Last().Hash == expected && returning.Blocks.Last().Hash == returningTip,
                        "real TCP reconnection rejects conflicting histories on both nodes and keeps operations blocked");
                }
                else
                    Check(resynchronized.Task.Result && readiness.IsReady && network.Blocks.Last().Hash == expected &&
                        returning.Blocks.Last().Hash == expected && returning.Blocks.Last().CreatorNodeId == localNodeId,
                        "real TCP reconnection accepts a matching continuation and preserves the creating node");
            }
        }
        }
    }

    private static bool Synchronize(Blockchain chain, IEnumerable<Block> blocks, FinalityState finality = null)
    {
        bool changed;
        return chain.TrySynchronizeChain(blocks, finality, out changed);
    }

    private static async Task Complete(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(10000)) != task) throw new Exception("Peer synchronization timed out.");
        await task;
    }
    private static void CheckDesktopPersistence(string directory)
    {
        using (var voter = new Wallet())
        {
        string firstDirectory = Path.Combine(directory, "desktop-first");
        var firstStore = new WalletStore(firstDirectory);
        ValidatorStake signer;
        var first = FinalityTestSupport.ActiveChain(voter, out signer, firstStore.NodeId);
        first.AddBlock(new Transaction[0]);
        FinalityTestSupport.FinalizeAvailable(first, signer);
        firstStore.SaveNetwork(first, new Transaction[0]);
        var restartedStore = new WalletStore(firstDirectory);
        System.Collections.Generic.List<Transaction> pending;
        Blockchain restored = restartedStore.LoadNetwork(out pending);
        Check(restored.LocalNodeId == first.LocalNodeId && restored.Blocks.Last().CreatorNodeId == first.LocalNodeId &&
            restored.AddBlock(new Transaction[0]).CreatorNodeId == first.LocalNodeId,
            "the actual Desktop store restores the same node identity after restart");
        var conflicting = new Blockchain();
        conflicting.AddBlock(new Transaction[0]);
        string restoredTip = restored.Blocks.Last().Hash;
        Reject(() => restored.TryReplaceChain(conflicting.Blocks));
        Check(restored.Blocks.Last().Hash == restoredTip &&
            restartedStore.LoadNetwork(out pending).Blocks.Last().Hash == first.Blocks.Last().Hash,
            "restarting the Desktop preserves protection of the stored history");
        string secondDirectory = Path.Combine(directory, "desktop-second");
        Directory.CreateDirectory(secondDirectory);
        File.Copy(Path.Combine(firstDirectory, "Blockchain.json"), Path.Combine(secondDirectory, "Blockchain.json"), true);
        var secondStore = new WalletStore(secondDirectory);
        Blockchain copied = secondStore.LoadNetwork(out pending);
        Check(copied.LocalNodeId != first.LocalNodeId && copied.Blocks.Last().CreatorNodeId == first.LocalNodeId &&
            copied.AddBlock(new Transaction[0]).CreatorNodeId == secondStore.NodeId,
            "copying a public chain snapshot preserves origins and gives the other installation its own identity");
        }
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static Block[] Copy(Block[] blocks)
    {
        using (var memory = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(Block[]));
            serializer.WriteObject(memory, blocks);
            memory.Position = 0;
            return (Block[])serializer.ReadObject(memory);
        }
    }
    private static void Mine(Block block)
    {
        var calculate = typeof(Block).GetMethod("CalculateHash", BindingFlags.Instance | BindingFlags.NonPublic);
        do { block.Nonce++; block.Hash = (string)calculate.Invoke(block, null); }
        while (!block.Hash.StartsWith("000", StringComparison.Ordinal));
    }
    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS operation blocked"); return; }
        throw new Exception("Expected operation rejection");
    }
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
