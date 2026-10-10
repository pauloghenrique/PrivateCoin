using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>Prepares a native transfer for external signing, using existing wallet addresses for both kinds of change.</summary>
    public static class TokenTransfer
    {
        public static UnsignedTokenTransfer Prepare(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, string tokenId, string destinationAddress, long amount, string changeAddress, long fee)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (publicKeys == null) throw new ArgumentNullException(nameof(publicKeys));
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (fee < Blockchain.TransferFeeStep || fee > Blockchain.MaximumTransferFee) throw new ArgumentOutOfRangeException(nameof(fee));
            if (!IsAddress(tokenId) || !IsAddress(destinationAddress) || !IsAddress(changeAddress)) throw new ArgumentException("Invalid transfer address or token identifier.");
            var queue = pending.ToArray();
            if (!chain.GetTokens(queue).Any(token => token.Id == tokenId)) throw new ArgumentException("Token is not confirmed.");
            var keys = publicKeys.Distinct(StringComparer.Ordinal).ToDictionary(Crypto.Sha256, key => key, StringComparer.Ordinal);
            if (!keys.ContainsKey(changeAddress)) throw new ArgumentException("Change must belong to the signing wallet.");
            var funding = Select(chain.GetSpendableOutputs(keys.Keys, queue), fee, "Insufficient POVIX for the transaction fee.");
            var tokens = Select(chain.GetSpendableTokenOutputs(keys.Keys, queue, tokenId), amount, "Insufficient token balance.");
            var selected = funding.Concat(tokens).ToArray();
            var tx = new Transaction { Kind = TransactionKind.TokenTransfer, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee };
            tx.Inputs.AddRange(selected.Select(output => new TransactionInput { TransactionId = output.TransactionId,
                OutputIndex = output.OutputIndex, PublicKey = keys[output.Output.OneTimeAddress] }));
            tx.Outputs.Add(new TransactionOutput { Amount = amount, OneTimeAddress = destinationAddress, AssetId = tokenId });
            long tokenChange = checked(tokens.Sum(output => output.Output.Amount) - amount);
            long povixChange = checked(funding.Sum(output => output.Output.Amount) - fee);
            if (tokenChange > 0) tx.Outputs.Add(new TransactionOutput { Amount = tokenChange, OneTimeAddress = changeAddress, AssetId = tokenId });
            if (povixChange > 0) tx.Outputs.Add(new TransactionOutput { Amount = povixChange, OneTimeAddress = changeAddress });
            return new UnsignedTokenTransfer(tx, selected.Select(output => output.Output.OneTimeAddress).ToArray(), tokenId, changeAddress, tokenChange, povixChange);
        }

        private static List<UnspentOutput> Select(IEnumerable<UnspentOutput> outputs, long amount, string error)
        {
            long total = 0;
            var selected = new List<UnspentOutput>();
            foreach (var output in outputs) { selected.Add(output); total = checked(total + output.Output.Amount); if (total >= amount) break; }
            if (total < amount) throw new InvalidOperationException(error);
            return selected;
        }

        private static bool IsAddress(string value) => value != null && value.Length == 64 && value.All(c =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    public sealed class UnsignedTokenTransfer
    {
        private readonly Transaction transaction;
        public IReadOnlyList<string> InputAddresses { get; }
        public byte[] SigningPayload => Encoding.UTF8.GetBytes(transaction.SigningPayload());
        public string TokenId { get; }
        public string DestinationAddress => transaction.Outputs[0].OneTimeAddress;
        public long Amount => transaction.Outputs[0].Amount;
        public long Fee => transaction.Fee;
        public string ChangeAddress { get; }
        public long TokenChangeAmount { get; }
        public long PovixChangeAmount { get; }
        public long TimestampUtcTicks => transaction.TimestampUtcTicks;
        public IReadOnlyList<TransactionInput> Inputs => transaction.Inputs.Select(input => new TransactionInput {
            TransactionId = input.TransactionId, OutputIndex = input.OutputIndex, PublicKey = input.PublicKey }).ToArray();

        internal UnsignedTokenTransfer(Transaction tx, string[] addresses, string tokenId, string changeAddress, long tokenChange, long povixChange)
        {
            transaction = tx; InputAddresses = Array.AsReadOnly(addresses); TokenId = tokenId;
            ChangeAddress = changeAddress; TokenChangeAmount = tokenChange; PovixChangeAmount = povixChange;
        }

        public Transaction Complete(IEnumerable<string> signatures)
        {
            if (signatures == null) throw new ArgumentNullException(nameof(signatures));
            string[] values = signatures.ToArray();
            if (values.Length != transaction.Inputs.Count || values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("One signature is required for every input.");
            var copy = new Transaction { Kind = transaction.Kind, TimestampUtcTicks = transaction.TimestampUtcTicks, Fee = transaction.Fee,
                Inputs = transaction.Inputs.Select((input, index) => new TransactionInput { TransactionId = input.TransactionId,
                    OutputIndex = input.OutputIndex, PublicKey = input.PublicKey, Signature = values[index] }).ToList(),
                Outputs = transaction.Outputs.Select(output => new TransactionOutput { Amount = output.Amount,
                    AssetId = output.AssetId, OneTimeAddress = output.OneTimeAddress }).ToList() };
            copy.Id = copy.CalculateId();
            return copy;
        }
    }
}
