using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nethereum.RPC.Eth.DTOs;

namespace Povix.Bridge.CSharp {
    public static class Program {
        const string Help = "POVIX Bridge — C# / .NET Framework 4.8\n" +
            "Povix.Bridge.exe <command> <production.local.json> [arguments]\n" +
            "network | create-native-operator <keystore> | create-operator <keystore>\n" +
            "run-coordinator <automation.json> [--once] | run-operator <automation.json> [--once]\n" +
            "enqueue-burn <automation.json> <signed-binding.json>\n" +
            "audit | deploy <relay-keystore> | deploy-factory <relay-keystore>\n" +
            "configure-native <founder-keys>\n" +
            "propose-native <mint|register|pause|resume> <reference.json|-> <coordinator.journal>\n" +
            "refresh-native <proposal.json> <coordinator.journal>\n" +
            "sign-native <proposal.json> <native-keystore> <operator.journal>\n" +
            "complete-native <proposal.json> <signatures.json>\n" +
            "deposit <native-atomic-amount> <POVIX-recipient> <EVM-keystore>\n" +
            "prepare-burn <native-atomic-amount> <holder-keys> <fee>\n" +
            "bind-burn <binding.json> <holder-keys> | verify-burn <binding.json>\n" +
            "sign-release <binding.json> <operator-keystore> <operator.journal> <deadline>\n" +
            "release <binding.json> <authorizations.json> <relay-keystore>\n" +
            "execute-release <burn-id> <relay-keystore> | cancel-release <burn-id> <operator-keystore>\n" +
            "emergency-pause <operator-keystore>\n" +
            "sign-pause <operator-keystore> <true|false> <deadline> | pause <authorizations.json> <relay-keystore>\n" +
            "sign-asset <profile.json> <operator-keystore> <operator.journal>\n" +
            "create-asset <profile.json> <authorizations.json> <relay-keystore>\n" +
            "production-asset-config <profile.json>\n" +
            "Passwords: NATIVE_KEYSTORE_PASSWORD / BRIDGE_KEYSTORE_PASSWORD in the local environment.\n" +
            "No Node.js runtime. Contracts remain Solidity. Production candidate, disabled until reviewed/deployed.";
        static JToken Read(string file) { using (var reader = new JsonTextReader(new StreamReader(file)) { DateParseHandling = DateParseHandling.None }) return JToken.Load(reader); }
        static JObject Obj(string file) { return (JObject)Read(file); }
        static JObject Receipt(TransactionReceipt receipt, string stage = null) { var output = new JObject { ["transactionHash"] = receipt.TransactionHash }; if (stage != null) output["stage"] = stage; return output; }
        public static int Main(string[] args) {
            Console.OutputEncoding = new UTF8Encoding(false);
            try { var result = Run(args).GetAwaiter().GetResult(); if (result != null) Console.WriteLine(result.ToString(Formatting.Indented)); return result is JObject && (bool?)result["backed"] == false ? 2 : 0; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
        public static async Task<JToken> Run(string[] args) {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help") { Console.WriteLine(Help); return null; }
            string command = args[0];
            if (command == "network") { if (args.Length != 1) throw new ArgumentException("network takes no arguments."); Console.WriteLine(PrivateCoin.Core.Blockchain.NetworkId); return null; }
            if (args.Length < 2) throw new ArgumentException("Specify a configuration file; use --help for syntax.");
            if (command == "create-native-operator") { if (args.Length != 2) throw new ArgumentException("create-native-operator <keystore>"); return OperatorKeys.CreateNative(args[1]); }
            if (command == "create-operator") { if (args.Length != 2) throw new ArgumentException("create-operator <keystore>"); return OperatorKeys.CreateEvm(args[1]); }
            var c = BridgeConfig.Load(args[1]); var a = args.Skip(2).ToArray();
            Func<int, string> arg = i => { if (i >= a.Length) throw new ArgumentException("Missing command argument; use --help."); return a[i]; };
            if (command == "run-coordinator" || command == "run-operator") {
                if (a.Length < 1 || a.Length > 2 || a.Length == 2 && a[1] != "--once") throw new ArgumentException("Specify automation.json and optional --once.");
                bool coordinator = command == "run-coordinator"; await Automation.Run(c, AutomationConfig.Load(a[0], coordinator), coordinator, a.Length == 2); return null;
            }
            if (command == "enqueue-burn") { if (a.Length != 2) throw new ArgumentException("enqueue-burn <automation.json> <signed-binding.json>"); return new JObject { ["requestId"] = Automation.EnqueueBurn(c, AutomationConfig.Load(a[0], true), AutomationFiles.Read(a[1])) }; }
            if (command == "audit") return await BridgeService.Audit(c);
            if (command == "propose-native") using (var db = new Journal(arg(2), c)) return await BridgeService.Propose(c, arg(0), arg(0) == "mint" ? Obj(arg(1)) : null, db);
            if (command == "refresh-native") using (var db = new Journal(arg(1), c)) return await BridgeService.RefreshExpired(c, Obj(arg(0)), db);
            if (command == "sign-native") using (var db = new Journal(arg(2), c)) return await BridgeService.SignNative(c, Obj(arg(0)), arg(1), db);
            if (command == "complete-native") return await BridgeService.Complete(c, Obj(arg(0)), (JArray)Read(arg(1)));
            if (command == "production-asset-config") return await BridgeService.AssetConfig(c, Obj(arg(0)));
            if (command == "deploy") {
                if (c["bridge"] != null) throw new InvalidOperationException("Deployment requires bridge:null."); await Evm.CheckAll(c, true);
                var evm = new Evm(c, key: OperatorKeys.LoadEvm(arg(0)));
                var receipt = await evm.Deploy("ProductionVault", c["asset"], Native.Bytes(BridgeNative.Hash(c["nativeNetwork"])), Native.Bytes(c["nativeToken"]), c.Strings("operators"), c.Number("threshold"), c.Amount("reserveCap"), c.Amount("dailyReleaseCap"));
                return new JObject { ["bridge"] = receipt.ContractAddress.ToLowerInvariant(), ["transactionHash"] = receipt.TransactionHash, ["vaultCodeHash"] = await evm.CodeHash(receipt.ContractAddress), ["paused"] = true };
            }
            await Evm.CheckAll(c);
            var readEvm = new Evm(c);
            if (command == "deploy-factory") { var evm = new Evm(c, key: OperatorKeys.LoadEvm(arg(0))); var receipt = await evm.Deploy("ProductionVaultFactory", c["bridge"]); return new JObject { ["factory"] = receipt.ContractAddress.ToLowerInvariant(), ["transactionHash"] = receipt.TransactionHash, ["factoryCodeHash"] = await evm.CodeHash(receipt.ContractAddress) }; }
            if (command == "configure-native") { OperatorKeys.RequirePrivate(arg(0)); return Native.Call(c, "configure", new JObject(), arg(0)); }
            if (command == "prepare-burn") {
                BigInteger amount; long fee;
                if (!BigInteger.TryParse(arg(0), out amount) || amount <= 0 || amount > c.Amount("dailyReleaseCap") || !long.TryParse(arg(2), out fee) || fee < 0) throw new InvalidOperationException("Split redemptions within the daily release cap; specify a nonnegative atomic fee.");
                OperatorKeys.RequirePrivate(arg(1)); return Native.Call(c, "operation", new JObject { ["operation"] = "burn", ["amount"] = amount.ToString(), ["fee"] = fee, ["pending"] = new JArray() }, arg(1));
            }
            if (command == "bind-burn") { OperatorKeys.RequirePrivate(arg(1)); return Native.Call(c, "bind", Obj(arg(0)), arg(1)); }
            if (command == "verify-burn") return BridgeService.Burn(c, Obj(arg(0)));
            if (command == "deposit") {
                BigInteger amount; if (!BigInteger.TryParse(arg(0), out amount) || amount <= 0 || amount > long.MaxValue || !BridgeConfig.Hex(arg(1), 64)) throw new InvalidOperationException("Specify a positive atomic amount and POVIX recipient.");
                var key = OperatorKeys.LoadEvm(arg(2)); var evm = new Evm(c, key: key); var token = evm.Token(c["asset"]); BigInteger scaled = amount * await evm.Call<BigInteger>(evm.Vault, "scale");
                BigInteger allowance = await evm.Call<BigInteger>(token, "allowance", key.GetPublicAddress(), c["bridge"]);
                if (allowance < scaled) { if (allowance != 0) await evm.Send(token, "approve", c["bridge"], BigInteger.Zero); await evm.Send(token, "approve", c["bridge"], scaled); }
                var receipt = await evm.Send(evm.Vault, "deposit", scaled, Native.Bytes(arg(1)));
                string topic = Native.Keccak(Encoding.UTF8.GetBytes("Deposited(bytes32,address,bytes32,uint256,uint256)"));
                var log = JArray.FromObject(receipt.Logs).OfType<JObject>().Single(l => string.Equals((string)l["address"], c["bridge"], StringComparison.OrdinalIgnoreCase) && (string)l["topics"][0] == topic);
                return new JObject { ["transactionHash"] = receipt.TransactionHash, ["logIndex"] = (int)new Nethereum.Hex.HexTypes.HexBigInteger((string)log["logIndex"]).Value };
            }
            if (command == "sign-release") {
                var evidence = BridgeService.Burn(c, Obj(arg(0))); byte[] burnId = Native.Bytes((string)evidence["depositId"]);
                if (await readEvm.Call<bool>(readEvm.Vault, "processedBurns", burnId)) throw new InvalidOperationException("Burn already released.");
                var value = new JObject { ["burnId"] = evidence["depositId"], ["recipient"] = evidence["recipient"], ["nativeAmount"] = evidence["amount"], ["deadline"] = long.Parse(arg(3)), ["nonce"] = (await readEvm.Call<BigInteger>(readEvm.Vault, "releaseNonces", burnId)).ToString() }; BridgeService.Deadline(value);
                var key = OperatorKeys.LoadEvm(arg(1));
                using (var db = new Journal(arg(2), c)) { db.Record("release:" + (string)value["burnId"], new JObject { ["recipient"] = value["recipient"], ["nativeAmount"] = value["nativeAmount"] }); return new JObject { ["value"] = value, ["signature"] = Evm.Sign(c, "Release", value, key), ["evidence"] = evidence }; }
            }
            if (command == "release") {
                var evidence = BridgeService.Burn(c, Obj(arg(0))); var auth = (JArray)Read(arg(1)); var value = auth.Count == 0 ? null : (JObject)auth[0]["value"];
                if (value == null || (string)value["burnId"] != (string)evidence["depositId"] || !string.Equals((string)value["recipient"], (string)evidence["recipient"], StringComparison.OrdinalIgnoreCase) || (string)value["nativeAmount"] != (string)evidence["amount"]) throw new InvalidOperationException("Release differs from the canonical burn.");
                BridgeService.Deadline(value); var signatures = Evm.Signatures(c, "Release", value, auth); var evm = new Evm(c, key: OperatorKeys.LoadEvm(arg(2)));
                if ((string)value["nonce"] != (await evm.Call<BigInteger>(evm.Vault, "releaseNonces", Native.Bytes((string)value["burnId"]))).ToString()) throw new InvalidOperationException("Stale release nonce.");
                return Receipt(await evm.Send(evm.Vault, "release", Native.Bytes((string)value["burnId"]), (string)value["recipient"], BigInteger.Parse((string)value["nativeAmount"]), (long)value["deadline"], signatures), "queued-24h");
            }
            if (command == "sign-pause") {
                if (arg(1) != "true" && arg(1) != "false") throw new InvalidOperationException("Pause must be true or false.");
                if (arg(1) == "false" && !(bool)(await BridgeService.Audit(c))["backed"]) throw new InvalidOperationException("Reserve audit failed; keep the bridge paused.");
                var value = new JObject { ["paused"] = arg(1) == "true", ["nonce"] = (await readEvm.Call<BigInteger>(readEvm.Vault, "pauseNonce")).ToString(), ["deadline"] = long.Parse(arg(2)) }; BridgeService.Deadline(value);
                return new JObject { ["value"] = value, ["signature"] = Evm.Sign(c, "Pause", value, OperatorKeys.LoadEvm(arg(0))) };
            }
            if (command == "pause") {
                var auth = (JArray)Read(arg(0)); if (auth.Count == 0) throw new InvalidOperationException("Missing authorizations."); var value = (JObject)auth[0]["value"]; BridgeService.Deadline(value);
                if ((string)value["nonce"] != (await readEvm.Call<BigInteger>(readEvm.Vault, "pauseNonce")).ToString()) throw new InvalidOperationException("Pause nonce mismatch.");
                var signatures = Evm.Signatures(c, "Pause", value, auth); var evm = new Evm(c, key: OperatorKeys.LoadEvm(arg(1))); return Receipt(await evm.Send(evm.Vault, "setPaused", (bool)value["paused"], (long)value["deadline"], signatures));
            }
            if (command == "execute-release" || command == "cancel-release" || command == "emergency-pause") {
                var evm = new Evm(c, key: OperatorKeys.LoadEvm(arg(command == "emergency-pause" ? 0 : 1)));
                if (command != "emergency-pause" && !BridgeConfig.Hex(arg(0), 64, true)) throw new InvalidOperationException("Specify a burn ID.");
                return Receipt(command == "emergency-pause" ? await evm.Send(evm.Vault, "emergencyPause") : await evm.Send(evm.Vault, command == "execute-release" ? "executeRelease" : "cancelRelease", Native.Bytes(arg(0))));
            }
            if (command == "sign-asset" || command == "create-asset") {
                var value = await BridgeService.ReviewAsset(c, Obj(arg(0))); BridgeService.Deadline(value);
                if (command == "sign-asset") using (var db = new Journal(arg(2), c)) { db.Record("asset:" + (string)value["asset"], new JObject { ["asset"] = value["asset"], ["assetCodeHash"] = value["codeHash"], ["cap"] = value["cap"], ["dailyCap"] = value["dailyCap"] }); return new JObject { ["value"] = value, ["signature"] = Evm.Sign(c, "Asset", value, OperatorKeys.LoadEvm(arg(1))) }; }
                var signatures = Evm.Signatures(c, "Asset", value, (JArray)Read(arg(1))); var evm = new Evm(c, key: OperatorKeys.LoadEvm(arg(2))); var factory = evm.Web3.Eth.GetContract(JObject.Parse(Evm.Artifact("ProductionVaultFactory"))["abi"].ToString(), c["factory"]);
                var receipt = await evm.Send(factory, "createVault", (string)value["asset"], Native.Bytes((string)value["codeHash"]), BigInteger.Parse((string)value["cap"]), BigInteger.Parse((string)value["dailyCap"]), (long)value["deadline"], signatures);
                var result = Receipt(receipt); result["vault"] = (await evm.Call<string>(factory, "vaults", (string)value["asset"])).ToLowerInvariant(); return result;
            }
            throw new ArgumentException("Unknown command; use --help.");
        }
    }
}
