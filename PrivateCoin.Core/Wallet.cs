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

        public ValidatorStake CreateValidatorStake(string rewardAddress, long lockedAmount)
        {
            RSACryptoServiceProvider key;
            if (!keys.TryGetValue(rewardAddress, out key)) throw new InvalidOperationException("The reward address is not owned by this wallet.");
            string publicKey = key.ToXmlString(false);
            string validatorId = Crypto.Sha256(publicKey);
            return new ValidatorStake(validatorId, rewardAddress, lockedAmount, new[] { rewardAddress }, publicKey, payload =>
                Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), CryptoConfig.MapNameToOID("SHA256"))));
        }

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
            return FromPrivateKeys(privateKeys, null);
        }

        /// <summary>
        /// Imports saved keys without regenerating them. The optional seed preserves
        /// deterministic generation of subsequent addresses at the next unused index.
        /// </summary>
        public static Wallet FromPrivateKeys(IEnumerable<string> privateKeys, byte[] seed)
        {
            if (privateKeys == null) throw new ArgumentNullException(nameof(privateKeys));
            if (seed != null && seed.Length < 16)
                throw new ArgumentException("A deterministic seed must contain at least 128 bits.", nameof(seed));
            var wallet = seed == null ? new Wallet() : new Wallet(seed);
            try
            {
                foreach (string privateKey in privateKeys)
                {
                    if (string.IsNullOrWhiteSpace(privateKey)) throw new ArgumentException("A private key is invalid.", nameof(privateKeys));
                    var key = new RSACryptoServiceProvider(2048);
                    key.PersistKeyInCsp = false;
                    try
                    {
                        key.FromXmlString(privateKey);
                        string address = Crypto.Sha256(key.ToXmlString(false));
                        if (wallet.keys.ContainsKey(address))
                            throw new ArgumentException("A private key is duplicated.", nameof(privateKeys));
                        wallet.keys.Add(address, key);
                        wallet.nextKeyIndex++;
                    }
                    catch
                    {
                        key.Dispose();
                        throw;
                    }
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

        /// <summary>Creates a transaction and reserves the fee for its transaction validator.</summary>
        public Transaction CreateTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions, string destinationOneTimeAddress, long amount, long fee)
        {
            return CreateSignedTransaction(chain, pendingTransactions, destinationOneTimeAddress, amount, fee, TransactionKind.Transfer, null, null);
        }

        public Transaction CreateStakeLockTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions,
            string rewardAddress, long amount, long fee)
        {
            RSACryptoServiceProvider validatorKey;
            if (!keys.TryGetValue(rewardAddress, out validatorKey)) throw new InvalidOperationException("The reward address is not owned by this wallet.");
            string publicKey = validatorKey.ToXmlString(false);
            return CreateSignedTransaction(chain, pendingTransactions, Crypto.Sha256(publicKey), amount, fee,
                TransactionKind.StakeLock, publicKey, rewardAddress, keys.Keys.OrderBy(item => item, StringComparer.Ordinal).ToList());
        }

        public Transaction CreateStakeUnlockTransaction(Blockchain chain, string rewardAddress, long fee)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            RSACryptoServiceProvider key;
            if (!keys.TryGetValue(rewardAddress, out key)) throw new InvalidOperationException("The validator address is not owned by this wallet.");
            UnspentOutput collateral = chain.GetUnspentOutputs(new[] { rewardAddress })
                .SingleOrDefault(item => item.TransactionKind == TransactionKind.StakeLock);
            if (collateral == null) throw new InvalidOperationException("No globally locked collateral exists for this validator.");
            if (fee < Blockchain.TransferFeeStep || fee >= collateral.Output.Amount)
                throw new ArgumentOutOfRangeException(nameof(fee));

            var transaction = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee, Kind = TransactionKind.StakeUnlock };
            transaction.Inputs.Add(new TransactionInput { TransactionId = collateral.TransactionId, OutputIndex = collateral.OutputIndex });
            transaction.Outputs.Add(new TransactionOutput { Amount = collateral.Output.Amount - fee, OneTimeAddress = CreateReceiveAddress() });
            byte[] payload = Encoding.UTF8.GetBytes(transaction.SigningPayload());
            transaction.Inputs[0].PublicKey = key.ToXmlString(false);
            transaction.Inputs[0].Signature = Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
            transaction.Id = transaction.CalculateId();
            return transaction;
        }

        private Transaction CreateSignedTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions,
            string destinationOneTimeAddress, long amount, long fee, TransactionKind kind, string validatorPublicKey, string validatorRewardAddress)
        {
            return CreateSignedTransaction(chain, pendingTransactions, destinationOneTimeAddress, amount, fee, kind,
                validatorPublicKey, validatorRewardAddress, null);
        }

        private Transaction CreateSignedTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions,
            string destinationOneTimeAddress, long amount, long fee, TransactionKind kind, string validatorPublicKey,
            string validatorRewardAddress, List<string> validatorOwnedAddresses)
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

            var transaction = new Transaction { TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee, Kind = kind,
                ValidatorPublicKey = validatorPublicKey, ValidatorRewardAddress = validatorRewardAddress,
                ValidatorOwnedAddresses = validatorOwnedAddresses };
            foreach (var item in selected)
                transaction.Inputs.Add(new TransactionInput { TransactionId = item.TransactionId, OutputIndex = item.OutputIndex });
            transaction.Outputs.Add(new TransactionOutput { Amount = amount, OneTimeAddress = destinationOneTimeAddress });
            if (total > required)
                transaction.Outputs.Add(new TransactionOutput { Amount = total - required, OneTimeAddress = CreateReceiveAddress() });
            if (kind == TransactionKind.StakeLock)
                transaction.ValidatorOwnedAddresses = selected.Select(item => item.Output.OneTimeAddress)
                    .Concat(new[] { validatorRewardAddress }).Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal).ToList();

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

        /// <summary>Creates a fixed-supply token; supply is expressed in atomic token units.</summary>
        public Transaction CreateTokenTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions,
            string name, string symbol, int decimals, long supply, string destinationOneTimeAddress, long fee)
        {
            var token = new TokenDefinition { Name = name, Symbol = symbol, Decimals = decimals, Supply = supply };
            token.Validate();
            var selected = SelectTokenFunding(chain, pendingTransactions, fee);
            var tx = new Transaction { Kind = TransactionKind.TokenCreate, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee, Token = token };
            AddTokenInputs(tx, selected);
            token.Id = TokenDefinition.IdFor(tx.Inputs[0]);
            tx.Outputs.Add(new TransactionOutput { Amount = supply, OneTimeAddress = RequireTokenDestination(destinationOneTimeAddress), AssetId = token.Id });
            AddTokenFeeChange(tx, selected, fee);
            SignTokenTransaction(tx, selected);
            return tx;
        }

        public Transaction CreateTokenTransferTransaction(Blockchain chain, IEnumerable<Transaction> pendingTransactions,
            string tokenId, string destinationOneTimeAddress, long amount, long fee)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (pendingTransactions == null) throw new ArgumentNullException(nameof(pendingTransactions));
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            string destination = RequireTokenDestination(destinationOneTimeAddress);
            var pending = pendingTransactions.ToList();
            var funding = SelectTokenFunding(chain, pending, fee);
            var tokens = new List<UnspentOutput>();
            long total = 0;
            foreach (var item in chain.GetSpendableTokenOutputs(keys.Keys, pending, tokenId))
            {
                tokens.Add(item); total = checked(total + item.Output.Amount);
                if (total >= amount) break;
            }
            if (total < amount) throw new InvalidOperationException("Insufficient token balance.");
            var tx = new Transaction { Kind = TransactionKind.TokenTransfer, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee };
            var selected = funding.Concat(tokens).ToList();
            AddTokenInputs(tx, selected);
            tx.Outputs.Add(new TransactionOutput { Amount = amount, OneTimeAddress = destination, AssetId = tokenId });
            if (total > amount) tx.Outputs.Add(new TransactionOutput { Amount = total - amount, OneTimeAddress = CreateReceiveAddress(), AssetId = tokenId });
            AddTokenFeeChange(tx, funding, fee);
            SignTokenTransaction(tx, selected);
            return tx;
        }

        private List<UnspentOutput> SelectTokenFunding(Blockchain chain, IEnumerable<Transaction> pending, long fee)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (fee < Blockchain.TransferFeeStep) throw new ArgumentOutOfRangeException(nameof(fee));
            var selected = new List<UnspentOutput>();
            long total = 0;
            foreach (var item in chain.GetSpendableOutputs(keys.Keys, pending))
            {
                selected.Add(item); total = checked(total + item.Output.Amount);
                if (total >= fee) break;
            }
            if (total < fee) throw new InvalidOperationException("Insufficient POVIX for the transaction fee.");
            return selected;
        }

        private static string RequireTokenDestination(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Destination is required.", nameof(destination));
            return destination;
        }

        private static void AddTokenInputs(Transaction tx, IEnumerable<UnspentOutput> selected)
        {
            foreach (var item in selected) tx.Inputs.Add(new TransactionInput { TransactionId = item.TransactionId, OutputIndex = item.OutputIndex });
        }

        private void AddTokenFeeChange(Transaction tx, IEnumerable<UnspentOutput> funding, long fee)
        {
            long total = funding.Aggregate(0L, (sum, item) => checked(sum + item.Output.Amount));
            if (total > fee) tx.Outputs.Add(new TransactionOutput { Amount = total - fee, OneTimeAddress = CreateReceiveAddress() });
        }

        private void SignTokenTransaction(Transaction tx, IList<UnspentOutput> selected)
        {
            byte[] payload = Encoding.UTF8.GetBytes(tx.SigningPayload());
            for (int i = 0; i < selected.Count; i++)
            {
                var key = keys[selected[i].Output.OneTimeAddress];
                tx.Inputs[i].PublicKey = key.ToXmlString(false);
                tx.Inputs[i].Signature = Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
            }
            tx.Id = tx.CalculateId();
        }

        public void Dispose()
        {
            foreach (var key in keys.Values) key.Dispose();
            keys.Clear();
        }
    }
}
