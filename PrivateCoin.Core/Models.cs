using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace PrivateCoin.Core
{
    [DataContract]
    public sealed class BlockValidator
    {
        [DataMember(Order = 1)] public string ValidatorId { get; set; }
        [DataMember(Order = 2)] public string RewardAddress { get; set; }
        [DataMember(Order = 3)] public long LockedAmount { get; set; }
        [DataMember(Order = 4)] public bool IsCreator { get; set; }
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

        internal string SigningPayload()
        {
            var value = new StringBuilder(TimestampUtcTicks.ToString(CultureInfo.InvariantCulture));
            foreach (var input in Inputs)
                value.Append('|').Append(input.TransactionId).Append(':').Append(input.OutputIndex.ToString(CultureInfo.InvariantCulture));
            foreach (var output in Outputs)
                value.Append('|').Append(output.Amount.ToString(CultureInfo.InvariantCulture)).Append(':').Append(output.OneTimeAddress);
            return value.ToString();
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

        internal string CalculateHash()
        {
            string validatorProof = Validators == null ? string.Empty : string.Join("|", Validators.Select(v =>
                v.ValidatorId + ":" + v.RewardAddress + ":" + v.LockedAmount.ToString(CultureInfo.InvariantCulture) + ":" + v.IsCreator));
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
    }
}
