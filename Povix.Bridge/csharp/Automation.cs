using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PrivateCoin.Core;

namespace Povix.Bridge.CSharp {
    public sealed class Automation : IDisposable {
        readonly BridgeConfig config;
        readonly AutomationConfig settings;
        readonly bool coordinator;
        readonly Journal journal;
        string nativeAddress, evmAddress;
        public Automation(BridgeConfig config, AutomationConfig settings, bool coordinator) {
            this.config = config; this.settings = settings; this.coordinator = coordinator;
            journal = new Journal(Path.Combine(settings.State, coordinator ? "coordinator.journal" : "operator.journal"), config);
            try {
                journal.Record("automation:role", new JArray(coordinator ? "coordinator" : "operator", settings.Exchange, settings.StartBlock, settings.Releases));
                if (!coordinator) {
                    using (var rsa = OperatorKeys.LoadNative(settings.NativeKey)) nativeAddress = BridgeNative.Hash(rsa.ToXmlString(false));
                    int index = Array.IndexOf(config.Strings("nativeAdministrators"), nativeAddress);
                    if (index < 0 || settings.Releases && index >= config.Strings("operators").Length) throw new InvalidOperationException("Local native key is not a registered operator.");
                    if (settings.Releases) {
                        evmAddress = OperatorKeys.LoadEvm(settings.EvmKey).GetPublicAddress().ToLowerInvariant();
                        if (!string.Equals(config.Strings("operators")[index], evmAddress, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Native and EVM keys must belong to the same configured operator index.");
                    }
                } else if (settings.Releases && config.Strings("operators").Contains(OperatorKeys.LoadEvm(settings.RelayKey).GetPublicAddress(), StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Relay wallet must be separate from operator wallets.");
            } catch { journal.Dispose(); throw; }
        }
        string PathFor(string folder, string id) { return Path.Combine(settings.Exchange, folder, id + ".json"); }
        string SignaturePath(string id, string address) { return Path.Combine(settings.Exchange, "signatures", address.ToLowerInvariant(), id + ".json"); }
        public static string EnqueueBurn(BridgeConfig config, AutomationConfig settings, JObject binding) {
            var fields = new[] { "transactionId", "outputIndex", "chainId", "bridge", "recipient", "signatures" };
            if (binding.Properties().Any(p => !fields.Contains(p.Name))) throw new InvalidOperationException("Unexpected binding field; only public burn authorization data may enter the exchange.");
            var evidence = BridgeService.Burn(config, binding);
            if (System.Numerics.BigInteger.Parse((string)evidence["amount"]) > config.Amount("dailyReleaseCap")) throw new InvalidOperationException("Burn exceeds the daily cap; reconcile manually before queueing.");
            var job = AutomationFiles.Job(config, "burn", binding); string id = AutomationFiles.Id(job);
            AutomationFiles.Put(Path.Combine(settings.Exchange, "burns", id + ".json"), job); return id;
        }
        public async Task Tick() {
            var audit = await BridgeService.Audit(config);
            if (!(bool)audit["backed"] || (bool)audit["native"]["paused"] || (bool)audit["bnb"]["paused"]) throw new InvalidOperationException("Audit failed or bridge paused; no automatic signatures or transfers.");
            if (coordinator) {
                await GuardHistory();
                bool evmReady = !settings.Releases || await AutomationRpc.Reconcile(config, journal);
                await Mint();
                if (settings.Releases && evmReady) await Releases();
            } else await Operator();
        }
        async Task GuardHistory() {
            foreach (string key in new[] { "automation:source-anchor", "automation:relay-anchor" }) {
                var anchor = journal.Get(key) as JObject;
                if (anchor != null) {
                    var hash = await AutomationRpc.Agree(config, async e => { var block = await AutomationRpc.Rpc<JObject>(e, "eth_getBlockByNumber", AutomationRpc.Hex((long)anchor["number"]), false); return block == null ? JValue.CreateNull() : block["hash"]; });
                    if ((string)hash != (string)anchor["hash"]) throw new InvalidOperationException("Confirmed BNB " + (key.EndsWith("relay-anchor", StringComparison.Ordinal) ? "relay" : "scan") + " anchor changed; stop and reconcile the reorganization.");
                }
            }
            var completed = journal.Keys.Where(k => k.StartsWith("automation:mint-done:", StringComparison.Ordinal)).Select(k => journal.Get(k)).ToArray();
            var burns = journal.Keys.Where(k => k.StartsWith("automation:burn-evidence:", StringComparison.Ordinal)).Select(k => journal.Get(k)).ToArray();
            if (completed.Length != 0 || burns.Length != 0) foreach (var snapshot in config.Strings("nativeSnapshots")) {
                // LoadChain validates the entire canonical history. Index it once, rather than
                // rebuilding the full UTXO set separately for each historical deposit/burn.
                var chain = BridgeNative.LoadChain(snapshot);
                var operations = chain.Blocks.SelectMany(b => b.Transactions).Where(t => t.TokenCommand != null && t.TokenCommand.TokenId == config["nativeToken"]).ToArray();
                var claims = new HashSet<string>(operations.Where(t => t.TokenCommand.Operation == "mint" && t.TokenCommand.BridgeDepositId != null).Select(t => t.TokenCommand.BridgeDepositId), StringComparer.Ordinal);
                var canonicalBurns = new HashSet<string>(operations.Where(t => t.Kind == TransactionKind.TokenManage && t.TokenCommand.Operation == "burn").Select(t => "0x" + BridgeNative.Hash(config["nativeNetwork"] + "|usdt-burn|" + config["nativeToken"] + "|" + t.Id)), StringComparer.Ordinal);
                if (completed.Any(d => !claims.Contains(((string)d["depositId"]).Substring(2)))) throw new InvalidOperationException("Previously included native claim disappeared; stop and reconcile the reorganization.");
                if (burns.Any(b => !canonicalBurns.Contains((string)b["burnId"]))) throw new InvalidOperationException("Previously verified native burn disappeared; stop and reconcile the reorganization.");
            }
        }
        async Task Operator() {
            foreach (string path in AutomationFiles.List(Path.Combine(settings.Exchange, "requests"))) {
                try {
                    var job = AutomationFiles.Read(path); AutomationFiles.ValidateJob(config, path, job);
                    string kind = (string)job["kind"], id = AutomationFiles.Id(job), address = kind == "mint" ? nativeAddress : evmAddress;
                    if (kind != "mint" && kind != "release" || kind == "release" && !settings.Releases) continue;
                    var saved = journal.Get("automation:signed:" + id) as JObject;
                    if (saved == null) {
                        var body = (JObject)job["body"]; JObject signature;
                        if (kind == "mint") {
                            if ((string)body["kind"] != "mint") throw new InvalidOperationException("Automatic native signing is restricted to mint, never register/pause/resume.");
                            signature = await BridgeService.SignNative(config, body, settings.NativeKey, journal);
                        } else signature = await AutomationRpc.SignRelease(config, (JObject)body["binding"], (JObject)body["value"], settings.EvmKey, journal);
                        saved = new JObject { ["jobId"] = id, ["scope"] = AutomationFiles.Scope(config), ["signer"] = address, ["signature"] = signature };
                        journal.Record("automation:signed:" + id, saved);
                    }
                    AutomationFiles.Put(SignaturePath(id, address), saved);
                    Console.Error.WriteLine("Published " + kind + " signature: " + id);
                } catch (IOException) { throw; }
                catch (Exception error) { Console.Error.WriteLine("Operator rejected job " + Path.GetFileName(path) + ": " + error.Message); }
            }
        }
        JArray Signatures(string id, string[] members) {
            var result = new JArray();
            foreach (var address in members) {
                string file = SignaturePath(id, address);
                if (!File.Exists(file)) continue;
                var envelope = AutomationFiles.Read(file);
                if ((string)envelope["jobId"] != id || (string)envelope["scope"] != AutomationFiles.Scope(config) || !string.Equals((string)envelope["signer"], address, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Signature envelope identity mismatch.");
                result.Add(envelope["signature"].DeepClone());
            }
            return result;
        }
        static string DepositKey(JObject reference) { return BridgeNative.Hash(Native.Canonical(new JArray(reference["transactionHash"], reference["logIndex"]))); }
        async Task Mint() {
            var queue = journal.Get("automation:deposits") as JArray ?? new JArray();
            var pending = queue.OfType<JObject>().FirstOrDefault(r => journal.Get("automation:mint-done:" + DepositKey(r)) == null);
            if (pending == null) {
                long from = (long?)journal.Get("automation:cursor") ?? settings.StartBlock;
                long head = await AutomationRpc.ConfirmedHead(config); if (from > head) return;
                long to = Math.Min(head, from + 99);
                var logs = await AutomationRpc.Deposits(config, from, to);
                foreach (var log in logs.OfType<JObject>()) {
                    if ((bool)log["removed"]) throw new InvalidOperationException("Removed deposit in confirmed scan.");
                    var reference = new JObject { ["transactionHash"] = log["transactionHash"], ["logIndex"] = log["logIndex"] };
                    if (!queue.OfType<JObject>().Any(r => DepositKey(r) == DepositKey(reference))) queue.Add(reference);
                }
                if (queue.Count > 1000) throw new InvalidOperationException("Archive completed automation history before scanning more deposits.");
                var hash = await AutomationRpc.Agree(config, async e => (await AutomationRpc.Rpc<JObject>(e, "eth_getBlockByNumber", AutomationRpc.Hex(to), false))["hash"]);
                journal.Replace("automation:deposits", queue);
                journal.Replace("automation:source-anchor", new JObject { ["number"] = to, ["hash"] = hash });
                journal.Replace("automation:cursor", new JValue(to + 1));
                pending = queue.OfType<JObject>().FirstOrDefault(r => journal.Get("automation:mint-done:" + DepositKey(r)) == null);
                if (pending == null) return;
            }
            // Always recheck canonical source evidence, including before recognizing a completed claim.
            var evidence = await Evm.VerifyDeposit(config, pending); string key = DepositKey(pending);
            var chains = config.Strings("nativeSnapshots").Select(BridgeNative.LoadChain).ToArray();
            string claim = ((string)evidence["depositId"]).Substring(2);
            var claimed = chains.Select(c => c.HasBridgeDeposit(config["nativeToken"], claim)).ToArray();
            if (claimed.Any(v => v)) {
                if (!claimed.All(v => v)) return;
                foreach (var chain in chains) {
                    var tx = chain.Blocks.SelectMany(b => b.Transactions).Single(t => t.TokenCommand != null && t.TokenCommand.TokenId == config["nativeToken"] && t.TokenCommand.BridgeDepositId == claim);
                    if (tx.TokenCommand.Amount.ToString(CultureInfo.InvariantCulture) != (string)evidence["amount"] || tx.TokenCommand.BridgeBlockHash != ((string)evidence["blockHash"]).Substring(2) || !tx.Outputs.Any(o => o.AssetId == config["nativeToken"] && o.OneTimeAddress == (string)evidence["recipient"] && o.Amount == tx.TokenCommand.Amount)) throw new InvalidOperationException("Confirmed claim differs from source deposit.");
                }
                journal.Record("automation:mint-done:" + key, evidence); Console.Error.WriteLine("Mint included in both native snapshots: " + (string)evidence["depositId"]); return;
            }
            var proposal = await BridgeService.Propose(config, "mint", pending, journal);
            string transactionKey = "automation:native-tx:" + key;
            var transaction = journal.Get(transactionKey) as JObject;
            long revision = (long)Native.Review(config, "production-status", new JObject())["revision"];
            if ((long)proposal["draft"]["TokenCommand"]["Revision"] != revision || transaction == null && DateTime.UtcNow.Ticks - (long)proposal["draft"]["TimestampUtcTicks"] > TimeSpan.FromMinutes(8).Ticks) {
                proposal = await BridgeService.RefreshExpired(config, proposal, journal); transaction = null; journal.Replace(transactionKey, JValue.CreateNull());
            }
            if (transaction == null) {
                var job = AutomationFiles.Job(config, "mint", proposal); string id = AutomationFiles.Id(job);
                AutomationFiles.Put(PathFor("requests", id), job);
                string[] selected = proposal["draft"]["TokenAuthorizations"].Select(p => BridgeNative.Hash((string)p["PublicKey"])).ToArray();
                var signatures = Signatures(id, selected); if (signatures.Count != selected.Length) { Console.Error.WriteLine("Awaiting native signatures " + signatures.Count + "/" + selected.Length + ": " + id); return; }
                transaction = (JObject)await BridgeService.Complete(config, proposal, signatures);
                journal.Replace(transactionKey, transaction); // Durable BEFORE contacting native peers.
            }
            await Broadcast(transaction);
        }
        async Task Broadcast(JObject transaction) {
            Transaction tx;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(Native.Canonical(transaction)))) tx = (Transaction)new DataContractJsonSerializer(typeof(Transaction)).ReadObject(stream);
            // Only rebroadcast this persisted transaction; no new IDs on transient delivery failures.
            foreach (var path in config.Strings("nativeSnapshots")) BridgeNative.LoadChain(path).ValidatePendingTransactions(new[] { tx });
            await AutomationNative.Deliver(settings.Peers, tx); Console.Error.WriteLine("Native transaction broadcast; awaiting snapshots: " + tx.Id);
        }
        async Task Releases() {
            foreach (string file in AutomationFiles.List(Path.Combine(settings.Exchange, "burns"))) {
                JObject request;
                try { request = AutomationFiles.Read(file); AutomationFiles.ValidateJob(config, file, request); if ((string)request["kind"] != "burn") throw new InvalidOperationException("Not a burn request."); }
                catch (IOException) { throw; } catch (Exception e) { Console.Error.WriteLine("Rejected burn request: " + e.Message); continue; }
                var binding = (JObject)request["body"]; string requestId = AutomationFiles.Id(request);
                if (journal.Get("automation:burn-done:" + requestId) != null) continue;
                JToken evidence;
                try { evidence = BridgeService.Burn(config, binding); }
                catch (IOException) { throw; }
                catch (Exception e) { Console.Error.WriteLine("Burn not eligible for release: " + e.Message); continue; }
                string burnId = (string)evidence["depositId"];
                journal.Record("automation:burn-evidence:" + burnId, new JObject { ["burnId"] = burnId, ["transactionId"] = binding["transactionId"], ["recipient"] = evidence["recipient"], ["amount"] = evidence["amount"] });
                var state = await AutomationRpc.ReleaseState(config, burnId);
                if ((bool)state["processed"]) { journal.Record("automation:burn-done:" + requestId, evidence); continue; }
                if ((string)state["executeAfter"] != "0") {
                    if ((string)state["recipient"] != (string)evidence["recipient"] || (string)state["amount"] != (string)evidence["amount"]) throw new InvalidOperationException("Queued release differs from verified burn.");
                    var times = await Task.WhenAll(config.Strings("rpcUrls").Select(async url => AutomationRpc.Number((await AutomationRpc.Rpc<JObject>(new Evm(config, url), "eth_getBlockByNumber", "latest", false))["timestamp"])));
                    if (times.Min() < long.Parse((string)state["executeAfter"], CultureInfo.InvariantCulture)) continue;
                    var e = new Evm(config); long day = times.Min() / 86400;
                    var used = await e.Call<System.Numerics.BigInteger>(e.Vault, "releasedPerDay", day);
                    if (used + System.Numerics.BigInteger.Parse((string)evidence["amount"]) > config.Amount("dailyReleaseCap")) continue;
                    await AutomationRpc.Send(config, settings, journal, "execute:" + burnId, "executeRelease", Native.Bytes(burnId)); return;
                }
                if ((string)state["nonce"] != "0") { Console.Error.WriteLine("Release previously queued/cancelled; manual reconciliation required: " + burnId); continue; }
                string valueKey = "automation:release-value:" + requestId;
                var value = journal.Get(valueKey) as JObject;
                if (value == null || (long)value["deadline"] < DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()) {
                    value = await AutomationRpc.ReleaseValue(config, binding); journal.Replace(valueKey, value);
                }
                var job = AutomationFiles.Job(config, "release", new JObject { ["binding"] = binding, ["value"] = value }); string id = AutomationFiles.Id(job);
                AutomationFiles.Put(PathFor("requests", id), job);
                var signatures = Signatures(id, config.Strings("operators")); if (signatures.Count < config.Number("threshold")) continue;
                BridgeService.Deadline(value); var proofs = Evm.Signatures(config, "Release", value, signatures);
                await AutomationRpc.Send(config, settings, journal, "queue:" + burnId, "release", Native.Bytes(burnId), (string)value["recipient"], System.Numerics.BigInteger.Parse((string)value["nativeAmount"]), (long)value["deadline"], proofs); return;
            }
        }
        public static async Task Run(BridgeConfig config, AutomationConfig settings, bool coordinator, bool once) {
            using (var worker = new Automation(config, settings, coordinator)) {
                bool stop = false; ConsoleCancelEventHandler handler = (s, e) => { e.Cancel = true; stop = true; }; Console.CancelKeyPress += handler;
                try {
                    do {
                        try { await worker.Tick(); }
                        catch (IOException) { throw; } // Never continue signing after a journal/filesystem failure.
                        catch (Exception error) { if (once) throw; Console.Error.WriteLine("Automation blocked: " + error.Message); }
                        if (once || stop) break;
                        for (int i = 0; i < settings.PollSeconds && !stop; i++) await Task.Delay(1000);
                    } while (!stop);
                } finally { Console.CancelKeyPress -= handler; }
            }
        }
        public void Dispose() { journal.Dispose(); }
    }
}
