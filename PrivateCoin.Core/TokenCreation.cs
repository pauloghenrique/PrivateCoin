using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>Prepares native token creation using public keys and externally supplied signatures.</summary>
    public static class TokenCreation
    {
        public static UnsignedTokenCreation Prepare(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, string name, string symbol, int decimals, long supply,
            string destinationAddress, string changeAddress, long fee)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (publicKeys == null) throw new ArgumentNullException(nameof(publicKeys));
            if (fee < Blockchain.TransferFeeStep || fee > Blockchain.MaximumTransferFee)
                throw new ArgumentOutOfRangeException(nameof(fee));
            if (!IsAddress(destinationAddress) || !IsAddress(changeAddress))
                throw new ArgumentException("A valid destination and change address are required.");
            var token = new TokenDefinition { Name = name, Symbol = symbol, Decimals = decimals, Supply = supply };
            token.Validate();
            var keys = publicKeys.Distinct(StringComparer.Ordinal).ToDictionary(Crypto.Sha256, key => key, StringComparer.Ordinal);
            if (!keys.ContainsKey(changeAddress)) throw new ArgumentException("Change must belong to the signing wallet.");
            var selected = new List<UnspentOutput>();
            long total = 0;
            foreach (var output in chain.GetSpendableOutputs(keys.Keys, pending))
            {
                selected.Add(output);
                total = checked(total + output.Output.Amount);
                if (total >= fee) break;
            }
            if (total < fee) throw new InvalidOperationException("Insufficient POVIX for the transaction fee.");
            var transaction = new Transaction { Kind = TransactionKind.TokenCreate, Token = token,
                TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee };
            foreach (var output in selected)
                transaction.Inputs.Add(new TransactionInput { TransactionId = output.TransactionId,
                    OutputIndex = output.OutputIndex, PublicKey = keys[output.Output.OneTimeAddress] });
            token.Id = TokenDefinition.IdFor(transaction.Inputs[0]);
            transaction.Outputs.Add(new TransactionOutput { Amount = supply, AssetId = token.Id, OneTimeAddress = destinationAddress });
            if (total > fee) transaction.Outputs.Add(new TransactionOutput { Amount = total - fee, OneTimeAddress = changeAddress });
            return new UnsignedTokenCreation(transaction, selected.Select(output => output.Output.OneTimeAddress).ToArray());
        }

        private static bool IsAddress(string address) => address != null && address.Length == 64 &&
            address.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    public sealed class UnsignedTokenCreation
    {
        private readonly Transaction transaction;
        public IReadOnlyList<string> InputAddresses { get; }
        public byte[] SigningPayload => Encoding.UTF8.GetBytes(transaction.SigningPayload());
        public TokenDefinition Token => new TokenDefinition { Id = transaction.Token.Id, Name = transaction.Token.Name,
            Symbol = transaction.Token.Symbol, Decimals = transaction.Token.Decimals, Supply = transaction.Token.Supply };
        public long Fee => transaction.Fee;
        public string DestinationAddress => transaction.Outputs[0].OneTimeAddress;
        public string ChangeAddress => transaction.Outputs.Count > 1 ? transaction.Outputs[1].OneTimeAddress : null;
        public long ChangeAmount => transaction.Outputs.Count > 1 ? transaction.Outputs[1].Amount : 0;
        public long TimestampUtcTicks => transaction.TimestampUtcTicks;
        public IReadOnlyList<TransactionInput> Inputs => transaction.Inputs.Select(input => new TransactionInput {
            TransactionId = input.TransactionId, OutputIndex = input.OutputIndex, PublicKey = input.PublicKey }).ToArray();

        internal UnsignedTokenCreation(Transaction transaction, string[] addresses)
        {
            this.transaction = transaction;
            InputAddresses = Array.AsReadOnly(addresses);
        }

        /// <summary>Attaches signatures. Always validate the result against the current blockchain.</summary>
        public Transaction Complete(IEnumerable<string> signatures)
        {
            if (signatures == null) throw new ArgumentNullException(nameof(signatures));
            string[] values = signatures.ToArray();
            if (values.Length != transaction.Inputs.Count || values.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("One signature is required for every input.", nameof(signatures));
            var copy = new Transaction { Kind = transaction.Kind, TimestampUtcTicks = transaction.TimestampUtcTicks,
                Fee = transaction.Fee, Token = Token,
                Inputs = transaction.Inputs.Select((input, index) => new TransactionInput { TransactionId = input.TransactionId,
                    OutputIndex = input.OutputIndex, PublicKey = input.PublicKey, Signature = values[index] }).ToList(),
                Outputs = transaction.Outputs.Select(output => new TransactionOutput { Amount = output.Amount,
                    AssetId = output.AssetId, OneTimeAddress = output.OneTimeAddress }).ToList() };
            copy.Id = copy.CalculateId();
            return copy;
        }
    }
}
