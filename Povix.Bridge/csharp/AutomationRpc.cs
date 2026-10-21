using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Hex.HexTypes;

namespace Povix.Bridge.CSharp {
    public static class AutomationRpc {
        public static async Task<T> Rpc<T>(Evm evm, string method, params object[] args) {
            var task = evm.Web3.Client.SendRequestAsync<T>(method, null, args);
            if (await Task.WhenAny(task, Task.Delay(30000)) != task) throw new TimeoutException("Automation RPC timed out."); return await task;
        }
        public static string Hex(long number) { return new HexBigInteger(number).HexValue; }
        public static long Number(JToken value) { var n = new HexBigInteger((string)value).Value; if (n < 0 || n > long.MaxValue) throw new InvalidOperationException("RPC integer outside supported range."); return (long)n; }
        public static async Task<JToken> Agree(BridgeConfig c, Func<Evm, Task<JToken>> read) {
            var values = await Task.WhenAll(c.Strings("rpcUrls").Select(url => read(new Evm(c, url))));
            if (values.Any(v => Native.Canonical(v) != Native.Canonical(values[0]))) throw new InvalidOperationException("Independent providers disagree; automation stopped."); return values[0];
        }
        public static async Task<long> ConfirmedHead(BridgeConfig c) {
            var heads = await Task.WhenAll(c.Strings("rpcUrls").Select(url => Rpc<JToken>(new Evm(c, url), "eth_blockNumber")));
            long confirmed = heads.Select(Number).Min() - c.Number("bscConfirmations") + 1; return confirmed;
        }
        public static async Task<JArray> Deposits(BridgeConfig c, long from, long to) {
            string topic = Native.Keccak(System.Text.Encoding.UTF8.GetBytes("Deposited(bytes32,address,bytes32,uint256,uint256)"));
            return (JArray)await Agree(c, async e => {
                var logs = await Rpc<JArray>(e, "eth_getLogs", new JObject { ["address"] = c["bridge"], ["fromBlock"] = Hex(from), ["toBlock"] = Hex(to), ["topics"] = new JArray(topic) });
                return new JArray(logs.Select(l => new JObject { ["transactionHash"] = ((string)l["transactionHash"]).ToLowerInvariant(), ["logIndex"] = Number(l["logIndex"]), ["blockHash"] = ((string)l["blockHash"]).ToLowerInvariant(), ["removed"] = (bool?)l["removed"] == true }).OrderBy(l => (string)l["transactionHash"]).ThenBy(l => (long)l["logIndex"]));
            });
        }
        public static async Task<JObject> ReleaseState(BridgeConfig c, string burnId) {
            byte[] id = Native.Bytes(burnId);
            return (JObject)await Agree(c, async e => {
                var queued = await Rpc<JToken>(e, "eth_call", new JObject { ["to"] = c["bridge"], ["data"] = "0x" + Nethereum.Util.Sha3Keccack.Current.CalculateHash("pendingReleases(bytes32)").Substring(0, 8) + burnId.Substring(2) }, "latest");
                byte[] bytes = Native.Bytes((string)queued); if (bytes.Length != 96) throw new InvalidOperationException("Invalid pending-release ABI.");
                Func<int, BigInteger> word = n => new BigInteger(bytes.Skip(n * 32).Take(32).Reverse().Concat(new byte[] { 0 }).ToArray());
                return new JObject { ["processed"] = await e.Call<bool>(e.Vault, "processedBurns", id), ["nonce"] = (await e.Call<BigInteger>(e.Vault, "releaseNonces", id)).ToString(),
                    ["recipient"] = "0x" + bytes.Skip(12).Take(20).ToArray().ToHexLower(), ["amount"] = word(1).ToString(), ["executeAfter"] = word(2).ToString() };
            });
        }
        public static async Task<JObject> ReleaseValue(BridgeConfig c, JObject binding) {
            var evidence = BridgeService.Burn(c, binding); var state = await ReleaseState(c, (string)evidence["depositId"]);
            if ((bool)state["processed"] || (string)state["executeAfter"] != "0" || (string)state["nonce"] != "0") throw new InvalidOperationException("Burn processed, queued or previously cancelled; do not automatically reauthorize it.");
            return new JObject { ["burnId"] = evidence["depositId"], ["recipient"] = evidence["recipient"], ["nativeAmount"] = evidence["amount"], ["nonce"] = state["nonce"], ["deadline"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() };
        }
        public static async Task<JObject> SignRelease(BridgeConfig c, JObject binding, JObject value, string keyFile, Journal journal) {
            await Evm.CheckAll(c); var evidence = BridgeService.Burn(c, binding); var state = await ReleaseState(c, (string)evidence["depositId"]); BridgeService.Deadline(value);
            if ((bool)state["processed"] || (string)state["executeAfter"] != "0" || (string)state["nonce"] != "0" || (string)value["nonce"] != (string)state["nonce"] || (string)value["burnId"] != (string)evidence["depositId"] || (string)value["recipient"] != (string)evidence["recipient"] || (string)value["nativeAmount"] != (string)evidence["amount"])
                throw new InvalidOperationException("Release authorization differs from independently verified burn/state.");
            if (System.Numerics.BigInteger.Parse((string)evidence["amount"]) > c.Amount("dailyReleaseCap")) throw new InvalidOperationException("Burn exceeds the daily cap; manual reconciliation required.");
            var key = OperatorKeys.LoadEvm(keyFile);
            journal.Record("release:" + (string)value["burnId"], new JObject { ["recipient"] = value["recipient"], ["nativeAmount"] = value["nativeAmount"] });
            return new JObject { ["value"] = value.DeepClone(), ["signature"] = Evm.Sign(c, "Release", value, key) };
        }
        // Persist the signed raw transaction/hash BEFORE broadcast. At most one EVM nonce in flight.
        public static async Task<bool> Reconcile(BridgeConfig c, Journal db) {
            var pending = db.Get("automation:pending-evm") as JObject; if (pending == null) return true;
            var receipts = await Task.WhenAll(c.Strings("rpcUrls").Select(url => Rpc<JToken>(new Evm(c, url), "eth_getTransactionReceipt", (string)pending["hash"])));
            if (receipts.All(r => r == null || r.Type == JTokenType.Null)) {
                var e = new Evm(c);
                var known = await Rpc<JToken>(e, "eth_getTransactionByHash", (string)pending["hash"]);
                if (known == null || known.Type == JTokenType.Null) {
                    string returned = await Rpc<string>(e, "eth_sendRawTransaction", (string)pending["raw"]);
                    if (!string.Equals(returned, (string)pending["hash"], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("RPC returned a different transaction hash.");
                }
                return false;
            }
            if (receipts.Any(r => r == null || r.Type == JTokenType.Null)) return false;
            Func<JToken, string> facts = r => Native.Canonical(new JArray(r["transactionHash"], r["blockHash"], r["blockNumber"], r["status"]));
            if (receipts.Any(r => facts(r) != facts(receipts[0]))) throw new InvalidOperationException("Providers disagree about relay receipt.");
            var receipt = receipts[0];
            if (Number(receipt["blockNumber"]) > await ConfirmedHead(c)) return false;
            var block = await Agree(c, async e => (await Rpc<JObject>(e, "eth_getBlockByNumber", (string)receipt["blockNumber"], false))["hash"]);
            if ((string)block != (string)receipt["blockHash"]) throw new InvalidOperationException("Relay receipt is orphaned.");
            if (Number(receipt["status"]) != 1) throw new InvalidOperationException("Relay transaction reverted; reconcile before retry: " + (string)pending["hash"]);
            db.Record("automation:evm-done:" + (string)pending["action"], new JObject { ["hash"] = pending["hash"], ["blockNumber"] = receipt["blockNumber"], ["blockHash"] = receipt["blockHash"] });
            db.Replace("automation:relay-anchor", new JObject { ["number"] = Number(receipt["blockNumber"]), ["hash"] = receipt["blockHash"] });
            db.Replace("automation:pending-evm", JValue.CreateNull()); return true;
        }
        public static async Task Send(BridgeConfig c, AutomationConfig settings, Journal db, string action, string method, params object[] args) {
            if (db.Get("automation:evm-done:" + action) != null) return;
            if (!await Reconcile(c, db)) return;
            var key = OperatorKeys.LoadEvm(settings.RelayKey); if (c.Strings("operators").Contains(key.GetPublicAddress(), StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Use a separate relay wallet, not an operator keystore.");
            var e = new Evm(c, key: key); var input = e.Vault.GetFunction(method).CreateTransactionInput(key.GetPublicAddress(), args);
            input.Nonce = new HexBigInteger((string)await Rpc<JToken>(e, "eth_getTransactionCount", key.GetPublicAddress(), "pending"));
            input.GasPrice = await e.Web3.Eth.GasPrice.SendRequestAsync(); input.Gas = await e.Web3.Eth.Transactions.EstimateGas.SendRequestAsync(input);
            if (input.GasPrice.Value > settings.MaxGasPriceWei || input.Gas.Value > settings.MaxGasLimit) throw new InvalidOperationException("Relay gas exceeds the configured price/limit; transaction was not signed.");
            string raw = await e.Web3.TransactionManager.SignTransactionAsync(input); if (!raw.StartsWith("0x", StringComparison.Ordinal)) raw = "0x" + raw;
            db.Replace("automation:pending-evm", new JObject { ["action"] = action, ["raw"] = raw, ["hash"] = Native.Keccak(Native.Bytes(raw)) });
            await Reconcile(c, db);
        }
    }
}
