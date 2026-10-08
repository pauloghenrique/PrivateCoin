using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace PrivateCoin.Core
{
    public enum TransactionKind
    {
        Transfer = 0,
        StakeLock = 1,
        StakeUnlock = 2,
        TokenCreate = 3,
        TokenTransfer = 4
    }

    [DataContract]
    public sealed class TokenDefinition
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        [DataMember(Order = 3)] public string Symbol { get; set; }
        [DataMember(Order = 4)] public int Decimals { get; set; }
        [DataMember(Order = 5)] public long Supply { get; set; }

        internal static string IdFor(TransactionInput input)
        {
            return Crypto.Sha256("povix-token-v1|" + input.TransactionId + "|" +
                input.OutputIndex.ToString(CultureInfo.InvariantCulture));
        }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64 || Name.Any(char.IsControl) ||
                string.IsNullOrEmpty(Symbol) || Symbol.Length > 10 ||
                Symbol.Any(c => c < 'A' || c > 'Z') || Decimals < 0 || Decimals > 8 || Supply <= 0)
                throw new InvalidOperationException("Invalid token: name up to 64 characters, symbol A-Z up to 10, decimals 0-8, positive atomic supply required.");
        }
    }

    [DataContract]
    public sealed class BlockValidator
    {
        [DataMember(Order = 1)] public string ValidatorId { get; set; }
        [DataMember(Order = 2)] public string RewardAddress { get; set; }
        [DataMember(Order = 3)] public long LockedAmount { get; set; }
        [DataMember(Order = 4)] public bool IsCreator { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public List<string> OwnedAddresses { get; set; }
        [DataMember(Order = 6)] public string PublicKey { get; set; }
        [DataMember(Order = 7)] public string VoteSignature { get; set; }
    }

    [DataContract]
    public sealed class TransactionInput
    {
        [DataMember(Order = 1)] public string TransactionId { get; set; }
        [DataMember(Order = 2)] public int OutputIndex { get; set; }
        [DataMember(Order = 3)] public string PublicKey { get; set; }
        [DataMember(Order = 4)] public string Signature { get; set; }
    }

    [DataContract]
    public sealed class TransactionOutput
    {
        [DataMember(Order = 1)] public long Amount { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public string AssetId { get; set; }
        // A one-use hash. No persistent wallet address is ever written to the chain.
        [DataMember(Order = 2)] public string OneTimeAddress { get; set; }
    }

    [DataContract]
    public sealed class Transaction
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public long TimestampUtcTicks { get; set; }
        [DataMember(Order = 3)] public List<TransactionInput> Inputs { get; set; } = new List<TransactionInput>();
        [DataMember(Order = 4)] public List<TransactionOutput> Outputs { get; set; } = new List<TransactionOutput>();
        [DataMember(Order = 5, EmitDefaultValue = false)] public long Fee { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public TransactionKind Kind { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public string ValidatorPublicKey { get; set; }
        [DataMember(Order = 8, EmitDefaultValue = false)] public string ValidatorRewardAddress { get; set; }
        [DataMember(Order = 9, EmitDefaultValue = false)] public List<string> ValidatorOwnedAddresses { get; set; }

        [DataMember(Order = 10, EmitDefaultValue = false)] public TokenDefinition Token { get; set; }

        internal string SigningPayload()
        {
            var value = new StringBuilder(TimestampUtcTicks.ToString(CultureInfo.InvariantCulture));
            foreach (var input in Inputs)
                value.Append('|').Append(input.TransactionId).Append(':').Append(input.OutputIndex.ToString(CultureInfo.InvariantCulture));
            foreach (var output in Outputs)
                value.Append('|').Append(output.Amount.ToString(CultureInfo.InvariantCulture)).Append(':').Append(output.OneTimeAddress);
            // Preserve the identifiers of fee-free transactions created by older clients.
            if (Fee != 0) value.Append("|fee:").Append(Fee.ToString(CultureInfo.InvariantCulture));
            if (Kind != TransactionKind.Transfer)
                value.Append("|kind:").Append(((int)Kind).ToString(CultureInfo.InvariantCulture))
                    .Append("|validator:").Append(ValidatorPublicKey).Append("|reward:").Append(ValidatorRewardAddress)
                    .Append("|owned:").Append(string.Join(",", ValidatorOwnedAddresses ?? new List<string>()));
            // Length-prefix new fields so delimiters in token names cannot change the encoding.
            if (Token != null || Outputs.Any(output => output.AssetId != null))
            {
                value.Append("|assets-v1|");
                foreach (var output in Outputs) AppendField(value, output.AssetId);
                if (Token != null)
                {
                    AppendField(value, Token.Id); AppendField(value, Token.Name); AppendField(value, Token.Symbol);
                    value.Append('|').Append(Token.Decimals.ToString(CultureInfo.InvariantCulture))
                        .Append('|').Append(Token.Supply.ToString(CultureInfo.InvariantCulture));
                }
            }
            return value.ToString();
        }

        private static void AppendField(StringBuilder value, string field)
        {
            value.Append('|').Append(field == null ? -1 : field.Length).Append(':').Append(field);
        }

        internal string CalculateId()
        {
            return Crypto.Sha256(SigningPayload() + "|" + string.Join("|", Inputs.Select(i => i.Signature ?? string.Empty)));
        }
    }

    [DataContract]
    public sealed class Block
    {
        [DataMember(Order = 1)] public int Height { get; set; }
        [DataMember(Order = 2)] public string PreviousHash { get; set; }
        [DataMember(Order = 3)] public long TimestampUtcTicks { get; set; }
        [DataMember(Order = 4)] public long Nonce { get; set; }
        [DataMember(Order = 5)] public List<Transaction> Transactions { get; set; } = new List<Transaction>();
        [DataMember(Order = 6)] public string Hash { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public List<BlockValidator> Validators { get; set; }

        // Certificates are signed over the block hash and are not part of that hash.
        [DataMember(Order = 8, EmitDefaultValue = false)] public List<FinalityVote> FinalityVotes { get; set; }

        internal string CalculateHash()
        {
            string validatorProof = Validators == null ? string.Empty : string.Join("|", Validators.Select(v =>
                v.ValidatorId + ":" + v.RewardAddress + ":" + v.LockedAmount.ToString(CultureInfo.InvariantCulture) + ":" + v.IsCreator +
                (v.OwnedAddresses == null ? string.Empty : ":addresses:" + string.Join(",", v.OwnedAddresses)) +
                ":key:" + v.PublicKey + ":vote:" + v.VoteSignature));
            return Crypto.Sha256(Height.ToString(CultureInfo.InvariantCulture) + "|" + PreviousHash + "|" +
                TimestampUtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + Nonce.ToString(CultureInfo.InvariantCulture) + "|" +
                string.Join("|", Transactions.Select(t => t.Id)) + (Validators == null ? string.Empty : "|pos|" + validatorProof));
        }
    }

    public sealed class UnspentOutput
    {
        public string TransactionId { get; internal set; }
        public int OutputIndex { get; internal set; }
        public TransactionOutput Output { get; internal set; }
        public TransactionKind TransactionKind { get; internal set; }
        public string ValidatorPublicKey { get; internal set; }
        public string ValidatorRewardAddress { get; internal set; }
        public IReadOnlyCollection<string> ValidatorOwnedAddresses { get; internal set; }
    }

}
