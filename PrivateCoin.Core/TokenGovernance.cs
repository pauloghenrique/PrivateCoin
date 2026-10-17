using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Security.Cryptography;

namespace PrivateCoin.Core
{
    [DataContract]
    public sealed class TokenPolicy
    {
        [DataMember(Order = 1)] public bool Mintable { get; set; }
        [DataMember(Order = 2)] public long MaximumSupply { get; set; }
        [DataMember(Order = 3)] public bool Burnable { get; set; }
        [DataMember(Order = 4)] public bool Pausable { get; set; }
        [DataMember(Order = 5)] public List<string> Administrators { get; set; } = new List<string>();
        [DataMember(Order = 6)] public int RequiredSignatures { get; set; } = 1;
        [DataMember(Order = 7)] public string Description { get; set; } = "";
        [DataMember(Order = 8)] public string LogoUrl { get; set; } = "";
        [DataMember(Order = 9)] public string Website { get; set; } = "";
        [DataMember(Order = 10)] public string SocialUrl { get; set; } = "";
        [DataMember(Order = 11)] public int VestingStartHeight { get; set; }
        [DataMember(Order = 12)] public int VestingIntervalBlocks { get; set; }
        [DataMember(Order = 13)] public int VestingTranches { get; set; }
        // Confidential transfers require a separate proof system; never accept a
        // flag which would leave plaintext values or participants on the chain.
        [DataMember(Order = 14)] public string Privacy { get; set; } = "public";
        [DataMember(Order = 15, EmitDefaultValue = false)] public int AutoBurnBasisPoints { get; set; }
        [DataMember(Order = 16, EmitDefaultValue = false)] public long AutoBurnFixedAtomic { get; set; }
        public long CalculateTransferBurn(long amount)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (AutoBurnBasisPoints < 0 || AutoBurnBasisPoints > 10000 || AutoBurnFixedAtomic < 0 || AutoBurnBasisPoints != 0 && AutoBurnFixedAtomic != 0)
                throw new InvalidOperationException("Invalid automatic burn policy.");
            // Split the multiplication to avoid overflow at Int64.MaxValue.
            return AutoBurnFixedAtomic != 0 ? AutoBurnFixedAtomic : checked(amount / 10000 * AutoBurnBasisPoints + amount % 10000 * AutoBurnBasisPoints / 10000);
        }
        public bool HasAutomaticBurn => AutoBurnBasisPoints != 0 || AutoBurnFixedAtomic != 0;

        public string Canonical()
        {
            var fields = new[] { Mintable ? "1" : "0", MaximumSupply.ToString(CultureInfo.InvariantCulture), Burnable ? "1" : "0", Pausable ? "1" : "0",
                string.Join(",", Administrators ?? new List<string>()), RequiredSignatures.ToString(CultureInfo.InvariantCulture),
                Description, LogoUrl, Website, SocialUrl, VestingStartHeight.ToString(CultureInfo.InvariantCulture),
                VestingIntervalBlocks.ToString(CultureInfo.InvariantCulture), VestingTranches.ToString(CultureInfo.InvariantCulture), Privacy };
            return string.Concat(fields.Select(field => "|" + (field == null ? -1 : field.Length).ToString(CultureInfo.InvariantCulture) + ":" + field)) +
                (HasAutomaticBurn ? "|auto-burn-v1:" + AutoBurnBasisPoints.ToString(CultureInfo.InvariantCulture) + ":" + AutoBurnFixedAtomic.ToString(CultureInfo.InvariantCulture) : "");
        }
        public TokenPolicy Copy()
        {
            var copy = (TokenPolicy)MemberwiseClone();
            copy.Administrators = Administrators == null ? null : new List<string>(Administrators);
            return copy;
        }
        internal static bool Address(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        internal void Validate(long supply)
        {
            CalculateTransferBurn(1);
            if (MaximumSupply < supply || !Mintable && MaximumSupply != supply ||
                Administrators == null || Administrators.Count < 1 || Administrators.Count > 16 ||
                Administrators.Any(address => !Address(address)) || Administrators.Distinct(StringComparer.Ordinal).Count() != Administrators.Count ||
                RequiredSignatures < 1 || RequiredSignatures > Administrators.Count)
                throw new InvalidOperationException("Invalid token supply cap or administrator threshold.");
            if (Description == null || Description.Length > 1000 || Description.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') ||
                !SafeUrl(LogoUrl) || !SafeUrl(Website) || !SafeUrl(SocialUrl))
                throw new InvalidOperationException("Invalid token presentation fields. Use HTTPS URLs.");
            if (VestingTranches < 0 || VestingTranches > 48 || VestingTranches > supply ||
                (VestingTranches == 0 ? VestingStartHeight != 0 || VestingIntervalBlocks != 0 :
                VestingStartHeight < 1 || VestingIntervalBlocks < 1 ||
                (long)VestingStartHeight + (long)(VestingTranches - 1) * VestingIntervalBlocks > int.MaxValue))
                throw new InvalidOperationException("Invalid token vesting schedule.");
            if (Privacy != "public") throw new InvalidOperationException("Confidential token transfers are not implemented by this consensus.");
        }
        private static bool SafeUrl(string value)
        {
            Uri uri;
            return value != null && (value == "" || value.Length <= 300 && !value.Any(char.IsControl) &&
                Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo));
        }
    }

    [DataContract]
    public sealed class TokenCommand
    {
        [DataMember(Order = 1)] public string TokenId { get; set; }
        [DataMember(Order = 2)] public string Operation { get; set; }
        [DataMember(Order = 3)] public long Amount { get; set; }
        [DataMember(Order = 4)] public long Revision { get; set; }
        internal string Canonical() => TokenId + ":" + Operation + ":" + Amount.ToString(CultureInfo.InvariantCulture) + ":" + Revision.ToString(CultureInfo.InvariantCulture);
    }
    [DataContract]
    public sealed class TokenAuthorization
    {
        [DataMember(Order = 1)] public string PublicKey { get; set; }
        [DataMember(Order = 2)] public string Signature { get; set; }
    }
    public sealed class TokenRuntime
    {
        public TokenDefinition Token { get; internal set; }
        public long Supply { get; internal set; }
        public long Issued { get; internal set; }
        public bool Paused { get; internal set; }
        public long Revision { get; internal set; }
    }

    internal static class TokenGovernance
    {
        private static string Key(string id) => "@token-state:" + id;
        internal static TokenRuntime State(IDictionary<string, UnspentOutput> utxo, string id)
        {
            UnspentOutput item;
            return id != null && utxo.TryGetValue(Key(id), out item) ? item.TokenRuntime : null;
        }
        internal static void ValidateTransferConservation(Transaction tx, IDictionary<string, UnspentOutput> utxo,
            IDictionary<string, long> inputs, IDictionary<string, long> outputs)
        {
            var terms = tx.TokenTransferTerms;
            bool taxed = inputs.Keys.Any(id => State(utxo, id)?.Token.Policy?.HasAutomaticBurn == true);
            if (terms == null && !taxed)
            {
                if (inputs.Count == 0 || inputs.Count != outputs.Count || inputs.Any(pair => !outputs.ContainsKey(pair.Key) || outputs[pair.Key] != pair.Value))
                    throw new InvalidOperationException("Token transfers must conserve each asset independently.");
                return;
            }
            var policy = terms == null ? null : State(utxo, terms.TokenId)?.Token.Policy;
            if (!taxed || policy == null || !policy.HasAutomaticBurn || inputs.Count != 1 || outputs.Count != 1 ||
                !inputs.ContainsKey(terms.TokenId) || !outputs.ContainsKey(terms.TokenId) || terms.Amount <= 0 ||
                tx.Outputs[0].AssetId != terms.TokenId || tx.Outputs[0].Amount != terms.Amount ||
                terms.BurnAmount != policy.CalculateTransferBurn(terms.Amount) ||
                inputs[terms.TokenId] != checked(outputs[terms.TokenId] + terms.BurnAmount))
                throw new InvalidOperationException("Transfer does not pay the automatic token burn.");
            var owners = new HashSet<string>(tx.Inputs.Select(input => Crypto.Sha256(input.PublicKey)), StringComparer.Ordinal);
            if (tx.Outputs.Skip(1).Any(output => output.AssetId != null && !owners.Contains(output.OneTimeAddress)))
                throw new InvalidOperationException("Token change must return to a signing input owner.");
        }
        internal static void Record(Transaction tx, IDictionary<string, UnspentOutput> utxo)
        {
            TokenRuntime next = null;
            if (tx.Token != null && tx.Kind == TransactionKind.TokenCreate)
                next = new TokenRuntime { Token = tx.Token, Supply = tx.Token.Supply, Issued = tx.Token.Supply };
            else if (tx.Kind == TransactionKind.TokenManage && tx.TokenCommand != null)
            {
                var prior = State(utxo, tx.TokenCommand.TokenId);
                if (prior == null) throw new InvalidOperationException("Unknown token.");
                var command = tx.TokenCommand;
                next = new TokenRuntime { Token = prior.Token, Supply = prior.Supply, Issued = prior.Issued,
                    Paused = prior.Paused, Revision = checked(prior.Revision + 1) };
                if (command.Operation == "mint") { next.Supply = checked(next.Supply + command.Amount); next.Issued = checked(next.Issued + command.Amount); }
                if (command.Operation == "burn") next.Supply = checked(next.Supply - command.Amount);
                if (command.Operation == "pause") next.Paused = true;
                if (command.Operation == "resume") next.Paused = false;
            }
            else if (tx.Kind == TransactionKind.TokenTransfer && tx.TokenTransferTerms != null)
            {
                var prior = State(utxo, tx.TokenTransferTerms.TokenId);
                if (prior?.Token.Policy == null) throw new InvalidOperationException("Unknown automatic burn token.");
                next = new TokenRuntime { Token = prior.Token, Supply = checked(prior.Supply - prior.Token.Policy.CalculateTransferBurn(tx.TokenTransferTerms.Amount)),
                    Issued = prior.Issued, Paused = prior.Paused, Revision = prior.Revision };
            }
            if (next != null) utxo[Key(next.Token.Id)] = new UnspentOutput { TransactionId = Key(next.Token.Id), OutputIndex = -1,
                Output = new TransactionOutput { Amount = 0, OneTimeAddress = "@token-state" }, TokenRuntime = next };
        }
        internal static void Validate(Transaction tx, IDictionary<string, UnspentOutput> utxo, int height, int version)
        {
            if (tx.Token?.Policy != null || tx.TokenCommand != null || tx.TokenAuthorizations != null || tx.TokenTransferTerms != null || tx.Outputs.Any(output => output.UnlockHeight != 0))
                if (version < 20) throw new InvalidOperationException("Token policies require consensus v20.");
            if (tx.TokenCommand == null && (tx.Kind == TransactionKind.TokenManage || tx.TokenAuthorizations != null))
                throw new InvalidOperationException("Missing token command.");
            if (tx.TokenTransferTerms != null && tx.Kind != TransactionKind.TokenTransfer)
                throw new InvalidOperationException("Automatic burn terms require a direct token transfer.");
            foreach (var output in tx.Outputs)
                if (output.UnlockHeight != 0 && (tx.Kind != TransactionKind.TokenCreate || tx.Token?.Policy == null || output.AssetId != tx.Token.Id))
                    throw new InvalidOperationException("Only a token vesting creation may lock outputs.");
            foreach (var input in tx.Inputs)
            {
                UnspentOutput source;
                if (!utxo.TryGetValue(input.TransactionId + ":" + input.OutputIndex.ToString(CultureInfo.InvariantCulture), out source)) continue;
                if (source.TokenRuntime != null) throw new InvalidOperationException("Token state is not spendable.");
                if (source.Output.UnlockHeight > height) throw new InvalidOperationException("Token tranche is not unlocked yet.");
                if (source.Output.AssetId != null && State(utxo, source.Output.AssetId)?.Paused == true)
                    throw new InvalidOperationException("Token transfers are paused.");
            }
            foreach (var output in tx.Outputs.Where(output => output.AssetId != null))
                if (State(utxo, output.AssetId)?.Paused == true) throw new InvalidOperationException("Token transfers are paused.");
            if (tx.Kind == TransactionKind.TokenCreate && tx.Token?.Policy != null)
            {
                var policy = tx.Token.Policy;
                policy.Validate(tx.Token.Supply);
                var outputs = tx.Outputs.Where(output => output.AssetId == tx.Token.Id).ToArray();
                int count = policy.VestingTranches == 0 ? 1 : policy.VestingTranches;
                if (outputs.Length != count || policy.VestingTranches != 0 && policy.VestingStartHeight <= height)
                    throw new InvalidOperationException("Vesting must start at a future block.");
                for (int i = 0; i < count; i++)
                {
                    long amount = tx.Token.Supply / count + (i < tx.Token.Supply % count ? 1 : 0);
                    int unlock = policy.VestingTranches == 0 ? 0 : checked(policy.VestingStartHeight + i * policy.VestingIntervalBlocks);
                    if (outputs[i].Amount != amount || outputs[i].UnlockHeight != unlock || outputs[i].OneTimeAddress != outputs[0].OneTimeAddress)
                        throw new InvalidOperationException("Vesting outputs do not match the schedule.");
                }
            }
            if (tx.TokenCommand == null) return;
            var cmd = tx.TokenCommand;
            var state = State(utxo, cmd.TokenId);
            if (tx.Kind != TransactionKind.TokenManage || state?.Token.Policy == null || cmd.Revision != state.Revision ||
                !TokenPolicy.Address(cmd.TokenId) || tx.Token != null || tx.TokenAuthorizations == null)
                throw new InvalidOperationException("Invalid or stale token command.");
            var rules = state.Token.Policy;
            if (cmd.Operation == "mint")
            {
                // Cap cumulative issuance: burning does not reopen minting capacity.
                if (!rules.Mintable || cmd.Amount <= 0 || cmd.Amount > rules.MaximumSupply - state.Issued)
                    throw new InvalidOperationException("Token issuance exceeds its policy.");
            }
            else if (cmd.Operation == "burn")
            {
                if (!rules.Burnable || cmd.Amount <= 0 || cmd.Amount > state.Supply) throw new InvalidOperationException("Token burning is not permitted.");
            }
            else if (cmd.Operation == "pause" || cmd.Operation == "resume")
            {
                if (!rules.Pausable || cmd.Amount != 0 || state.Paused == (cmd.Operation == "pause")) throw new InvalidOperationException("Invalid pause transition.");
            }
            else throw new InvalidOperationException("Unknown token command.");
            var signers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proof in tx.TokenAuthorizations)
            {
                string address = Crypto.Sha256(proof.PublicKey);
                if (!rules.Administrators.Contains(address) || !signers.Add(address)) throw new InvalidOperationException("Invalid or duplicate administrator.");
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(proof.PublicKey);
                    if (proof.Signature == null || !rsa.VerifyData(Encoding.UTF8.GetBytes(tx.SigningPayload()), CryptoConfig.MapNameToOID(Crypto.SignatureHashAlgorithm), Convert.FromBase64String(proof.Signature)))
                        throw new InvalidOperationException("Invalid token administrator signature.");
                }
            }
            if (cmd.Operation == "burn")
            {
                // A holder can only burn tokens owned by its signed UTXO inputs.
                if (signers.Count != 0) throw new InvalidOperationException("Burns use holder input signatures.");
            }
            else if (signers.Count < rules.RequiredSignatures) throw new InvalidOperationException("Not enough administrator signatures.");
        }
    }
}
