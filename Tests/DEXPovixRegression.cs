using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using DEXPovix.Models;
using DEXPovix.Services;
using PrivateCoin.Core;

public sealed class BrowserFixture
{
    public string PublicKey { get; set; }
    public string Address { get; set; }
    public string Recipient { get; set; }
    public string ChangeAddress { get; set; }
}

internal static class DEXPovixRegression
{
    private static int checks;
    private static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private static readonly Transaction[] none = new Transaction[0];
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    private static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is CryptographicException)
        { rejected = true; }
        Check(rejected, name);
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static string[] Sign(Wallet wallet, string payload, string[] addresses)
    {
        return addresses.Select(address => {
            foreach (string privateKey in wallet.ExportPrivateKeys())
                using (var key = new RSACryptoServiceProvider())
                {
                    key.PersistKeyInCsp = false; key.FromXmlString(privateKey);
                    if (Crypto.Sha256(key.ToXmlString(false)) == address)
                        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), CryptoConfig.MapNameToOID("SHA256")));
                }
            throw new Exception("Unknown test signing key");
        }).ToArray();
    }
    private static string PublicKey(Wallet wallet)
    {
        using (var key = new RSACryptoServiceProvider())
        { key.PersistKeyInCsp = false; key.FromXmlString(wallet.ExportPrivateKeys().First()); return key.ToXmlString(false); }
    }
    private static void Write<T>(string file, T value)
    { using (var stream = File.Create(file)) new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); }
    private static T Read<T>(string file)
    { using (var stream = File.OpenRead(file)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
    public static void Main(string[] args) { Run(args).GetAwaiter().GetResult(); }
    private static async Task Until(Func<bool> predicate)
    {
        for (int i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(25); }
        throw new Exception("Local P2P test timed out");
    }
    private static async Task Run(string[] args)
    {
        string directory = args.Length > 1 ? args[1] : "work/dexpovix";
        var browser = json.Deserialize<BrowserFixture>(File.ReadAllText(Path.Combine(directory, "browser-public.json")));
        if (args.Length > 0 && args[0] == "prepare")
        {
            var chain = new Blockchain(); Block block;
            chain.TryAddWalletCreationReward(browser.Address, out block);
            var draft = TokenCreationDraft.Prepare(chain, none, new[] { browser.PublicKey },
                "Token 🪙 | teste", "WEB", 8, 9007199254740993L, browser.Recipient, browser.ChangeAddress, 1);
            var transaction = draft.Complete(new[] { Convert.ToBase64String(new byte[256]) });
            Write(Path.Combine(directory, "browser-chain.json"), chain.Blocks.ToArray());
            Write(Path.Combine(directory, "browser-transaction.json"), transaction);
            File.WriteAllText(Path.Combine(directory, "browser-draft.json"), json.Serialize(new { Payload = draft.SigningPayload, InputAddresses = draft.InputAddresses }));
            Check(Crypto.Sha256(browser.PublicKey) == browser.Address, "browser RSA XML/address matches Core exactly");
            Check(transaction.Token.Supply == 9007199254740993L, "supply above JavaScript integer precision remains exact");
            return;
        }
        var externalChain = new Blockchain(Read<Block[]>(Path.Combine(directory, "browser-chain.json")));
        var externalTransaction = Read<Transaction>(Path.Combine(directory, "browser-transaction.json"));
        var browserSignatures = json.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, "browser-signatures.json")));
        for (int i = 0; i < browserSignatures.Length; i++) externalTransaction.Inputs[i].Signature = browserSignatures[i];
        externalTransaction.Id = externalTransaction.CalculateId();
        externalChain.ValidatePendingTransactions(new[] { externalTransaction });
        Check(externalChain.GetTokens().Count == 0, "Web Crypto signed creation passes consensus without confirming tokens");
        externalTransaction.Token.Name += "tampered"; externalTransaction.Id = externalTransaction.CalculateId();
        Reject(() => externalChain.ValidatePendingTransactions(new[] { externalTransaction }), "signed token metadata cannot be modified");
        Check(TokenAmounts.Parse("90071992.54740993", 8) == 9007199254740993L &&
            TokenAmounts.Parse("1,25", 2) == 125 && TokenAmounts.Format(long.MaxValue, 8) == "92233720368.54775807",
            "decimal conversion preserves exact 64-bit atomic values");
        Reject(() => TokenAmounts.Parse("0", 2), "zero supply rejected");
        Reject(() => TokenAmounts.Parse("1.001", 2), "excess fractional precision rejected");
        Reject(() => TokenAmounts.Parse("9223372036854775808", 0), "atomic overflow rejected");
        Reject(() => TokenAmounts.Parse("1e3", 0), "scientific notation rejected");

        using (var payer = new Wallet()) using (var vaWallet = new Wallet()) using (var vbWallet = new Wallet())
        {
            var chain = new Blockchain(); Block block;
            chain.TryAddWalletCreationReward(payer.CreateReceiveAddress(), out block);
            string va = vaWallet.CreateReceiveAddress(), vb = vbWallet.CreateReceiveAddress();
            chain.TryAddWalletCreationReward(va, out block); chain.TryAddWalletCreationReward(vb, out block);
            chain.AddBlock(new[] { vaWallet.CreateStakeLockTransaction(chain, none, va, Blockchain.OneCoin, 1),
                vbWallet.CreateStakeLockTransaction(chain, none, vb, Blockchain.OneCoin, 1) });
            var validators = new[] { vaWallet.CreateValidatorStake(va, Blockchain.OneCoin), vbWallet.CreateValidatorStake(vb, Blockchain.OneCoin) };
            string key = PublicKey(payer), recipient = payer.CreateReceiveAddress(), change = payer.CreateReceiveAddress();
            var draft = TokenCreationDraft.Prepare(chain, none, new[] { key }, "Meu token", "MTK", 2, 12500, recipient, change, 1);
            Reject(() => TokenCreationDraft.Prepare(chain, none, payer.ExportPrivateKeys(), "A", "A", 0, 1, recipient, change, 1), "server rejects private keys");
            var completed = draft.Complete(Sign(payer, draft.SigningPayload, draft.InputAddresses));
            chain.ValidatePendingTransactions(new[] { completed });
            completed.Token.Name = "changed copy";
            var original = draft.Complete(Sign(payer, draft.SigningPayload, draft.InputAddresses));
            Check(original.Token.Name == "Meu token", "completed transactions cannot mutate a stored draft");

            string cache = Path.Combine(Path.GetFullPath(directory), "DEXBlockchain.json");
            Write(cache, new NetworkSnapshot { NetworkId = Blockchain.NetworkId, Blocks = chain.Blocks.ToArray(), Pending = none });
            int servicePort = Port(), peerPort = Port();
            using (var service = new TokenNetworkService(servicePort, new string[0], cache))
            using (var peer = new PeerNode(peerPort, false))
            {
                var request = new PrepareTokenRequest { Name = "Meu token", Symbol = "mtk", Decimals = 2, Supply = "125",
                    PublicKeys = new[] { key }, Recipient = recipient, ChangeAddress = change };
                Reject(() => service.Prepare(request), "offline creation is refused");
                peer.SynchronizationRequested += (sender, eventArgs) => peer.BroadcastChainAsync(chain.Blocks);
                var received = new TaskCompletionSource<Transaction>();
                peer.TransactionReceived += (sender, eventArgs) => received.TrySetResult(eventArgs.Transaction);
                peer.Start(); await peer.ConnectAsync("127.0.0.1", servicePort);
                await Until(() => service.GetDashboard().ConnectedPeers == 1);
                var prepared = service.Prepare(request);
                var stale = service.Prepare(request);
                var submission = new SubmitTokenRequest { DraftId = prepared.DraftId,
                    Signatures = Sign(payer, prepared.Payload, prepared.InputAddresses) };
                string txId = await service.SubmitAsync(submission);
                await Until(() => received.Task.IsCompleted);
                Check(received.Task.Result.Id == txId && service.GetDashboard().Tokens.Single().Status == "Pendente",
                    "signed creation is persisted and broadcast as pending");
                Check(await service.SubmitAsync(submission) == txId && service.GetDashboard().Tokens.Length == 1,
                    "submission retry is idempotent");
                Reject(() => service.SubmitAsync(new SubmitTokenRequest { DraftId = stale.DraftId,
                    Signatures = Sign(payer, stale.Payload, stale.InputAddresses) }).GetAwaiter().GetResult(),
                    "competing draft cannot double-spend funding");
                Check(Read<NetworkSnapshot>(cache).Pending.Single().Id == txId, "pending queue survives in the public cache");
                chain.AddProofOfStakeBlock(new[] { received.Task.Result }, validators);
                await peer.BroadcastChainAsync(chain.Blocks);
                await Until(() => service.GetDashboard().Tokens.Single().Status == "Confirmado");
                var row = service.GetDashboard().Tokens.Single();
                Check(row.Id == prepared.TokenId && row.Supply == "125.00" && row.Height == chain.Blocks.Last().Height,
                    "validator-confirmed P2P block updates the token registry");
                Check(Read<NetworkSnapshot>(cache).Pending.Length == 0, "confirmed creation leaves the pending queue");
            }
            using (var restored = new TokenNetworkService(Port(), new string[0], cache))
                Check(restored.GetDashboard().Tokens.Single().Status == "Confirmado", "node restart restores and validates confirmed tokens");
        }
        Console.WriteLine(checks + " checks passed");
    }
}
