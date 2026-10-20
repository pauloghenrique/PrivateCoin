using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using PrivateCoin.Core;

namespace Povix.Bridge
{
    [DataContract] public sealed class Binding {
        [DataMember] public string transactionId;
        [DataMember] public int outputIndex;
        [DataMember] public long chainId;
        [DataMember] public string bridge;
        [DataMember] public string recipient;
        [DataMember] public Dictionary<string, string> signatures;
    }
    [DataContract] public sealed class NativeConfig {
        [DataMember] public string nativeNetwork;
        [DataMember] public string mode;
        [DataMember] public string[] nativeSigningPublicKeys;
        [DataMember] public long chainId;
        [DataMember] public string bridge;
        [DataMember] public string nativeToken;
        [DataMember] public string asset;
        [DataMember] public int nativeDecimals = 8;
        [DataMember] public string nativeSymbol;
        [DataMember] public string reserveCap;
        [DataMember] public string dailyReleaseCap;
        [DataMember] public string[] nativeAdministrators;
        [DataMember] public int nativeThreshold;
        [DataMember] public int nativeConfirmations;
    }
    [DataContract] public sealed class DepositEvidence {
        [DataMember] public string depositId;
        [DataMember] public string recipient;
        [DataMember] public string amount;
        [DataMember] public string blockHash;
        [DataMember] public int height;
        [DataMember] public int confirmations;
        [DataMember] public string tipHash;
    }
    [DataContract] public sealed class ReleaseRequest {
        [DataMember] public string recipient;
        [DataMember] public string amount;
        [DataMember] public long fee;
        [DataMember] public List<Transaction> pending;
        [DataMember] public string operation;
    }
    [DataContract] sealed class StoredNetwork {
        [DataMember] public string Data;
        [DataMember] public string Sha256;
        [DataMember] public List<Block> Blocks;
    }
    [DataContract] sealed class StoredData { [DataMember] public List<Block> Blocks; }

    public static class BridgeNative {
        public static string Hash(string text) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        static string Hex(byte[] bytes) { return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
        static bool HexAddress(string value, int size) { return value != null && value.Length == size && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        static bool BscAddress(string value) { return value != null && value.StartsWith("0x", StringComparison.Ordinal) && HexAddress(value.Substring(2), 40) && value != "0x" + new string('0', 40); }
        public static void ValidateConfig(NativeConfig config) {
            if (config == null || config.nativeNetwork != Blockchain.NetworkId || (config.chainId != 56 && config.chainId != 97 && config.chainId != 31337) ||
                !BscAddress(config.bridge) || config.nativeConfirmations < 12 || config.nativeConfirmations > 100000 ||
                (!NativeBridge.IsAssetId(config.nativeToken) || config.nativeToken != InitialLaunch.PusdtTokenId && config.nativeToken != NativeBridge.RepresentationId(config.chainId, config.asset)) || config.nativeAdministrators == null || config.nativeAdministrators.Length < 3 ||
                config.nativeThreshold <= config.nativeAdministrators.Length / 2 || config.nativeThreshold > config.nativeAdministrators.Length)
                throw new InvalidOperationException("Invalid test USDT bridge config; minimum 12 native confirmations and majority administrators required.");
        }
        public static void ValidateProduction(Blockchain chain, NativeConfig config) {
            ValidateConfig(config);
            if (config.mode != "production" || config.nativeAdministrators.Length < 5 || config.nativeThreshold < 3 || config.nativeConfirmations < (config.chainId == 31337 ? 12 : 120) ||
                config.nativeSigningPublicKeys == null || config.nativeSigningPublicKeys.Length < config.nativeThreshold ||
                config.nativeSigningPublicKeys.Select(Hash).Distinct().Count() != config.nativeSigningPublicKeys.Length ||
                config.nativeSigningPublicKeys.Any(k => !config.nativeAdministrators.Contains(Hash(k))))
                throw new InvalidOperationException("Production requires five administrators, quorum public keys and 120 native confirmations.");
            foreach (var publicKey in config.nativeSigningPublicKeys) using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (rsa.KeySize < 3072 || rsa.KeySize > 4096 || rsa.ToXmlString(false) != publicKey)
                    throw new InvalidOperationException("Production signing configuration requires canonical public-only RSA keys of 3072-4096 bits.");
            }
            var age = DateTime.UtcNow.Ticks - chain.Blocks.Last().TimestampUtcTicks;
            if (age > TimeSpan.FromMinutes(5).Ticks || age < -TimeSpan.FromMinutes(1).Ticks)
                throw new InvalidOperationException("Independent native chain tip is stale or in the future.");
        }
        public static TokenDefinition VerifyToken(Blockchain chain, NativeConfig config) {
            ValidateConfig(config);
            var token = chain.GetTokens().SingleOrDefault(t => t.Id == config.nativeToken);
            if (token == null || token.Decimals != (config.nativeToken == InitialLaunch.PusdtTokenId ? 8 : config.nativeDecimals) || token.Policy == null || !token.Policy.Mintable || !token.Policy.Burnable ||
                !token.Policy.Pausable || token.Policy.HasAutomaticBurn || token.Policy.VestingTranches != 0 ||
                token.Policy.RequiredSignatures != config.nativeThreshold || token.Policy.BridgeBinding != NativeBridge.Binding(config.chainId, config.asset, config.bridge) ||
                !token.Policy.Administrators.OrderBy(a => a).SequenceEqual(config.nativeAdministrators.OrderBy(a => a)))
                throw new InvalidOperationException("Native representation does not match pinned bridge policy.");
            return token;
        }
        public static string BindingPayload(Binding binding) {
            if (binding == null || !HexAddress(binding.transactionId, 64) || binding.outputIndex < 0 ||
                !BscAddress(binding.bridge) || !BscAddress(binding.recipient) || (binding.chainId != 56 && binding.chainId != 97 && binding.chainId != 31337))
                throw new InvalidOperationException("Invalid burn binding (use lowercase BSC addresses).");
            return string.Join("|", new[] { "povix-usdt-burn-binding-v1", Blockchain.NetworkId, binding.chainId.ToString(CultureInfo.InvariantCulture),
                binding.bridge, binding.transactionId, binding.outputIndex.ToString(CultureInfo.InvariantCulture), binding.recipient });
        }
        // Every input owner authorizes the burn recipient; observers cannot claim public burns.
        public static Binding SignBinding(Blockchain chain, Binding binding, IEnumerable<string> privateKeys) {
            var tx = chain.Blocks.SelectMany(b => b.Transactions).Single(t => t.Id == binding.transactionId);
            var keys = tx.Inputs.Select(i => i.PublicKey).Distinct().ToArray();
            var signatures = new Dictionary<string, string>();
            byte[] payload = Encoding.UTF8.GetBytes(BindingPayload(binding));
            foreach (string xml in privateKeys) using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml);
                string pub = rsa.ToXmlString(false);
                if (keys.Contains(pub)) signatures[Hash(pub)] = Convert.ToBase64String(rsa.SignData(payload, "SHA256"));
            }
            if (keys.Length == 0 || signatures.Count != keys.Length) throw new InvalidOperationException("All burn input owners must authorize the BSC recipient.");
            binding.signatures = signatures; return binding;
        }
        public static DepositEvidence VerifyBurn(Blockchain chain, NativeConfig config, Binding binding) {
            VerifyToken(chain, config);
            byte[] payload = Encoding.UTF8.GetBytes(BindingPayload(binding));
            if (binding.outputIndex != 0 || binding.chainId != config.chainId || binding.bridge != config.bridge) throw new InvalidOperationException("Wrong bridge domain.");
            var blocks = chain.Blocks;
            Block block = blocks.SingleOrDefault(b => b.Transactions.Any(t => t.Id == binding.transactionId));
            if (block == null) throw new InvalidOperationException("Burn is not confirmed in the current canonical chain.");
            Transaction tx = block.Transactions.Single(t => t.Id == binding.transactionId);
            int confirmations = blocks.Last().Height - block.Height + 1;
            if (tx.Kind != TransactionKind.TokenManage || tx.TokenCommand == null || tx.TokenCommand.Operation != "burn" ||
                tx.TokenCommand.TokenId != config.nativeToken || tx.TokenCommand.Amount <= 0 || tx.Inputs.Count == 0 || confirmations < config.nativeConfirmations)
                throw new InvalidOperationException("Expected a sufficiently confirmed burn of the pinned native USDT representation.");
            foreach (string key in tx.Inputs.Select(i => i.PublicKey).Distinct()) {
                string signature;
                if (binding.signatures == null || !binding.signatures.TryGetValue(Hash(key), out signature)) throw new InvalidOperationException("Missing recipient authorization.");
                using (var rsa = new RSACryptoServiceProvider()) {
                    rsa.PersistKeyInCsp = false; rsa.FromXmlString(key);
                    if (!rsa.VerifyData(payload, "SHA256", Convert.FromBase64String(signature))) throw new InvalidOperationException("Invalid recipient authorization.");
                }
            }
            return new DepositEvidence { depositId = "0x" + Hash(Blockchain.NetworkId + "|usdt-burn|" + config.nativeToken + "|" + tx.Id),
                recipient = binding.recipient, amount = tx.TokenCommand.Amount.ToString(CultureInfo.InvariantCulture), blockHash = block.Hash,
                height = block.Height, confirmations = confirmations, tipHash = blocks.Last().Hash };
        }
        // Test harness only: native administrators' private keys must not be consolidated for production.
        public static Transaction PrepareOperation(Blockchain chain, NativeConfig config, ReleaseRequest request, string[] privateKeys) {
            ValidateConfig(config);
            if (config.chainId == 56 && request.operation == "mint") throw new InvalidOperationException("Mainnet mint requires separate operator signatures.");
            var registration = (request.pending ?? new List<Transaction>()).FirstOrDefault(t => NativeBridge.IsRegistration(t) && t.TokenCommand.TokenId == config.nativeToken);
            bool registering = chain.GetTokenRuntime(config.nativeToken) == null && registration != null;
            if (!registering) VerifyToken(chain, config);
            long amount;
            if ((request.operation != "burn" && request.operation != "mint") || !long.TryParse(request.amount, NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount <= 0)
                throw new InvalidOperationException("Invalid native token operation.");
            if (config.mode == "production" && request.operation == "burn" && amount > long.Parse(config.dailyReleaseCap, CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Split the redemption into burns no larger than the daily release cap before burning.");
            var keys = new Dictionary<string, RSACryptoServiceProvider>();
            try {
                foreach (string xml in privateKeys) {
                    var rsa = new RSACryptoServiceProvider(); rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); keys.Add(Hash(rsa.ToXmlString(false)), rsa);
                }
                var confirmed = new HashSet<string>(chain.Blocks.SelectMany(b => b.Transactions).Select(t => t.Id));
                var pending = (request.pending ?? new List<Transaction>()).Where(t => !confirmed.Contains(t.Id)).ToArray();
                if (registering) {
                    if (request.operation != "mint" || request.fee != 0 || registration.Token.Policy.BridgeBinding != NativeBridge.Binding(config.chainId, config.asset, config.bridge) ||
                        registration.Token.Decimals != config.nativeDecimals || amount > registration.Token.Policy.MaximumSupply)
                        throw new InvalidOperationException("Invalid first issuance.");
                    var root = chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Token.Policy;
                    var firstMint = new Transaction { Kind = TransactionKind.TokenManage, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                        TokenCommand = new TokenCommand { TokenId = config.nativeToken, Operation = "mint", Amount = amount },
                        Outputs = new List<TransactionOutput> { new TransactionOutput { AssetId = config.nativeToken, Amount = amount, OneTimeAddress = request.recipient } },
                        TokenAuthorizations = root.Administrators.Where(keys.ContainsKey).Take(root.RequiredSignatures).Select(a => new TokenAuthorization { PublicKey = keys[a].ToXmlString(false) }).ToList() };
                    Sign(firstMint, keys); chain.ValidatePendingTransactions(pending.Concat(new[] { firstMint })); return firstMint;
                }
                var draft = TokenManagement.Prepare(chain, pending, keys.Values.Select(k => k.ToXmlString(false)), config.nativeToken,
                    request.operation, amount, request.recipient, keys.Keys.First(), request.fee);
                Func<string, string> sign = address => Convert.ToBase64String(keys[address].SignData(draft.SigningPayload, "SHA256"));
                var tx = draft.Complete(draft.InputAddresses.Select(sign), draft.AdministratorAddresses.Select(sign));
                chain.ValidatePendingTransactions(pending.Concat(new[] { tx })); return tx;
            } finally { foreach (var key in keys.Values) key.Dispose(); }
        }
        static void Sign(Transaction tx, Dictionary<string, RSACryptoServiceProvider> keys) {
            NativeBridge.Authorize(tx, (address, payload) => Convert.ToBase64String(keys[address].SignData(payload, "SHA256")));
        }
        public static Transaction Register(Blockchain chain, NativeConfig config, string[] privateKeys) {
            ValidateConfig(config);
            if (config.chainId == 56) throw new InvalidOperationException("Mainnet registration requires separate operator signatures.");
            var existing = chain.GetTokenRuntime(config.nativeToken);
            if (existing != null) { VerifyToken(chain, config); return null; }
            var keys = new Dictionary<string, RSACryptoServiceProvider>();
            try {
                foreach (var xml in privateKeys) { var key = new RSACryptoServiceProvider(); key.PersistKeyInCsp = false; key.FromXmlString(xml); keys.Add(Hash(key.ToXmlString(false)), key); }
                var tx = NativeBridge.PrepareRegistration(chain, config.chainId, config.asset, config.bridge, config.nativeDecimals,
                    config.nativeSymbol, long.Parse(config.reserveCap, CultureInfo.InvariantCulture), keys.Values.Select(k => k.ToXmlString(false)));
                Sign(tx, keys); chain.ValidatePendingTransactions(new[] { tx }); return tx;
            } finally { foreach (var key in keys.Values) key.Dispose(); }
        }
        public static Transaction Configure(Blockchain chain, NativeConfig config, string[] founderKeys) {
            ValidateConfig(config);
            var keys = new Dictionary<string, RSACryptoServiceProvider>();
            try {
                foreach (string xml in founderKeys) { var rsa = new RSACryptoServiceProvider(); rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); keys.Add(Hash(rsa.ToXmlString(false)), rsa); }
                var policy = new TokenPolicy { Mintable = true, MaximumSupply = long.Parse(config.reserveCap, CultureInfo.InvariantCulture),
                    Burnable = true, Pausable = true, Administrators = config.nativeAdministrators.ToList(), RequiredSignatures = config.nativeThreshold,
                    BridgeBinding = NativeBridge.Binding(config.chainId, config.asset, config.bridge), Description = "USDT BNB em custódia. Emissão e resgate autorizados pela maioria dos operadores da ponte." };
                var draft = TokenManagement.Prepare(chain, new Transaction[0], keys.Values.Select(k => k.ToXmlString(false)), config.nativeToken,
                    "bridge-configure", 0, null, keys.Keys.First(), 0, null, policy);
                var tx = draft.Complete(new string[0], draft.AdministratorAddresses.Select(address => Convert.ToBase64String(keys[address].SignData(draft.SigningPayload, "SHA256"))));
                chain.ValidatePendingTransactions(new[] { tx }); return tx;
            } finally { foreach (var key in keys.Values) key.Dispose(); }
        }
        public static T Read<T>(string path) { using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        public static Blockchain LoadChain(string path) {
            // Public desktop Blockchain.json (schema 5) or a raw DataContract Block[] export.
            string json = File.ReadAllText(path).TrimStart();
            if (json.StartsWith("[", StringComparison.Ordinal)) return new Blockchain(Read<Block[]>(path));
            var state = Read<StoredNetwork>(path);
            if (state.Data == null) return new Blockchain(state.Blocks);
            byte[] data = Convert.FromBase64String(state.Data);
            using (var sha = SHA256.Create()) if (Hex(sha.ComputeHash(data)) != state.Sha256) throw new InvalidOperationException("Native snapshot checksum mismatch.");
            using (var stream = new MemoryStream(data)) return new Blockchain(((StoredData)new DataContractJsonSerializer(typeof(StoredData)).ReadObject(stream)).Blocks);
        }
        [DataContract] public sealed class Proposal {
            [DataMember] public string kind;
            [DataMember] public Transaction draft;
            [DataMember] public DepositEvidence deposit;
            [DataMember] public List<NativeSignature> signatures;
        }
        [DataContract] public sealed class NativeSignature {
            [DataMember] public string address;
            [DataMember] public string signature;
        }
        public static Transaction DraftProposal(Blockchain chain, NativeConfig config, Proposal request) {
            ValidateProduction(chain, config);
            Transaction tx;
            if (request.kind == "register") {
                if (chain.GetTokenRuntime(config.nativeToken) != null) throw new InvalidOperationException("Representation is already registered.");
                tx = NativeBridge.PrepareRegistration(chain, config.chainId, config.asset, config.bridge, config.nativeDecimals,
                    config.nativeSymbol, long.Parse(config.reserveCap, CultureInfo.InvariantCulture), config.nativeSigningPublicKeys);
            } else if (request.kind == "mint") {
                VerifyToken(chain, config);
                var evidence = request.deposit;
                if (evidence == null || evidence.depositId == null || !evidence.depositId.StartsWith("0x") || !HexAddress(evidence.depositId.Substring(2), 64) ||
                    evidence.blockHash == null || !evidence.blockHash.StartsWith("0x") || !HexAddress(evidence.blockHash.Substring(2), 64) ||
                    !HexAddress(evidence.recipient, 64) || evidence.recipient == new string('0', 64) || chain.HasBridgeDeposit(config.nativeToken, evidence.depositId.Substring(2)))
                    throw new InvalidOperationException("Invalid or already claimed deposit evidence.");
                var draft = TokenManagement.Prepare(chain, new Transaction[0], config.nativeSigningPublicKeys, config.nativeToken,
                    "mint", long.Parse(evidence.amount, CultureInfo.InvariantCulture), evidence.recipient, Hash(config.nativeSigningPublicKeys[0]), 0, null, null,
                    evidence.depositId.Substring(2), evidence.blockHash.Substring(2));
                tx = draft.Preview;
            } else if (request.kind == "pause" || request.kind == "resume") {
                VerifyToken(chain, config);
                tx = TokenManagement.Prepare(chain, new Transaction[0], config.nativeSigningPublicKeys, config.nativeToken,
                    request.kind, 0, null, Hash(config.nativeSigningPublicKeys[0]), 0).Preview;
            } else throw new InvalidOperationException("Expected mint, register, pause or resume proposal.");
            return tx;
        }
        public static string ReviewProposal(Blockchain chain, NativeConfig config, Proposal request) {
            var expected = DraftProposal(chain, config, request);
            if (request.draft == null || request.draft.Inputs.Count != 0 || request.draft.TokenAuthorizations == null ||
                DateTime.UtcNow.Ticks - request.draft.TimestampUtcTicks > TimeSpan.FromMinutes(10).Ticks ||
                request.draft.TimestampUtcTicks > DateTime.UtcNow.AddMinutes(1).Ticks)
                throw new InvalidOperationException("Invalid or expired proposal.");
            expected.TimestampUtcTicks = request.draft.TimestampUtcTicks;
            if (NativeBridge.Payload(expected) != NativeBridge.Payload(request.draft)) throw new InvalidOperationException("Proposal differs from independent native policy or deposit evidence.");
            return NativeBridge.Payload(expected);
        }
        public static Transaction CompleteProposal(Blockchain chain, NativeConfig config, Proposal request) {
            ReviewProposal(chain, config, request);
            if (request.signatures == null || request.signatures.Select(s => s.address).Distinct().Count() != request.signatures.Count)
                throw new InvalidOperationException("Missing or duplicate operator signatures.");
            var tx = NativeBridge.Authorize(request.draft, (address, payload) => request.signatures.Single(s => s.address == address).signature);
            chain.ValidatePendingTransactions(new[] { tx }); return tx;
        }
        [DataContract] public sealed class ProductionStatus {
            [DataMember] public long supply;
            [DataMember] public long issued;
            [DataMember] public long revision;
            [DataMember] public bool paused;
        }
        static void Print(object value) { if (value == null) { Console.WriteLine("null"); return; } using (var stream = new MemoryStream()) { new DataContractJsonSerializer(value.GetType()).WriteObject(stream, value); Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray())); } }
        public static int Main(string[] args) {
            try {
                if (args.Length == 1 && args[0] == "network") { Console.WriteLine(Blockchain.NetworkId); return 0; }
                if (args.Length < 4) throw new ArgumentException("verify|bind|operation <Blockchain.json> <config.json> <request.json> [keys.json]");
                var chain = LoadChain(args[1]); var config = Read<NativeConfig>(args[2]); ValidateConfig(config);
                if (config.mode == "production") ValidateProduction(chain, config);
                if (args[0] == "production-status") {
                    ValidateProduction(chain, config); VerifyToken(chain, config); var state = chain.GetTokenRuntime(config.nativeToken);
                    Print(new ProductionStatus { supply = state.Supply, issued = state.Issued, revision = state.Revision, paused = state.Paused });
                }
                else if (args[0] == "production-draft") Print(DraftProposal(chain, config, Read<Proposal>(args[3])));
                else if (args[0] == "production-review") Print(ReviewProposal(chain, config, Read<Proposal>(args[3])));
                else if (args[0] == "production-complete") Print(CompleteProposal(chain, config, Read<Proposal>(args[3])));
                else if (args[0] == "register" && args.Length == 5) Print(Register(chain, config, Read<string[]>(args[4])));
                else if (args[0] == "configure" && args.Length == 5) Print(Configure(chain, config, Read<string[]>(args[4])));
                else if (args[0] == "verify") Print(VerifyBurn(chain, config, Read<Binding>(args[3])));
                else if (args[0] == "bind" && args.Length == 5) Print(SignBinding(chain, Read<Binding>(args[3]), Read<string[]>(args[4])));
                else if (args[0] == "operation" && args.Length == 5) Print(PrepareOperation(chain, config, Read<ReleaseRequest>(args[3]), Read<string[]>(args[4])));
                else throw new ArgumentException("Unknown native bridge command.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}
