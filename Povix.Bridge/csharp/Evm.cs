using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;

namespace Povix.Bridge.CSharp {
    public sealed class Evm {
        public readonly Web3 Web3;
        public readonly BridgeConfig Config;
        public readonly Contract Vault;
        readonly Account account;
        public static string Artifact(string name) {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "contracts");
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, name + ".json"));
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            using (var sha = System.Security.Cryptography.SHA256.Create()) if (sha.ComputeHash(bytes).ToHexLower() != (string)manifest["files"]["contracts/artifacts/" + name + ".json"]) throw new InvalidOperationException("Contract artifact integrity failure.");
            return Encoding.UTF8.GetString(bytes);
        }
        public Evm(BridgeConfig config, string url = null, EthECKey key = null) {
            Config = config;
            account = key == null ? null : new Account(key.GetPrivateKeyAsBytes(), config.ChainId);
            Web3 = account == null ? new Web3(url ?? config["rpcUrl"]) : new Web3(account, url ?? config["rpcUrl"]);
            Web3.TransactionManager.UseLegacyAsDefault = true;
            Vault = config["bridge"] == null ? null : Web3.Eth.GetContract((string)JObject.Parse(Artifact("ProductionVault"))["abi"].ToString(Formatting.None), config["bridge"]);
        }
        static async Task<T> Timeout<T>(Task<T> task) { if (await Task.WhenAny(task, Task.Delay(30000)) != task) throw new TimeoutException("BNB RPC timed out."); return await task; }
        public async Task<T> Call<T>(Contract contract, string name, params object[] args) { return await Timeout(contract.GetFunction(name).CallAsync<T>(args)); }
        public Contract Token(string address) { return Web3.Eth.GetContract("[{\"type\":\"function\",\"name\":\"decimals\",\"inputs\":[],\"outputs\":[{\"type\":\"uint8\"}],\"stateMutability\":\"view\"},{\"type\":\"function\",\"name\":\"symbol\",\"inputs\":[],\"outputs\":[{\"type\":\"string\"}],\"stateMutability\":\"view\"},{\"type\":\"function\",\"name\":\"balanceOf\",\"inputs\":[{\"type\":\"address\"}],\"outputs\":[{\"type\":\"uint256\"}],\"stateMutability\":\"view\"},{\"type\":\"function\",\"name\":\"allowance\",\"inputs\":[{\"type\":\"address\"},{\"type\":\"address\"}],\"outputs\":[{\"type\":\"uint256\"}],\"stateMutability\":\"view\"},{\"type\":\"function\",\"name\":\"approve\",\"inputs\":[{\"type\":\"address\"},{\"type\":\"uint256\"}],\"outputs\":[{\"type\":\"bool\"}],\"stateMutability\":\"nonpayable\"}]", address); }
        public async Task<string> CodeHash(string address) { string code = await Timeout(Web3.Eth.GetCode.SendRequestAsync(address)); if (code == "0x" || code == "0x0") throw new InvalidOperationException("Contract has no runtime code."); return Native.Keccak(Native.Bytes(code)); }
        public async Task Check(bool deployment = false) {
            if ((await Timeout(Web3.Eth.ChainId.SendRequestAsync())).Value != Config.ChainId || await CodeHash(Config["asset"]) != Config["assetCodeHash"]) throw new InvalidOperationException("BNB chain/source code mismatch.");
            if (deployment) return;
            if (Vault == null || await CodeHash(Config["bridge"]) != Config["vaultCodeHash"] ||
                (await Call<byte[]>(Vault, "nativeNetwork")).ToHexLower() != BridgeNative.Hash(Config["nativeNetwork"]) ||
                (await Call<byte[]>(Vault, "nativeToken")).ToHexLower() != Config["nativeToken"] ||
                !string.Equals(await Call<string>(Vault, "asset"), Config["asset"], StringComparison.OrdinalIgnoreCase) ||
                await Call<BigInteger>(Vault, "threshold") != Config.Number("threshold") ||
                await Call<BigInteger>(Vault, "reserveCap") != Config.Amount("reserveCap") || await Call<BigInteger>(Vault, "dailyReleaseCap") != Config.Amount("dailyReleaseCap") ||
                await Call<BigInteger>(Vault, "bridgeVersion") != 2 || await Call<BigInteger>(Vault, "releaseDelay") != 86400) throw new InvalidOperationException("Vault differs from pinned production configuration.");
            var members = Config.Strings("operators");
            for (int i = 0; i < members.Length; i++) if (!string.Equals(await Call<string>(Vault, "operators", i), members[i], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Vault operator mismatch.");
            // A matching prefix is insufficient: reject an additional on-chain operator.
            if (members.Length < 10) {
                bool extra = false; try { await Call<string>(Vault, "operators", members.Length); extra = true; } catch (Nethereum.JsonRpc.Client.RpcResponseException) { }
                if (extra) throw new InvalidOperationException("Vault has extra operators.");
            }
            int decimals = (int)await Call<BigInteger>(Token(Config["asset"]), "decimals"), expected = Config["nativeToken"] == Native.ProtocolToken ? 8 : Config.Number("nativeDecimals");
            if (decimals < 0 || decimals > 18 || expected != Math.Min(8, decimals) || await Call<BigInteger>(Vault, "scale") != BigInteger.Pow(10, decimals - expected)) throw new InvalidOperationException("Asset precision mismatch.");
            if (Config["factory"] != null && await CodeHash(Config["factory"]) != Config["factoryCodeHash"]) throw new InvalidOperationException("Factory code mismatch.");
        }
        public static async Task CheckAll(BridgeConfig c, bool deployment = false) { await Task.WhenAll(c.Strings("rpcUrls").Select(async url => await new Evm(c, url).Check(deployment))); }
        public async Task<JObject> DepositEvidence(JObject reference) {
            string hash = (string)reference["transactionHash"]; int index = (int)reference["logIndex"];
            if (!BridgeConfig.Hex(hash == null ? null : hash.ToLowerInvariant(), 64, true) || index < 0) throw new InvalidOperationException("Invalid deposit reference.");
            await Check(); var receipt = await Timeout(Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(hash));
            if (receipt == null || receipt.Status == null || receipt.Status.Value != 1) throw new InvalidOperationException("Deposit did not succeed.");
            var block = await Timeout(Web3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(new BlockParameter(receipt.BlockNumber)));
            var latest = await Timeout(Web3.Eth.Blocks.GetBlockNumber.SendRequestAsync());
            if (block == null || block.BlockHash != receipt.BlockHash || latest.Value - receipt.BlockNumber.Value + 1 < Config.Number("bscConfirmations")) throw new InvalidOperationException("Deposit is orphaned or lacks confirmations.");
            var log = JArray.FromObject(receipt.Logs).OfType<JObject>().SingleOrDefault(l => new HexBigInteger((string)l["logIndex"]).Value == index && string.Equals((string)l["address"], Config["bridge"], StringComparison.OrdinalIgnoreCase));
            string topic = Native.Keccak(Encoding.UTF8.GetBytes("Deposited(bytes32,address,bytes32,uint256,uint256)"));
            if (log == null || (bool?)log["removed"] == true || ((JArray)log["topics"]).Count != 4 || (string)log["topics"][0] != topic) throw new InvalidOperationException("Not a deposit from the pinned vault.");
            byte[] data = Native.Bytes((string)log["data"]); if (data.Length != 64) throw new InvalidOperationException("Invalid deposit data.");
            BigInteger amount = new BigInteger(data.Take(32).Reverse().Concat(new byte[] { 0 }).ToArray()), assetAmount = new BigInteger(data.Skip(32).Reverse().Concat(new byte[] { 0 }).ToArray());
            if (amount <= 0 || amount > long.MaxValue || assetAmount != amount * await Call<BigInteger>(Vault, "scale")) throw new InvalidOperationException("Deposit amount/scale mismatch.");
            return new JObject { ["depositId"] = log["topics"][1], ["recipient"] = ((string)log["topics"][3]).Substring(2), ["amount"] = amount.ToString(), ["blockHash"] = block.BlockHash };
        }
        public static async Task<JObject> VerifyDeposit(BridgeConfig c, JObject reference) {
            var results = await Task.WhenAll(c.Strings("rpcUrls").Select(url => new Evm(c, url).DepositEvidence(reference)));
            if (results.Any(r => Native.Canonical(r) != Native.Canonical(results[0]))) throw new InvalidOperationException("Independent BNB RPCs disagree on the deposit."); return results[0];
        }
        public async Task<TransactionReceipt> Send(Contract contract, string name, params object[] args) {
            if (account == null) throw new InvalidOperationException("A local EVM keystore is required.");
            var function = contract.GetFunction(name); var input = function.CreateTransactionInput(account.Address, args); input.Gas = await Timeout(Web3.Eth.Transactions.EstimateGas.SendRequestAsync(input));
            return await Send(input);
        }
        public async Task<TransactionReceipt> Send(TransactionInput input) {
            if (account == null) throw new InvalidOperationException("A local EVM keystore is required."); input.From = account.Address;
            if (input.Gas == null) input.Gas = await Timeout(Web3.Eth.Transactions.EstimateGas.SendRequestAsync(input));
            string hash = await Timeout(Web3.TransactionManager.SendTransactionAsync(input)); Console.Error.WriteLine("Submitted transaction: " + hash + ". If interrupted, reconcile this hash before retrying.");
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10))) {
                var receipt = await Web3.TransactionManager.TransactionReceiptService.PollForReceiptAsync(hash, cts.Token);
                if (receipt.Status == null || receipt.Status.Value != 1) throw new InvalidOperationException("Transaction reverted: " + hash); return receipt;
            }
        }
        public async Task<TransactionReceipt> Deploy(string name, params object[] args) {
            var artifact = JObject.Parse(Artifact(name));
            return await Send(new TransactionInput { From = account.Address, Data = Web3.Eth.DeployContract.GetData((string)artifact["bytecode"], artifact["abi"].ToString(Formatting.None), args) });
        }
        public static JObject TypedData(BridgeConfig c, string kind, JObject value) {
            string fields = kind == "Release" ? "burnId:bytes32,recipient:address,nativeAmount:uint256,deadline:uint256,nonce:uint256" : kind == "Pause" ? "paused:bool,nonce:uint256,deadline:uint256" : kind == "Asset" ? "asset:address,codeHash:bytes32,cap:uint256,dailyCap:uint256,deadline:uint256" : throw new InvalidOperationException("Unknown typed authorization.");
            Func<string, JArray> types = s => new JArray(s.Split(',').Select(f => { var pair = f.Split(':'); return new JObject { ["name"] = pair[0], ["type"] = pair[1] }; }));
            return new JObject { ["types"] = new JObject { ["EIP712Domain"] = types("name:string,version:string,chainId:uint256,verifyingContract:address"), [kind] = types(fields) }, ["primaryType"] = kind,
                ["domain"] = new JObject { ["name"] = kind == "Asset" ? "POVIX Asset Factory" : "POVIX Asset Bridge", ["version"] = "2", ["chainId"] = c.ChainId, ["verifyingContract"] = c[kind == "Asset" ? "factory" : "bridge"] }, ["message"] = value.DeepClone() };
        }
        public static string Sign(BridgeConfig c, string kind, JObject value, EthECKey key) {
            if (!c.Strings("operators").Contains(key.GetPublicAddress(), StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Not a registered EVM operator.");
            return Eip712TypedDataSigner.Current.SignTypedDataV4(TypedData(c, kind, value).ToString(Formatting.None), key);
        }
        public static byte[][] Signatures(BridgeConfig c, string kind, JObject value, JArray auth) {
            var data = TypedData(c, kind, value).ToString(Formatting.None);
            var recovered = auth.Select(a => { if (Native.Canonical(a["value"]) != Native.Canonical(value)) throw new InvalidOperationException("Mismatched authorizations."); return new { Address = Eip712TypedDataSigner.Current.RecoverFromSignatureV4(data, (string)a["signature"], "message").ToLowerInvariant(), Signature = Native.Bytes((string)a["signature"]) }; }).OrderBy(a => a.Address, StringComparer.Ordinal).ToArray();
            if (recovered.Length < c.Number("threshold") || recovered.Length > c.Strings("operators").Length || recovered.Select(a => a.Address).Distinct().Count() != recovered.Length || recovered.Any(a => !c.Strings("operators").Contains(a.Address, StringComparer.OrdinalIgnoreCase))) throw new InvalidOperationException("Invalid operator quorum."); return recovered.Select(a => a.Signature).ToArray();
        }
    }
}
