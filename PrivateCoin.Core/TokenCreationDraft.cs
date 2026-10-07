using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace PrivateCoin.Core
{
    /// <summary>Prepares token creation using public keys only, for an external signer.</summary>
    public sealed class TokenCreationDraft
    {
        private readonly Transaction transaction;

        private TokenCreationDraft(Transaction transaction) { this.transaction = transaction; }

        public string SigningPayload => transaction.SigningPayload();
        public string TokenId => transaction.Token.Id;
        public long Fee => transaction.Fee;
        public string[] InputAddresses => transaction.Inputs.Select(input => Crypto.Sha256(input.PublicKey)).ToArray();

        public static TokenCreationDraft Prepare(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, string name, string symbol, int decimals, long supply,
            string recipient, string changeAddress, long fee)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (publicKeys == null) throw new ArgumentNullException(nameof(publicKeys));
            if (!IsAddress(recipient) || !IsAddress(changeAddress) || recipient == changeAddress)
                throw new ArgumentException("Use separate, valid one-time addresses for tokens and change.");
            if (fee < Blockchain.TransferFeeStep || fee > Blockchain.OneCoin)
                throw new ArgumentOutOfRangeException(nameof(fee));
            var token = new TokenDefinition { Name = name, Symbol = symbol, Decimals = decimals, Supply = supply };
            token.Validate();
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string publicKey in publicKeys)
            {
                if (string.IsNullOrEmpty(publicKey) || publicKey.Length > 1024 || publicKey.Contains("<D>"))
                    throw new ArgumentException("Only RSA public keys are accepted.");
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKey);
                    if (!rsa.PublicOnly || rsa.KeySize != 2048 || rsa.ToXmlString(false) != publicKey)
                        throw new ArgumentException("A canonical 2048-bit RSA public key is required.");
                }
                keys[Crypto.Sha256(publicKey)] = publicKey;
                if (keys.Count > 100) throw new ArgumentException("At most 100 funding keys are accepted.");
            }
            if (keys.ContainsKey(recipient) || keys.ContainsKey(changeAddress))
                throw new ArgumentException("Token and change addresses must be new.");
            var tx = new Transaction { Kind = TransactionKind.TokenCreate, Token = token,
                TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee };
            long total = 0;
            foreach (var output in chain.GetSpendableOutputs(keys.Keys, pending))
            {
                tx.Inputs.Add(new TransactionInput { TransactionId = output.TransactionId,
                    OutputIndex = output.OutputIndex, PublicKey = keys[output.Output.OneTimeAddress] });
                total = checked(total + output.Output.Amount);
                if (total >= fee) break;
            }
            if (total < fee) throw new InvalidOperationException("Saldo POVIX confirmado insuficiente para a taxa.");
            token.Id = TokenDefinition.IdFor(tx.Inputs[0]);
            tx.Outputs.Add(new TransactionOutput { Amount = supply, AssetId = token.Id, OneTimeAddress = recipient });
            if (total > fee) tx.Outputs.Add(new TransactionOutput { Amount = total - fee, OneTimeAddress = changeAddress });
            return new TokenCreationDraft(tx);
        }

        public Transaction Complete(string[] signatures)
        {
            if (signatures == null || signatures.Length != transaction.Inputs.Count)
                throw new ArgumentException("One signature per input is required.");
            var tx = new Transaction { TimestampUtcTicks = transaction.TimestampUtcTicks,
                Fee = transaction.Fee, Kind = transaction.Kind,
                Token = new TokenDefinition { Id = transaction.Token.Id, Name = transaction.Token.Name,
                    Symbol = transaction.Token.Symbol, Decimals = transaction.Token.Decimals, Supply = transaction.Token.Supply } };
            for (int i = 0; i < signatures.Length; i++)
            {
                if (string.IsNullOrEmpty(signatures[i]) || signatures[i].Length > 512 ||
                    Convert.FromBase64String(signatures[i]).Length != 256)
                    throw new ArgumentException("Invalid RSA signature.");
                var input = transaction.Inputs[i];
                tx.Inputs.Add(new TransactionInput { TransactionId = input.TransactionId,
                    OutputIndex = input.OutputIndex, PublicKey = input.PublicKey, Signature = signatures[i] });
            }
            tx.Outputs.AddRange(transaction.Outputs.Select(output => new TransactionOutput {
                Amount = output.Amount, AssetId = output.AssetId, OneTimeAddress = output.OneTimeAddress }));
            tx.Id = tx.CalculateId();
            return tx;
        }

        private static bool IsAddress(string value) => value != null && value.Length == 64 &&
            value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }
}
