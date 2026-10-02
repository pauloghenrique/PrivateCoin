using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>A wallet with a separate RSA key for every received output.</summary>
    public sealed class Wallet : IDisposable
    {
        private readonly Dictionary<string, RSACryptoServiceProvider> keys =
            new Dictionary<string, RSACryptoServiceProvider>(StringComparer.Ordinal);
        private readonly byte[] deterministicSeed;
        private int nextKeyIndex;

        public Wallet() { }

        private Wallet(byte[] seed)
        {
            deterministicSeed = (byte[])seed.Clone();
        }

        public static Wallet FromSeed(byte[] seed, int addressCount)
        {
            if (addressCount < 0) throw new ArgumentOutOfRangeException(nameof(addressCount));
            var wallet = new Wallet(seed);
            try
            {
                for (int index = 0; index < addressCount; index++) wallet.CreateReceiveAddress();
                return wallet;
            }
            catch { wallet.Dispose(); throw; }
        }

        public string CreateReceiveAddress()
        {
            var key = deterministicSeed == null
                ? new RSACryptoServiceProvider(2048)
                : DeterministicRsa.Create(deterministicSeed, nextKeyIndex);
            key.PersistKeyInCsp = false;
            string publicKey = key.ToXmlString(false);
            string address = Crypto.Sha256(publicKey);
            keys.Add(address, key);
            nextKeyIndex++;
            return address;
        }

        public IReadOnlyCollection<string> OwnedOneTimeAddresses => keys.Keys.ToArray();

        /// <summary>Indicates whether this wallet sends or receives value in a transaction.</summary>
        public bool IsParticipant(Transaction transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            return transaction.Inputs.Any(input => !string.IsNullOrWhiteSpace(input.PublicKey) &&
                    keys.ContainsKey(Crypto.Sha256(input.PublicKey))) ||
                transaction.Outputs.Any(output => keys.ContainsKey(output.OneTimeAddress));
        }

        /// <summary>Restores a wallet from RSA private keys previously exported by this class.</summary>
        public static Wallet FromPrivateKeys(IEnumerable<string> privateKeys)
        {
            if (privateKeys == null) throw new ArgumentNullException(nameof(privateKeys));
            var wallet = new Wallet();
            try
            {
                foreach (string privateKey in privateKeys)
                {
                    if (string.IsNullOrWhiteSpace(privateKey)) throw new ArgumentException("A private key is invalid.", nameof(privateKeys));
                    var key = new RSACryptoServiceProvider(2048);
                    key.PersistKeyInCsp = false;
                    key.FromXmlString(privateKey);
                    string address = Crypto.Sha256(key.ToXmlString(false));
                    if (wallet.keys.ContainsKey(address))
                    {
                        key.Dispose();
                        throw new ArgumentException("A private key is duplicated.", nameof(privateKeys));
                    }
                    wallet.keys.Add(address, key);
                }
                return wallet;
            }
            catch
            {
                wallet.Dispose();
                throw;
            }
        }

        /// <summary>Exports the private key material needed to restore every one-time address.</summary>
        public IReadOnlyCollection<string> ExportPrivateKeys()
        {
            return keys.Values.Select(key => key.ToXmlString(true)).ToArray();
        }

        public Transaction CreateTransaction(Blockchain chain, string destinationOneTimeAddress, long amount)
        {
            return CreateTransaction(chain, Enumerable.Empty<Transaction>(), destinationOneTimeAddress, amount,
                Blockchain.CalculateAutomaticFee(0, 1));
        }

        /// <summary>
        /// Creates a transaction from confirmed outputs that have not already been
        /// reserved by an ordered set of pending transactions.
        /// </summary>
        public Transaction CreateTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions, string destinationOneTimeAddress, long amount)
        {
            int pendingCount = pendingTransactions == null ? 0 : pendingTransactions.Count();
            return CreateTransaction(chain, pendingTransactions, destinationOneTimeAddress, amount,
                Blockchain.CalculateAutomaticFee(pendingCount, 1));
        }

        /// <summary>Creates a transaction and reserves the fee for its block creator.</summary>
        public Transaction CreateTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions, string destinationOneTimeAddress, long amount, long fee)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            if (string.IsNullOrWhiteSpace(destinationOneTimeAddress)) throw new ArgumentException("Destination is required.", nameof(destinationOneTimeAddress));
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (fee < Blockchain.TransferFeeStep) throw new ArgumentOutOfRangeException(nameof(fee), "The minimum transaction fee is one atomic unit.");

            var selected = new List<UnspentOutput>();
            long total = 0;
            long required = checked(amount + fee);
            foreach (var item in chain.GetSpendableOutputs(keys.Keys, pendingTransactions))
            {
                selected.Add(item);
                total = checked(total + item.Output.Amount);
                if (total >= required) break;
            }
            if (total < required) throw new InvalidOperationException("Insufficient funds.");

            var transaction = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee };
            foreach (var item in selected)
                transaction.Inputs.Add(new TransactionInput { TransactionId = item.TransactionId, OutputIndex = item.OutputIndex });
            transaction.Outputs.Add(new TransactionOutput { Amount = amount, OneTimeAddress = destinationOneTimeAddress });
            if (total > required)
                transaction.Outputs.Add(new TransactionOutput { Amount = total - required, OneTimeAddress = CreateReceiveAddress() });

            byte[] payload = Encoding.UTF8.GetBytes(transaction.SigningPayload());
            for (int index = 0; index < selected.Count; index++)
            {
                var key = keys[selected[index].Output.OneTimeAddress];
                transaction.Inputs[index].PublicKey = key.ToXmlString(false);
                transaction.Inputs[index].Signature = Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
            }
            transaction.Id = transaction.CalculateId();
            return transaction;
        }

        public void Dispose()
        {
            foreach (var key in keys.Values) key.Dispose();
            keys.Clear();
        }
    }
}
