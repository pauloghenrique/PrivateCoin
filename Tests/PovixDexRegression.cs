using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using System.Web.SessionState;
using System.Web.Script.Serialization;
using Povix.Dex.Controllers;
using Povix.Dex.Models;
using Povix.Dex.Services;
using PrivateCoin.Core;

internal static partial class PovixDexRegression
{
    private static int checks;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private const string Password = "dex-test-password-only";
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Wait(Func<bool> ready, string name) { var timer = Stopwatch.StartNew(); while (!ready() && timer.ElapsedMilliseconds < 15000) Thread.Sleep(25); Check(ready(), name); }
    private static string[] PublicKeys(Wallet wallet) => wallet.ExportPrivateKeys().Select(xml => {
        using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); return rsa.ToXmlString(false); }
    }).ToArray();
    private static byte[] Random(int count) { byte[] value = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(value); return value; }
    private static byte[] Combined(params byte[][] parts) => parts.SelectMany(value => value).ToArray();
    private static object EncryptedWallet(Wallet wallet)
    {
        byte[] salt = Random(16), iv = Random(16), key;
        using (var derivation = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(Password), salt, 210000, HashAlgorithmName.SHA256)) key = derivation.GetBytes(64);
        byte[] cipher;
        using (var aes = Aes.Create()) {
            aes.Key = key.Take(32).ToArray(); aes.IV = iv;
            byte[] clear = Encoding.UTF8.GetBytes(Json.Serialize(new { Name = "Carteira de teste", PrivateKeys = wallet.ExportPrivateKeys() }));
            using (var encryptor = aes.CreateEncryptor()) cipher = encryptor.TransformFinalBlock(clear, 0, clear.Length);
        }
        byte[] tag; using (var hmac = new HMACSHA256(key.Skip(32).ToArray())) tag = hmac.ComputeHash(Combined(salt, iv, cipher));
        return new { format = "povix-dex-wallet-v1", iterations = 210000, salt = Convert.ToBase64String(salt), iv = Convert.ToBase64String(iv), data = Convert.ToBase64String(cipher), hmac = Convert.ToBase64String(tag) };
    }
    private static string[] DecryptWallet(Dictionary<string, object> record)
    {
        byte[] salt = Convert.FromBase64String((string)record["salt"]), iv = Convert.FromBase64String((string)record["iv"]), cipher = Convert.FromBase64String((string)record["data"]), key;
        using (var derivation = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(Password), salt, 210000, HashAlgorithmName.SHA256)) key = derivation.GetBytes(64);
        using (var hmac = new HMACSHA256(key.Skip(32).ToArray())) Check(hmac.ComputeHash(Combined(salt, iv, cipher)).SequenceEqual(Convert.FromBase64String((string)record["hmac"])), "browser backup HMAC interoperates with .NET");
        using (var aes = Aes.Create()) {
            aes.Key = key.Take(32).ToArray(); aes.IV = iv;
            using (var decryptor = aes.CreateDecryptor()) {
                var data = Json.DeserializeObject(Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length))) as Dictionary<string, object>;
                return ((object[])data["PrivateKeys"]).Cast<string>().ToArray();
            }
        }
    }
    public static int Main(string[] args)
    {
        try { Run(args[0], args[1]); Console.WriteLine(checks + " DEX checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run(string directory, string nodeExecutable)
    {
        Directory.CreateDirectory(directory);
        long atomic;
        Check(CreateTokenViewModel.TryParseSupply("92233720368,54775807", 8, out atomic) && atomic == long.MaxValue, "maximum supply converted exactly");
        Check(!CreateTokenViewModel.TryParseSupply("92233720368,54775808", 8, out atomic) &&
            !CreateTokenViewModel.TryParseSupply("1.001", 2, out atomic) &&
            !CreateTokenViewModel.TryParseSupply("0", 8, out atomic), "overflow, extra precision and zero rejected");
        using (var issuer = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain();
            LegacyConsensusFixture.Fund(chain, issuer.CreateReceiveAddress());
            string destination = issuer.CreateReceiveAddress(), change = issuer.OwnedOneTimeAddresses.First();
            string[] originalAddresses = issuer.OwnedOneTimeAddresses.ToArray();
            long originalBalance = chain.GetBalance(originalAddresses);
            string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
            LegacyConsensusFixture.Fund(chain, firstAddress); LegacyConsensusFixture.Fund(chain, secondAddress);
            LegacyConsensusFixture.SelfBlock(chain, first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, Blockchain.OneCoin, 0));
            LegacyConsensusFixture.SelfBlock(chain, second.CreateStakeLockTransaction(chain, new Transaction[0], secondAddress, Blockchain.OneCoin, 0));
            Check(chain.IsValid(), "existing test chain contains confirmed funds and validator collateral");
            using (var batches = new ValidationBatchFixture(chain))
            {
            var commonChain = chain.Blocks.ToArray();
            var model = new CreateTokenViewModel { Name = "Aurora | edição 🌙", Symbol = "AUR", Decimals = 8, Supply = "92233720368,54775807", DestinationAddress = destination, FeePriority = 2 };
            int peerPort = Port(), dexPort = Port();
            using (var peer = new PeerNode(peerPort))
            {
                Transaction received = null;
                peer.SynchronizationRequested += (s, e) => peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                peer.TransactionReceived += (s, e) => { if (e.Transaction.Kind == TransactionKind.TokenCreate) Interlocked.Exchange(ref received, e.Transaction); };
                peer.Start();
                var service = new TokenNetworkService(directory, dexPort, new string[0]);
                try
                {
                    bool blocked = false;
                    try { service.Prepare(model, PublicKeys(issuer), change, "regression"); } catch (InvalidOperationException) { blocked = true; }
                    Check(blocked, "offline creation blocked");
                    peer.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult();
                    peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().CanCreate, "DEX synchronizes through an existing peer");
                    var sessionItems = new SessionStateItemCollection();
                    var controller = ControllerFor(service, "regression", sessionItems);
                    object prepared = ((JsonResult)controller.Prepare(model, PublicKeys(issuer).Select(key =>
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(key))).ToArray(), change)).Data;
                    Check(sessionItems.Dirty && sessionItems.Count > 0,
                        "MVC preparation persists session state so ASP.NET retains the owner cookie");
                    var draft = Json.DeserializeObject(Json.Serialize(prepared)) as Dictionary<string, object>;
                    long fee = long.Parse((string)draft["feeAtomic"]), expectedChange = originalBalance - fee;
                    Check((string)draft["changeAddress"] == change && long.Parse((string)draft["changeAtomic"]) == expectedChange,
                        "review returns the full native balance minus only the fee to an original wallet address");
                    File.WriteAllText(Path.Combine(directory, "fixture.json"), Json.Serialize(new { wallet = EncryptedWallet(issuer), draft = prepared,
                        expected = new { Name = model.Name, Symbol = model.Symbol, Decimals = "8", Supply = model.Supply, DestinationAddress = destination },
                        networkId = Blockchain.NetworkId, changeAddress = change }));
                    var start = new ProcessStartInfo(nodeExecutable) { UseShellExecute = false };
                    start.Arguments = "Tests/PovixDexWalletRegression.js " + directory;
                    using (var process = Process.Start(start)) { process.WaitForExit(); Check(process.ExitCode == 0, "browser review and signing regression"); }
                    var result = Json.DeserializeObject(File.ReadAllText(Path.Combine(directory, "browser-result.json"))) as Dictionary<string, object>;
                    string[] signatures = ((object[])result["signatures"]).Cast<string>().ToArray();
                    CheckRejection(ControllerFor(service, "another-session", new SessionStateItemCollection()),
                        (string)draft["draftId"], signatures, "draft_session_changed");
                    controller = ControllerFor(service, "regression", sessionItems);
                    CheckRejection(controller, Guid.NewGuid().ToString("N"), signatures, "draft_missing");
                    CheckRejection(controller, (string)draft["draftId"], signatures.Concat(signatures).ToArray(), "signatures_invalid");
                    CheckRejection(controller, (string)draft["draftId"], new[] { Convert.ToBase64String(new byte[256]) }, "signature_invalid");
                    IDictionary savedDrafts = (IDictionary)typeof(TokenNetworkService).GetField("drafts",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(service);
                    object expiringDraft = savedDrafts[(string)draft["draftId"]];
                    var expiry = expiringDraft.GetType().GetProperty("ExpiresUtc");
                    DateTime originalExpiry = (DateTime)expiry.GetValue(expiringDraft, null);
                    expiry.SetValue(expiringDraft, DateTime.UtcNow.AddSeconds(-1), null);
                    CheckRejection(controller, (string)draft["draftId"], signatures, "draft_expired");
                    expiry.SetValue(expiringDraft, originalExpiry, null);
                    var conflictingDraft = Json.DeserializeObject(Json.Serialize(service.Prepare(model, PublicKeys(issuer), change, "regression"))) as Dictionary<string, object>;
                    string[] conflictingSignatures = SignPrepared(conflictingDraft, issuer);
                    controller = ControllerFor(service, "regression", sessionItems);
                    var accepted = SubmitResult(controller, (string)draft["draftId"], signatures);
                    string id = (string)accepted["transactionId"];
                    Check(controller.Response.StatusCode == 200 && ((string)accepted["receiptUrl"]).EndsWith(id),
                        "a second MVC request in the saved session submits the browser signature and returns its receipt");
                    Wait(() => received != null && received.Id == id, "locally signed token creation propagates over P2P");
                    Check(service.GetRegistration(id).Status == "pending" && chain.GetTokens().Count == 0, "pending receipt does not claim blockchain confirmation");
                    Check(service.GetBalance(issuer.OwnedOneTimeAddresses.ToArray()) == 0, "pending funding output is reserved");
                    var pendingBalance = BalanceResult(controller, originalAddresses);
                    Check((string)pendingBalance["balanceAtomic"] == "0" && long.Parse((string)pendingBalance["confirmedAtomic"]) == originalBalance &&
                        long.Parse((string)pendingBalance["reservedAtomic"]) == originalBalance && long.Parse((string)pendingBalance["pendingIncomingAtomic"]) == expectedChange,
                        "MVC balance distinguishes the whole reserved UTXO from the pending change and the fee");
                    var povixOutputs = service.GetRegistration(id).PovixOutputs;
                    Check(povixOutputs.Length == 1 && povixOutputs[0].OneTimeAddress == change && povixOutputs[0].Amount == expectedChange,
                        "receipt exposes the actual native change amount and its original wallet address");
                    Check(service.SubmitAsync((string)draft["draftId"], signatures, "regression").GetAwaiter().GetResult() == id, "submission retry is idempotent");
                    CheckRejection(controller, (string)conflictingDraft["draftId"], conflictingSignatures, "funding_unavailable");
                    string state = File.ReadAllText(Path.Combine(directory, "dex-network.json"));
                    Check(!state.Contains("RSAKeyValue") || (!state.Contains("<D>") && !state.Contains("<P>")), "persisted network state contains no private keys");
                    service.Dispose();
                    var legacyCache = (Dictionary<string, object>)Json.DeserializeObject(state);
                    legacyCache["ConsensusVersion"] = 3;
                    File.WriteAllText(Path.Combine(directory, "dex-network.json"), Json.Serialize(legacyCache));
                    service = new TokenNetworkService(directory, dexPort, new string[0]);
                    Check(service.GetRegistration(id).Status == "pending" && !service.GetNetwork().CanCreate, "v3 cache preserves pending creation while synchronization is required again");
                    service.Dispose();
                    legacyCache["ConsensusVersion"] = 4;
                    File.WriteAllText(Path.Combine(directory, "dex-network.json"), Json.Serialize(legacyCache));
                    service = new TokenNetworkService(directory, dexPort, new string[0]);
                    Check(service.GetRegistration(id).Status == "pending" && !service.GetNetwork().CanCreate, "v4 cache also preserves pending creation during the v10 upgrade");
                    service.Dispose();
                    legacyCache["ConsensusVersion"] = 7;
                    File.WriteAllText(Path.Combine(directory, "dex-network.json"), Json.Serialize(legacyCache));
                    service = new TokenNetworkService(directory, dexPort, new string[0]);
                    Check(service.GetRegistration(id).Status == "pending" && !service.GetNetwork().CanCreate, "v7 cache preserves pending creation during the v10 upgrade");
                    service.Dispose();
                    legacyCache["ConsensusVersion"] = 8;
                    File.WriteAllText(Path.Combine(directory, "dex-network.json"), Json.Serialize(legacyCache));
                    service = new TokenNetworkService(directory, dexPort, new string[0]);
                    Check(service.GetRegistration(id).Status == "pending" && !service.GetNetwork().CanCreate, "v8 cache preserves pending creation during the v10 upgrade");
                    service.Dispose();
                    legacyCache["ConsensusVersion"] = 9;
                    File.WriteAllText(Path.Combine(directory, "dex-network.json"), Json.Serialize(legacyCache));
                    service = new TokenNetworkService(directory, dexPort, new string[0]);
                    Check(service.GetRegistration(id).Status == "pending" && !service.GetNetwork().CanCreate, "v9 cache preserves pending creation during the v10 upgrade");
                    controller = ControllerFor(service, "regression", sessionItems);
                    CheckRejection(controller, (string)conflictingDraft["draftId"], conflictingSignatures, "network_not_ready", 503);
                    Check((string)SubmitResult(ControllerFor(service, "regression", sessionItems), (string)draft["draftId"], signatures)["transactionId"] == id,
                        "an accepted submission can recover its receipt after restart while the network reconnects");
                    peer.ConnectAsync("127.0.0.1", dexPort).GetAwaiter().GetResult();
                    peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetNetwork().CanCreate, "restarted DEX resynchronizes");
                    CheckRejection(ControllerFor(service, "regression", sessionItems), (string)conflictingDraft["draftId"], conflictingSignatures, "draft_missing");
                    chain.ValidatePendingTransactions(new[] { received });
                    var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.OneCoin), second.CreateValidatorStake(secondAddress, Blockchain.OneCoin) };
                    Block confirmed = batches.Confirm(chain, new[] { received }, validators);
                    Check(chain.IsValid() && chain.GetTokenBalance(new[] { destination }, (string)draft["tokenId"]) == long.MaxValue, "validators confirm exact token supply in a valid block");
                    peer.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetRegistration(id).Status == "confirmed", "receipt confirms only after receiving a validated block");
                    Check(service.GetRegistration(id).BlockHash == confirmed.Hash && service.GetRegistration(id).Confirmations == 1, "receipt exposes real block hash and confirmations");
                    Check(chain.GetBalance(originalAddresses) == expectedChange && service.GetBalance(originalAddresses) == expectedChange,
                        "Desktop wallet addresses and DEX retain exactly the original POVIX balance minus the fee after confirmation");
                    var confirmedBalance = BalanceResult(ControllerFor(service, "regression", sessionItems), originalAddresses);
                    Check(long.Parse((string)confirmedBalance["balanceAtomic"]) == expectedChange && long.Parse((string)confirmedBalance["confirmedAtomic"]) == expectedChange &&
                        (string)confirmedBalance["reservedAtomic"] == "0" && (string)confirmedBalance["pendingIncomingAtomic"] == "0",
                        "confirmed change becomes spendable without changing the wallet keys or counting token supply as POVIX");
                    string[] updatedKeys = DecryptWallet((Dictionary<string, object>)result["encryptedWallet"]);
                    using (var restored = Wallet.FromPrivateKeys(updatedKeys)) Check(restored.OwnedOneTimeAddresses.Contains((string)result["newAddress"]), "locally generated browser address restores in the Core");
                    RunTransfers(directory, nodeExecutable, issuer, chain, service, peer, validators, batches);
                    var fork = new Blockchain(commonChain);
                    while (fork.ChainWork <= chain.ChainWork)
                        LegacyConsensusFixture.Fund(fork, "reorganization-fixture-" + fork.Blocks.Count);
                    peer.BroadcastChainAsync(fork.Blocks).GetAwaiter().GetResult();
                    Wait(() => service.GetRegistration(id).Status == "pending", "greater-work reorganization returns an orphaned valid creation to the queue");
                    Check(service.GetRegistration(id).Confirmations == 0 && service.GetRegistration(id).BlockHash == null,
                        "the receipt loses its old block and confirmations after reorganization");
                    service.Dispose();
                    service = new TokenNetworkService(directory, dexPort, new string[0]);
                    Check(service.GetRegistration(id).Status == "pending" && service.GetRegistration(id).Confirmations == 0 && !service.GetNetwork().CanCreate,
                        "restart preserves the adopted fork and pending receipt while awaiting synchronization");
                }
                finally { service.Dispose(); }
            }
            }
        }
    }

    private static Dictionary<string, object> SubmitResult(TokensController controller, string draftId, string[] signatures)
        => Json.DeserializeObject(Json.Serialize(((JsonResult)controller.Submit(draftId, signatures).GetAwaiter().GetResult()).Data)) as Dictionary<string, object>;

    private static Dictionary<string, object> BalanceResult(TokensController controller, string[] addresses)
        => Json.DeserializeObject(Json.Serialize(((JsonResult)controller.Balance(addresses)).Data)) as Dictionary<string, object>;

    private static void CheckRejection(TokensController controller, string draftId, string[] signatures, string code, int status = 400)
    {
        var result = SubmitResult(controller, draftId, signatures);
        Check(controller.Response.StatusCode == status && (string)result["code"] == code && !result.ContainsKey("transactionId"),
            "MVC rejection identifies " + code + " without claiming acceptance");
    }

    private static string[] SignPrepared(Dictionary<string, object> draft, Wallet wallet)
    {
        var keys = wallet.ExportPrivateKeys().ToDictionary(xml => {
            using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml);
                using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(rsa.ToXmlString(false)))).Replace("-", "").ToLowerInvariant();
            }
        }, xml => xml);
        byte[] payload = Convert.FromBase64String((string)draft["signingPayload"]);
        return ((object[])draft["inputAddresses"]).Cast<string>().Select(address => {
            using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(keys[address]);
                return Convert.ToBase64String(rsa.SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
            }
        }).ToArray();
    }

    private static TokensController ControllerFor(TokenNetworkService service, string sessionId, SessionStateItemCollection items)
    {
        typeof(Povix.Dex.MvcApplication).GetProperty("TokenNetwork").SetValue(null, service, null);
        var context = new HttpContext(new HttpRequest("", "http://localhost/", ""), new HttpResponse(new StringWriter()));
        SessionStateUtility.AddHttpSessionStateToContext(context, new HttpSessionStateContainer(sessionId,
            items, new HttpStaticObjectsCollection(), 20, true, HttpCookieMode.UseCookies, SessionStateMode.InProc, false));
        var testContext = new TestContext(new HttpSessionStateWrapper(context.Session));
        var controller = new TokensController();
        var request = new RequestContext(testContext, new RouteData());
        controller.ControllerContext = new ControllerContext(request, controller);
        var routes = new RouteCollection();
        Povix.Dex.RouteConfig.RegisterRoutes(routes);
        controller.Url = new UrlHelper(request, routes);
        return controller;
    }

    private sealed class TestContext : HttpContextBase
    {
        private readonly HttpSessionStateBase session;
        private readonly IDictionary items = new Hashtable();
        private readonly HttpResponseBase response = new TestResponse();
        private readonly HttpRequestBase request = new TestRequest();
        public TestContext(HttpSessionStateBase session) { this.session = session; }
        public override HttpSessionStateBase Session => session;
        public override HttpResponseBase Response => response;
        public override HttpRequestBase Request => request;
        public override IDictionary Items => items;
        public override object GetService(Type serviceType) => null;
    }
    private sealed class TestRequest : HttpRequestBase
    {
        public override string ApplicationPath => "/";
        public override string AppRelativeCurrentExecutionFilePath => "~/";
        public override string PathInfo => "";
        public override string RawUrl => "/";
        public override Uri Url => new Uri("http://localhost/");
        public override NameValueCollection ServerVariables => new NameValueCollection();
    }
    private sealed class TestResponse : HttpResponseBase
    {
        public override int StatusCode { get; set; } = 200;
        public override bool TrySkipIisCustomErrors { get; set; }
        public override HttpCachePolicyBase Cache { get; } = new TestCache();
        public override string ApplyAppPathModifier(string path) => path;
    }
    private sealed class TestCache : HttpCachePolicyBase
    {
        public override void SetCacheability(HttpCacheability cacheability) { }
        public override void SetNoStore() { }
    }
}
