using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Povix.Dex.Models;
using Povix.Dex.Services;
using PrivateCoin.Core;
using PrivateCoin.Desktop;

internal static class PovixDexDesktopSynchronizationRegression
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static void Wait(Func<bool> condition, string label)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 10000) Thread.Sleep(25);
        Check(condition(), label);
    }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static Dictionary<string, object> Data(object value) => Json.DeserializeObject(Json.Serialize(value)) as Dictionary<string, object>;
    private static string[] PublicKeys(Wallet wallet) => wallet.ExportPrivateKeys().Select(xml => {
        using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); return rsa.ToXmlString(false); }
    }).ToArray();
    private static string Sign(Wallet wallet, string address, string payload)
    {
        foreach (string xml in wallet.ExportPrivateKeys()) using (var rsa = new RSACryptoServiceProvider())
        {
            rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml);
            using (var hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(rsa.ToXmlString(false)))).Replace("-", "").ToLowerInvariant() == address)
                    return Convert.ToBase64String(rsa.SignData(Convert.FromBase64String(payload), CryptoConfig.MapNameToOID("SHA256")));
        }
        throw new Exception("Disposable test key not found");
    }
    private static string[] SignInputs(Wallet wallet, Dictionary<string, object> draft) => ((object[])draft["inputAddresses"])
        .Select(address => Sign(wallet, (string)address, (string)draft["signingPayload"])).ToArray();

    private sealed class DesktopPeer : IDisposable
    {
        private readonly object gate = new object();
        private readonly NetworkReadiness readiness = new NetworkReadiness();
        private readonly List<Transaction> pending = new List<Transaction>();
        private Exception failure;
        private int chainMessages;
        public readonly Blockchain Chain;
        public readonly PeerNode Node;
        public int StaleChains { get; private set; }
        public int ChainMessages => Volatile.Read(ref chainMessages);
        public bool Ready => readiness.IsReady;
        public DesktopPeer(Blockchain chain, string directory)
        {
            Chain = chain; Node = new PeerNode(Port(), false, Path.Combine(directory, "desktop-peers.dat"));
            Node.PeerCountChanged += (sender, args) => { if (Node.ConnectedPeerCount == 0) readiness.Disconnect(); };
            Node.TransactionReceived += (sender, args) => {
                try { lock (gate) { if (readiness.IsReady) Queue(args.Transaction); else readiness.Defer(readiness.Epoch, args.Transaction); } }
                catch (Exception error) { failure = error; }
            };
            Node.ChainReceived += (sender, args) => {
                Interlocked.Increment(ref chainMessages);
                try { lock (gate) {
                    bool changed;
                    if (!readiness.Accept(readiness.Epoch, () => Node.ConnectedPeerCount > 0,
                        () => Chain.TrySynchronizeChain(args.Blocks, out changed))) { StaleChains++; return; }
                    foreach (Transaction tx in readiness.TakeDeferred(readiness.Epoch)) Queue(tx);
                } } catch (Exception error) { failure = error; }
            };
            // Delay the Desktop's initial answer to exercise the real handshake order:
            // the older DEX snapshot arrives before it adopts the Desktop's chain.
            Node.Start(new string[0]);
        }
        private void Queue(Transaction tx)
        {
            if (pending.Any(item => item.Id == tx.Id) || Chain.Blocks.Any(block => block.Transactions.Any(item => item.Id == tx.Id))) return;
            Chain.ValidatePendingTransactions(pending.Concat(new[] { tx }));
            pending.Add(tx);
        }
        public Transaction Find(string id) { lock (gate) { if (failure != null) throw failure; return pending.FirstOrDefault(tx => tx.Id == id); } }
        public void Confirm(Block block) { lock (gate) pending.RemoveAll(tx => block.Transactions.Any(included => included.Id == tx.Id)); }
        public void Dispose() { Node.Dispose(); }
    }

    private static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var issuer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain(); string owner = issuer.CreateReceiveAddress(), a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress();
            LegacyConsensusFixture.FundBatch(chain, owner, a, b);
            chain.AddBlock(new[] { first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 0), second.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 0) });
            var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
            using (var desktop = new DesktopPeer(chain, directory))
            {
                int dexPort = Port();
                using (var dex = new TokenNetworkService(Path.Combine(directory, "dex"), dexPort, new string[0]))
                {
                    desktop.Node.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult();
                    Wait(() => desktop.StaleChains > 0, "Desktop first receives and rejects the older DEX snapshot");
                    Check(!desktop.Ready, "an older DEX snapshot does not bypass Desktop synchronization");
                    desktop.Node.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => dex.GetNetwork().CanCreate, "DEX adopts the newer Desktop chain and accepts submissions");
                    var creation = Data(dex.Prepare(new CreateTokenViewModel { Name = "Sincronizado", Symbol = "SYN", Decimals = 0, Supply = "100", DestinationAddress = owner, FeePriority = 1, FeeAtomic = 2 }, PublicKeys(issuer), owner, "creation"));
                    string creationId = dex.SubmitAsync((string)creation["draftId"], SignInputs(issuer, creation), "creation").GetAwaiter().GetResult();
                    Wait(() => desktop.Find(creationId) != null, "creation reaches the Desktop validation queue after both peers synchronize");
                    Check(desktop.Ready && dex.GetRegistration(creationId).Status == "pending", "submission releases no token before validator approval");
                    Confirm(desktop, dex, creationId, validators, false);
                    var movement = Data(dex.PrepareTransfer(new TokenTransferViewModel { TokenId = (string)creation["tokenId"], Amount = "25", DestinationAddress = new string('e', 64), FeePriority = 1, FeeAtomic = 3 }, PublicKeys(issuer), owner, "movement"));
                    string movementId = dex.SubmitTransferAsync((string)movement["draftId"], SignInputs(issuer, movement),
                        Sign(issuer, owner, (string)movement["authorizationPayload"]), "movement").GetAwaiter().GetResult();
                    Wait(() => desktop.Find(movementId) != null, "transfer reaches the same Desktop validation queue");
                    Confirm(desktop, dex, movementId, validators, true);
                    Check(chain.GetTokenBalance(new[] { new string('e', 64) }, (string)creation["tokenId"]) == 25 && dex.GetTransferReceipt(movementId).Confirmations == 1,
                        "the transfer becomes spendable only after its signed block is adopted by the DEX");
                }
                Wait(() => desktop.Node.ConnectedPeerCount == 0, "the Desktop observes disconnection without preserving readiness");
                Check(!desktop.Ready, "disconnection resets Desktop synchronization");
                int stale = desktop.StaleChains; dexPort = Port();
                using (var restarted = new TokenNetworkService(Path.Combine(directory, "fresh-dex"), dexPort, new string[0]))
                {
                    desktop.Node.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult();
                    Wait(() => desktop.StaleChains > stale, "a reconnecting DEX can again start with an older snapshot");
                    desktop.Node.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => restarted.GetNetwork().CanCreate && desktop.Ready, "both peers synchronize again after reconnecting to an empty DEX cache");
                    int replies = desktop.ChainMessages;
                    desktop.Node.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Thread.Sleep(500);
                    Check(desktop.ChainMessages == replies, "identical snapshots do not trigger repeated chain replies");
                }
            }
        }
    }
    private static void Confirm(DesktopPeer desktop, TokenNetworkService dex, string id, ValidatorStake[] validators, bool transfer)
    {
        Transaction tx = desktop.Find(id); int height = desktop.Chain.Blocks.Count;
        tx.TransactionApproval = desktop.Chain.CreateTransactionApproval(tx, new[] { tx }, validators.Take(1));
        desktop.Node.BroadcastAsync(tx).GetAwaiter().GetResult();
        Wait(() => (transfer ? dex.GetTransferReceipt(id).Status : dex.GetRegistration(id).Status) == "validated", "DEX receives validator approval while waiting for the block");
        TimeSpan now = TimeSpan.Zero; var schedule = new BlockProductionScheduler(() => now);
        Check(schedule.SelectBatch(desktop.Chain.GetApprovedTransactions(new[] { tx })).Count == 0, "an approved operation starts the ten-second production wait");
        now = TimeSpan.FromSeconds(9);
        Check(schedule.SelectBatch(new[] { tx }).Count == 0 && desktop.Chain.Blocks.Count == height, "nine seconds do not produce a partial block early");
        now = TimeSpan.FromSeconds(10);
        Block block = desktop.Chain.AddProofOfStakeBlock(schedule.SelectBatch(new[] { tx }), validators);
        desktop.Confirm(block); desktop.Node.BroadcastChainAsync(desktop.Chain.Blocks).GetAwaiter().GetResult();
        Wait(() => transfer ? dex.GetTransferReceipt(id).BlockHash == block.Hash : dex.GetRegistration(id).BlockHash == block.Hash,
            "DEX confirms the operation only when its actual signed block arrives");
    }
    public static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine(checks + " DEX/Desktop synchronization checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
