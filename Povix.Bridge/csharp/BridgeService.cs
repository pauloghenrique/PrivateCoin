using System;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Povix.Bridge.CSharp {
    public static class BridgeService {
        public static void Deadline(JObject value) {
            long deadline = (long)value["deadline"], now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (deadline <= now || deadline > now + 600) throw new InvalidOperationException("Use a common deadline within ten minutes.");
        }
        public static async Task<JObject> Review(BridgeConfig c, JObject proposal) {
            string kind = (string)proposal["kind"];
            if (kind == "mint") { var evidence = await Evm.VerifyDeposit(c, (JObject)proposal["reference"]); if (Native.Canonical(evidence) != Native.Canonical(proposal["deposit"])) throw new InvalidOperationException("Proposal differs from independently verified deposit."); }
            else if (new[] { "register", "pause", "resume" }.Contains(kind)) await Evm.CheckAll(c);
            else throw new InvalidOperationException("Unknown proposal kind.");
            return new JObject { ["payload"] = Native.Review(c, "production-review", proposal) };
        }
        static string ProposalKey(BridgeConfig c, JObject p) { return "proposal:" + ((string)p["kind"] == "mint" ? (string)p["deposit"]["depositId"] : (string)p["kind"] + ":" + c["nativeToken"] + (new[] { "pause", "resume" }.Contains((string)p["kind"]) ? ":" + (string)p["draft"]["TokenCommand"]["Revision"] : "")); }
        public static async Task<JObject> Propose(BridgeConfig c, string kind, JObject reference, Journal journal) {
            if (!new[] { "mint", "register", "pause", "resume" }.Contains(kind)) throw new InvalidOperationException("Expected mint, register, pause or resume.");
            JObject deposit = kind == "mint" ? await Evm.VerifyDeposit(c, reference) : null;
            if (kind != "mint") await Evm.CheckAll(c);
            var p = new JObject { ["kind"] = kind, ["deposit"] = deposit, ["reference"] = kind == "mint" ? reference : null };
            p["draft"] = Native.Call(c, "production-draft", p);
            string key = ProposalKey(c, p); var old = journal.Get(key);
            if (old != null) return (JObject)old;
            Native.Review(c, "production-review", p); journal.Record(key, p); return p;
        }
        public static async Task<JObject> RefreshExpired(BridgeConfig c, JObject previous, Journal journal) {
            if ((string)previous["kind"] == "mint") {
                var evidence = await Evm.VerifyDeposit(c, (JObject)previous["reference"]);
                if (Native.Canonical(evidence) != Native.Canonical(previous["deposit"])) throw new InvalidOperationException("Deposit facts changed. Stop and investigate.");
            } else { if (!new[] { "register", "pause", "resume" }.Contains((string)previous["kind"])) throw new InvalidOperationException("Unknown proposal kind."); await Evm.CheckAll(c); }
            return RefreshVerified(c, previous, journal);
        }
        static JObject RefreshVerified(BridgeConfig c, JObject previous, Journal journal) {
            var old = journal.Get(ProposalKey(c, previous));
            if (old == null || Native.Canonical(old) != Native.Canonical(previous)) throw new InvalidOperationException("Refresh requires this coordinator's exact stored proposal.");
            var next = (JObject)previous.DeepClone(); next.Remove("signatures"); next["draft"] = Native.Call(c, "production-draft", previous); Native.Review(c, "production-review", next);
            journal.Record("history:" + BridgeNative.Hash(Native.Canonical(previous)), previous); journal.Replace(ProposalKey(c, next), next); return next;
        }
        public static async Task<JObject> SignNative(BridgeConfig c, JObject proposal, string keystore, Journal journal) {
            var result = await Review(c, proposal); byte[] payload = Convert.FromBase64String((string)result["payload"]);
            using (var rsa = OperatorKeys.LoadNative(keystore)) {
                string pub = rsa.ToXmlString(false), address = BridgeNative.Hash(pub);
                if (!proposal["draft"]["TokenAuthorizations"].Any(a => (string)a["PublicKey"] == pub)) throw new InvalidOperationException("Operator is not in the chosen signing quorum.");
                string key = (string)proposal["kind"] + ":" + ((string)proposal["kind"] == "mint" ? (string)proposal["deposit"]["depositId"] : c["nativeToken"] + ":" + (string)proposal["draft"]["TokenCommand"]["Revision"]);
                JToken decision = (string)proposal["kind"] == "mint" ? proposal["deposit"] : new JObject { ["token"] = proposal["draft"]["Token"], ["command"] = proposal["draft"]["TokenCommand"] };
                journal.Record("decision:" + key, decision);
                return new JObject { ["address"] = address, ["signature"] = Convert.ToBase64String(rsa.SignData(payload, "SHA256")), ["payloadHash"] = Native.Keccak(payload) };
            }
        }
        public static async Task<JToken> Complete(BridgeConfig c, JObject proposal, JArray signatures) {
            byte[] payload = Convert.FromBase64String((string)(await Review(c, proposal))["payload"]);
            var proofs = proposal["draft"]["TokenAuthorizations"].ToArray();
            if (signatures.Count != proofs.Length || signatures.Select(s => (string)s["address"]).Distinct().Count() != signatures.Count) throw new InvalidOperationException("Collect every distinct signature of the selected quorum.");
            foreach (var s in signatures) {
                var proof = proofs.SingleOrDefault(p => BridgeNative.Hash((string)p["PublicKey"]) == (string)s["address"]);
                if (proof == null || (string)s["payloadHash"] != Native.Keccak(payload)) throw new InvalidOperationException("Invalid signature identity/payload.");
                using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString((string)proof["PublicKey"]); if (!rsa.VerifyData(payload, "SHA256", Convert.FromBase64String((string)s["signature"]))) throw new InvalidOperationException("Invalid native signature."); }
            }
            var completed = (JObject)proposal.DeepClone(); completed["signatures"] = signatures; return Native.Review(c, "production-complete", completed);
        }
        public static JToken Burn(BridgeConfig c, JObject binding) {
            var results = c.Strings("nativeSnapshots").Select(s => Native.Call(c, "verify", binding, snapshot: s)).ToArray();
            Func<JToken, string> facts = r => Native.Canonical(new JObject { ["depositId"] = r["depositId"], ["recipient"] = r["recipient"], ["amount"] = r["amount"], ["blockHash"] = r["blockHash"] });
            if (results.Any(r => facts(r) != facts(results[0]))) throw new InvalidOperationException("Native nodes disagree about the burn."); return results[0];
        }
        public static async Task<JObject> Audit(BridgeConfig c) {
            var native = Native.Review(c, "production-status", new JObject());
            var states = await Task.WhenAll(c.Strings("rpcUrls").Select(async url => {
                var evm = new Evm(c, url); await evm.Check();
                return new JObject { ["balance"] = (await evm.Call<BigInteger>(evm.Token(c["asset"]), "balanceOf", c["bridge"])).ToString(), ["reserve"] = (await evm.Call<BigInteger>(evm.Vault, "accountedReserve")).ToString(), ["scale"] = (await evm.Call<BigInteger>(evm.Vault, "scale")).ToString(), ["reserved"] = (await evm.Call<BigInteger>(evm.Vault, "reservedForRelease")).ToString(), ["paused"] = await evm.Call<bool>(evm.Vault, "paused") };
            }));
            if (states.Any(s => Native.Canonical(s) != Native.Canonical(states[0]))) throw new InvalidOperationException("BNB providers disagree about reserves.");
            var state = states[0]; Func<string, BigInteger> amount = k => BigInteger.Parse((string)state[k]);
            bool backed = BigInteger.Parse((string)native["supply"]) <= amount("reserve") && amount("balance") >= amount("reserve") * amount("scale") && amount("reserved") <= amount("reserve");
            return new JObject { ["backed"] = backed, ["native"] = native, ["bnb"] = state, ["observedAt"] = DateTime.UtcNow.ToString("o") };
        }
        public static async Task<JObject> ReviewAsset(BridgeConfig c, JObject asset) {
            BigInteger cap, daily;
            if (c["factory"] == null || !BridgeConfig.Hex((string)asset["asset"], 40, true) || !BridgeConfig.Hex((string)asset["assetCodeHash"], 64, true) || !BigInteger.TryParse((string)asset["cap"], out cap) || !BigInteger.TryParse((string)asset["dailyCap"], out daily) || cap <= 0 || cap > long.MaxValue || daily <= 0 || daily > cap) throw new InvalidOperationException("Specify reviewed asset, runtime hash and per-asset limits.");
            var decimals = await Task.WhenAll(c.Strings("rpcUrls").Select(async url => { var evm = new Evm(c, url); await evm.Check(); if (await evm.CodeHash((string)asset["asset"]) != (string)asset["assetCodeHash"]) throw new InvalidOperationException("Asset runtime hash mismatch."); return (int)await evm.Call<BigInteger>(evm.Token((string)asset["asset"]), "decimals"); }));
            if (decimals.Any(d => d != decimals[0] || d < 0 || d > 18)) throw new InvalidOperationException("Asset precision mismatch.");
            return new JObject { ["asset"] = asset["asset"], ["codeHash"] = asset["assetCodeHash"], ["cap"] = cap.ToString(), ["dailyCap"] = daily.ToString(), ["deadline"] = asset["deadline"] };
        }
        public static async Task<JObject> AssetConfig(BridgeConfig c, JObject asset) {
            await ReviewAsset(c, asset); var evm = new Evm(c); var factory = evm.Web3.Eth.GetContract(JObject.Parse(Evm.Artifact("ProductionVaultFactory"))["abi"].ToString(), c["factory"]);
            string address = (string)asset["asset"], vault = (await evm.Call<string>(factory, "vaults", address)).ToLowerInvariant();
            if (!BridgeConfig.Hex(vault, 40, true)) throw new InvalidOperationException("Asset has no approved vault.");
            var json = (JObject)c.Json.DeepClone(); json["asset"] = address; json["assetCodeHash"] = asset["assetCodeHash"]; json["bridge"] = vault; json["vaultCodeHash"] = await evm.CodeHash(vault); json["nativeToken"] = address == c["asset"] ? Native.ProtocolToken : Native.Representation(c.ChainId, address);
            json["nativeDecimals"] = Math.Min(8, (int)await evm.Call<BigInteger>(evm.Token(address), "decimals")); string symbol;
            try { symbol = await evm.Call<string>(evm.Token(address), "symbol"); } catch { symbol = "TOKEN"; }
            symbol = System.Text.RegularExpressions.Regex.Replace(symbol.ToUpperInvariant(), "[^A-Z0-9]", ""); json["nativeSymbol"] = "p" + (symbol.Length == 0 ? "TOKEN" : symbol.Substring(0, Math.Min(9, symbol.Length)));
            json["reserveCap"] = asset["cap"]; json["dailyReleaseCap"] = asset["dailyCap"];
            if ((await evm.Call<byte[]>(factory, "representation", address)).ToHexLower() != (string)json["nativeToken"]) throw new InvalidOperationException("Factory representation mismatch.");
            await Evm.CheckAll(BridgeConfig.From(json)); return json;
        }
    }
}
