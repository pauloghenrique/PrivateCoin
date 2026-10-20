using System;
using System.Globalization;
using System.Linq;

namespace PrivateCoin.Core
{
    /// <summary>Federated bridge registration; external deposit verification is performed by operators.</summary>
    public static class NativeBridge
    {
        internal static string ClaimKey(string binding, string depositId) => "@bridge-deposit:" + Crypto.Sha256(binding + "|" + depositId);
        public static string Payload(Transaction tx) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tx.SigningPayload()));
        internal static void ValidateClaim(Transaction tx, System.Collections.Generic.IDictionary<string, UnspentOutput> utxo, TokenPolicy policy, int version) {
            var cmd = tx.TokenCommand;
            bool mainnet = policy.BridgeBinding?.StartsWith("56:", StringComparison.Ordinal) == true;
            if (!mainnet && cmd.BridgeDepositId == null && cmd.BridgeBlockHash == null) return;
            if (version < 38 || !IsAssetId(cmd.TokenId) || policy.BridgeBinding == null ||
                !TokenPolicy.Address(cmd.BridgeDepositId) || !cmd.BridgeDepositId.Any(c => c != '0') ||
                !TokenPolicy.Address(cmd.BridgeBlockHash) || !cmd.BridgeBlockHash.Any(c => c != '0') ||
                utxo.ContainsKey(ClaimKey(policy.BridgeBinding, cmd.BridgeDepositId)))
                throw new InvalidOperationException("Missing, invalid or already claimed bridge deposit.");
        }
        public static bool IsRepresentationId(string id) => id != null && id.Length == 64 && id.StartsWith("b9b0", StringComparison.Ordinal) && TokenPolicy.Address(id);
        public static bool IsAssetId(string id) => id == InitialLaunch.PusdtTokenId || IsRepresentationId(id);
        public static string RepresentationId(long chainId, string asset) {
            Binding(chainId, asset, "0x" + new string('1', 40));
            return "b9b0" + Crypto.Sha256("povix-bnb-token-v1|" + chainId.ToString(CultureInfo.InvariantCulture) + "|" + asset).Substring(4);
        }
        public static bool IsRegistration(Transaction tx) => tx?.Kind == TransactionKind.TokenManage &&
            tx.TokenCommand?.Operation == "bridge-register" && IsRepresentationId(tx.TokenCommand.TokenId);
        public static Transaction PrepareRegistration(Blockchain chain, long chainId, string asset, string vault, int decimals,
            string symbol, long cap, System.Collections.Generic.IEnumerable<string> publicKeys) {
            var root = chain.GetTokenRuntime(InitialLaunch.PusdtTokenId)?.Token.Policy;
            if (root?.BridgeBinding == null) throw new InvalidOperationException("Configure the protocol bridge first.");
            var token = new TokenDefinition { Id = RepresentationId(chainId, asset), Name = "BNB " + asset, Symbol = symbol,
                Supply = 0, Decimals = decimals, Policy = new TokenPolicy { Mintable = true, Burnable = true, Pausable = true,
                    MaximumSupply = cap, Administrators = root.Administrators.ToList(), RequiredSignatures = root.RequiredSignatures,
                    BridgeBinding = Binding(chainId, asset, vault) } };
            var keys = publicKeys.Distinct().ToDictionary(Crypto.Sha256, k => k);
            var admins = root.Administrators.Where(keys.ContainsKey).OrderBy(k => k, StringComparer.Ordinal).Take(root.RequiredSignatures).ToArray();
            if (admins.Length != root.RequiredSignatures) throw new InvalidOperationException("Bridge registration requires the protocol administrator quorum.");
            return new Transaction { Kind = TransactionKind.TokenManage, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                Token = token, TokenCommand = new TokenCommand { TokenId = token.Id, Operation = "bridge-register" },
                TokenAuthorizations = admins.Select(a => new TokenAuthorization { PublicKey = keys[a] }).ToList() };
        }
        public static Transaction Authorize(Transaction tx, Func<string, byte[], string> signer) {
            if (!IsFeeFree(tx) || tx.Inputs.Count != 0 || tx.TokenAuthorizations == null) throw new InvalidOperationException("Expected an administrator bridge operation.");
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(tx.SigningPayload());
            foreach (var proof in tx.TokenAuthorizations) proof.Signature = signer(Crypto.Sha256(proof.PublicKey), payload);
            tx.Id = tx.CalculateId(); return tx;
        }
        internal static TokenPolicy ValidateRegistration(Transaction tx, System.Collections.Generic.IDictionary<string, UnspentOutput> utxo, int version) {
            var token = tx.Token; var cmd = tx.TokenCommand;
            var root = TokenGovernance.State(utxo, InitialLaunch.PusdtTokenId)?.Token.Policy;
            if (version < 37 || !IsRegistration(tx) || token == null || token.Id != cmd.TokenId || token.Supply != 0 ||
                cmd.Amount != 0 || cmd.Revision != 0 || cmd.BridgePolicy != null || tx.Fee != 0 || tx.Inputs.Count != 0 || tx.Outputs.Count != 0 ||
                tx.ValidatorPublicKey != null || tx.ValidatorRewardAddress != null || tx.ValidatorOwnedAddresses != null ||
                tx.TokenAuthorizations == null || root?.BridgeBinding == null || TokenGovernance.State(utxo, token.Id) != null)
                throw new InvalidOperationException("Invalid or duplicate bridge representation registration.");
            ValidatePolicy(token.Policy);
            var parts = token.Policy.BridgeBinding.Split(':');
            var rootParts = root.BridgeBinding.Split(':');
            if (parts[0] != rootParts[0] || parts[1] == rootParts[1] || token.Id != RepresentationId(long.Parse(parts[0], CultureInfo.InvariantCulture), parts[1]) ||
                token.Decimals < 0 || token.Decimals > 8 || token.Name != "BNB " + parts[1] ||
                token.Symbol == null || token.Symbol.Length < 2 || token.Symbol.Length > 10 || token.Symbol[0] != 'p' ||
                token.Symbol.Skip(1).Any(c => !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9')) ||
                token.Policy.RequiredSignatures != root.RequiredSignatures || !token.Policy.Administrators.SequenceEqual(root.Administrators))
                throw new InvalidOperationException("Representation must bind the source asset and use the protocol bridge federation.");
            return root;
        }
        public static bool IsConfiguration(Transaction tx) => tx?.Kind == TransactionKind.TokenManage &&
            tx.TokenCommand?.TokenId == InitialLaunch.PusdtTokenId && tx.TokenCommand.Operation == "bridge-configure";
        public static bool IsFeeFree(Transaction tx) => tx?.Kind == TransactionKind.TokenManage && tx.Fee == 0 &&
            IsAssetId(tx.TokenCommand?.TokenId) &&
            (tx.TokenCommand.Operation == "burn" ? tx.Inputs.Count > 0 : tx.Inputs.Count == 0 &&
                (tx.TokenCommand.Operation == "mint" || tx.TokenCommand.Operation == "pause" || tx.TokenCommand.Operation == "resume" || tx.TokenCommand.Operation == "bridge-configure" || IsRegistration(tx)));
        public static string Binding(long chainId, string asset, string vault)
        {
            if ((chainId != 97 && chainId != 56 && chainId != 31337) || !BscAddress(asset) || !BscAddress(vault))
                throw new ArgumentException("Invalid BNB bridge binding.");
            return chainId.ToString(CultureInfo.InvariantCulture) + ":" + asset + ":" + vault;
        }
        private static bool BscAddress(string value) => value != null && value.Length == 42 && value.StartsWith("0x", StringComparison.Ordinal) &&
            value.Skip(2).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') && value.Skip(2).Any(c => c != '0');
        internal static void ValidatePolicy(TokenPolicy policy)
        {
            if (policy == null) throw new InvalidOperationException("Missing bridge policy.");
            policy.Validate(0);
            if (!policy.Mintable || !policy.Burnable || !policy.Pausable || policy.MaximumSupply <= 0 || policy.HasAutomaticBurn ||
                policy.VestingTranches != 0 || policy.Administrators.Count < 2 || policy.RequiredSignatures <= policy.Administrators.Count / 2)
                throw new InvalidOperationException("Bridge representations require at least two administrators and a strict majority and no transfer burn or vesting.");
            if (policy.BridgeBinding?.StartsWith("56:", StringComparison.Ordinal) == true && policy.Administrators.Count != 2 && (policy.Administrators.Count < 5 || policy.RequiredSignatures < 3))
                throw new InvalidOperationException("Mainnet bridge requires two administrators with both signatures, or at least five administrators with a majority of at least three.");
            var parts = (policy.BridgeBinding ?? "").Split(':'); long chainId;
            if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out chainId) || Binding(chainId, parts[1], parts[2]) != policy.BridgeBinding)
                throw new InvalidOperationException("Bridge representation must be bound to a BNB asset and vault.");
        }
    }
}
