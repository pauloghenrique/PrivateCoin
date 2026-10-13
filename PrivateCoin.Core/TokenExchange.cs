using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace PrivateCoin.Core
{
    /// <summary>Atomic, externally signed transactions with consensus-enforced pool reserves.</summary>
    public static class TokenExchange
    {
        public const int PoolFeeBasisPoints = 30;

        public static long Quote(long reserveIn, long reserveOut, long amount)
        {
            if (reserveIn <= 0 || reserveOut <= 0 || amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            BigInteger adjusted = (BigInteger)amount * (10000 - PoolFeeBasisPoints);
            long result = (long)((BigInteger)reserveOut * adjusted / ((BigInteger)reserveIn * 10000 + adjusted));
            if (result <= 0 || result >= reserveOut) throw new InvalidOperationException("Insufficient liquidity.");
            return result;
        }

        public static UnsignedTokenExchange PrepareLiquidity(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, string assetA, string assetB, long amountA, long amountB, string address, long fee, IEnumerable<string> excludedInputs = null)
        {
            var queue = pending.ToArray();
            ValidatePair(chain, queue, assetA, assetB);
            if (amountA <= 0 || amountB <= 0) throw new ArgumentOutOfRangeException(nameof(amountA));
            var keys = Keys(publicKeys, address, fee);
            var selected = new List<UnspentOutput>();
            var outputs = new List<TransactionOutput> { Output(assetA, amountA, address), Output(assetB, amountB, address) };
            Fund(chain, queue, keys, new Dictionary<string, long> {
                { assetA ?? "povix", amountA }, { assetB ?? "povix", amountB } }, address, fee, selected, outputs, new HashSet<string>(excludedInputs ?? new string[0]));
            return Build(selected, keys, outputs, selected.Count, fee, TransactionKind.LiquidityPool);
        }

        public static UnsignedTokenExchange PrepareSwap(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, UnspentOutput reserveA, UnspentOutput reserveB, string providerKey,
            int inputSide, long amount, long minimumReceive, string address, long fee, IEnumerable<string> excludedInputs = null)
        {
            var queue = pending.ToArray();
            if (reserveA == null || reserveB == null || inputSide < 0 || inputSide > 1 || minimumReceive <= 0)
                throw new ArgumentException("Invalid swap.");
            ValidatePair(chain, queue, reserveA.Output.AssetId, reserveB.Output.AssetId);
            var keys = Keys(publicKeys, address, fee);
            string provider = Crypto.Sha256(providerKey);
            if (reserveA.Output.OneTimeAddress != provider || reserveB.Output.OneTimeAddress != provider)
                throw new ArgumentException("Pool reserves must belong to the provider.");
            var reserved = new HashSet<string>(queue.SelectMany(tx => tx.Inputs).Select(Key));
            var confirmed = chain.GetUnspentOutputs(new[] { provider }).ToDictionary(Key);
            foreach (var reserve in new[] { reserveA, reserveB })
            {
                UnspentOutput current;
                if (reserved.Contains(Key(reserve)) || !confirmed.TryGetValue(Key(reserve), out current) ||
                    !current.IsPoolReserve || current.Output.AssetId != reserve.Output.AssetId ||
                    current.Output.Amount != reserve.Output.Amount) throw new InvalidOperationException("Pool reserves are unavailable.");
            }
            var input = inputSide == 0 ? reserveA : reserveB;
            var output = inputSide == 0 ? reserveB : reserveA;
            long receive = Quote(input.Output.Amount, output.Output.Amount, amount);
            if (receive < minimumReceive) throw new InvalidOperationException("Minimum receive is not met.");
            long nextA = inputSide == 0 ? checked(reserveA.Output.Amount + amount) : reserveA.Output.Amount - receive;
            long nextB = inputSide == 1 ? checked(reserveB.Output.Amount + amount) : reserveB.Output.Amount - receive;
            var outputs = new List<TransactionOutput> { Output(reserveA.Output.AssetId, nextA, provider),
                Output(reserveB.Output.AssetId, nextB, provider), Output(output.Output.AssetId, receive, address) };
            var selected = new List<UnspentOutput>();
            Fund(chain, queue, keys, new Dictionary<string, long> { { input.Output.AssetId ?? "povix", amount } },
                address, fee, selected, outputs, new HashSet<string>((excludedInputs ?? new string[0]).Concat(new[] { Key(reserveA), Key(reserveB) })));
            int userCount = selected.Count;
            selected.Add(reserveA); selected.Add(reserveB); keys[provider] = providerKey;
            return Build(selected, keys, outputs, userCount, fee, TransactionKind.LiquiditySwap,
                new LiquiditySwapTerms { InputSide = inputSide, Amount = amount });
        }

        public static UnsignedTokenExchange PrepareReserveOperation(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, UnspentOutput reserveA, UnspentOutput reserveB, string address, long fee,
            bool close, IEnumerable<string> excludedInputs = null)
        {
            var queue = pending.ToArray();
            var keys = Keys(publicKeys, address, fee);
            if (reserveA == null || reserveB == null || reserveA.TransactionId != reserveB.TransactionId ||
                reserveA.OutputIndex != 0 || reserveB.OutputIndex != 1 || reserveA.Output.OneTimeAddress != address ||
                reserveB.Output.OneTimeAddress != address || reserveA.IsPoolReserve != close || reserveB.IsPoolReserve != close)
                throw new InvalidOperationException("Invalid pool reserve operation.");
            var outputs = new List<TransactionOutput> { Output(reserveA.Output.AssetId, reserveA.Output.Amount, address),
                Output(reserveB.Output.AssetId, reserveB.Output.Amount, address) };
            var selected = new List<UnspentOutput>();
            Fund(chain, queue, keys, new Dictionary<string, long>(), address, fee, selected, outputs,
                new HashSet<string>((excludedInputs ?? new string[0]).Concat(new[] { Key(reserveA), Key(reserveB) })));
            int userCount = selected.Count;
            selected.Add(reserveA); selected.Add(reserveB);
            return Build(selected, keys, outputs, userCount, fee, close ? TransactionKind.LiquidityClose : TransactionKind.LiquidityPool);
        }

        internal static bool IsPoolOperation(TransactionKind kind) => kind == TransactionKind.LiquidityPool ||
            kind == TransactionKind.LiquiditySwap || kind == TransactionKind.LiquidityClose;

        internal static void ValidatePoolOperation(Transaction tx, IDictionary<string, UnspentOutput> utxo)
        {
            var sources = tx.Inputs.Select(input => { UnspentOutput source;
                return utxo.TryGetValue(Key(input), out source) ? source : null; }).ToArray();
            var locked = sources.Where(source => source != null && source.IsPoolReserve).ToArray();
            if (!IsPoolOperation(tx.Kind))
            {
                if (locked.Length != 0) throw new InvalidOperationException("Pool reserves cannot fund ordinary movements.");
                return;
            }
            if (tx.Outputs.Count < 2 || tx.Outputs[0].AssetId == tx.Outputs[1].AssetId ||
                tx.Outputs[0].OneTimeAddress != tx.Outputs[1].OneTimeAddress)
                throw new InvalidOperationException("A pool requires two distinct assets at the provider address.");
            if (tx.Kind == TransactionKind.LiquidityPool)
            {
                if (locked.Length != 0 || !tx.Inputs.Any(input => Crypto.Sha256(input.PublicKey) == tx.Outputs[0].OneTimeAddress))
                    throw new InvalidOperationException("Creating a pool requires the provider's authorization and free funds.");
                return;
            }
            if (locked.Length != 2 || tx.Inputs.Count < 3 || sources.Any(source => source == null) ||
                !sources[sources.Length - 2].IsPoolReserve || !sources[sources.Length - 1].IsPoolReserve)
                throw new InvalidOperationException("A pool operation must consume exactly both reserves and pay its fee from free funds.");
            var a = sources[sources.Length - 2]; var b = sources[sources.Length - 1];
            if (a.TransactionId != b.TransactionId || a.OutputIndex != 0 || b.OutputIndex != 1 ||
                a.Output.OneTimeAddress != b.Output.OneTimeAddress || a.Output.AssetId == b.Output.AssetId ||
                tx.Outputs[0].OneTimeAddress != a.Output.OneTimeAddress ||
                tx.Outputs[0].AssetId != a.Output.AssetId || tx.Outputs[1].AssetId != b.Output.AssetId)
                throw new InvalidOperationException("Pool reserves must remain paired with the same assets and provider.");
            long nextA = a.Output.Amount, nextB = b.Output.Amount;
            if (tx.Kind == TransactionKind.LiquiditySwap)
            {
                var terms = tx.LiquiditySwap;
                if (terms == null || terms.InputSide < 0 || terms.InputSide > 1 || terms.Amount <= 0 || tx.Outputs.Count < 3)
                    throw new InvalidOperationException("Missing swap terms.");
                long receive = Quote(terms.InputSide == 0 ? nextA : nextB, terms.InputSide == 0 ? nextB : nextA, terms.Amount);
                nextA = terms.InputSide == 0 ? checked(nextA + terms.Amount) : nextA - receive;
                nextB = terms.InputSide == 1 ? checked(nextB + terms.Amount) : nextB - receive;
                if (tx.Outputs[2].AssetId != (terms.InputSide == 0 ? b.Output.AssetId : a.Output.AssetId) || tx.Outputs[2].Amount != receive)
                    throw new InvalidOperationException("The swap payout must match the pool quote.");
            }
            if (tx.Outputs[0].Amount != nextA || tx.Outputs[1].Amount != nextB)
                throw new InvalidOperationException("Pool reserves must follow the swap quote or return intact to the provider on closure.");
        }

        private static void ValidatePair(Blockchain chain, Transaction[] pending, string a, string b)
        {
            if (chain == null || a == b) throw new ArgumentException("Choose two distinct assets.");
            var tokens = new HashSet<string>(chain.GetTokens(pending).Select(token => token.Id));
            if ((a != null && !tokens.Contains(a)) || (b != null && !tokens.Contains(b))) throw new ArgumentException("Token is not confirmed.");
        }
        private static Dictionary<string, string> Keys(IEnumerable<string> publicKeys, string address, long fee)
        {
            if (fee < Blockchain.TransferFeeStep || fee > Blockchain.MaximumTransferFee) throw new ArgumentOutOfRangeException(nameof(fee));
            var keys = publicKeys.Distinct(StringComparer.Ordinal).ToDictionary(Crypto.Sha256, key => key, StringComparer.Ordinal);
            if (!keys.ContainsKey(address)) throw new ArgumentException("Change must belong to the signing wallet.");
            return keys;
        }
        private static void Fund(Blockchain chain, Transaction[] pending, Dictionary<string, string> keys,
            Dictionary<string, long> amounts, string address, long fee, List<UnspentOutput> selected,
            List<TransactionOutput> outputs, HashSet<string> excluded)
        {
            long native;
            amounts.TryGetValue("povix", out native); amounts["povix"] = checked(native + fee);
            foreach (var requirement in amounts.OrderBy(pair => pair.Key == "povix" ? 0 : 1).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string asset = requirement.Key == "povix" ? null : requirement.Key;
                var available = asset == null ? chain.GetSpendableOutputs(keys.Keys, pending) : chain.GetSpendableTokenOutputs(keys.Keys, pending, asset);
                long total = 0;
                foreach (var item in available.Where(item => !excluded.Contains(Key(item))))
                { selected.Add(item); total = checked(total + item.Output.Amount); if (total >= requirement.Value) break; }
                if (total < requirement.Value) throw new InvalidOperationException(asset == null ? "Insufficient POVIX for the transaction fee." : "Insufficient token balance.");
                if (total > requirement.Value) outputs.Add(Output(asset, total - requirement.Value, address));
            }
        }
        private static UnsignedTokenExchange Build(List<UnspentOutput> selected, Dictionary<string, string> keys,
            List<TransactionOutput> outputs, int userCount, long fee, TransactionKind kind, LiquiditySwapTerms terms = null)
        {
            var tx = new Transaction { Kind = kind, LiquiditySwap = terms, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee, Outputs = outputs,
                Inputs = selected.Select(item => new TransactionInput { TransactionId = item.TransactionId,
                    OutputIndex = item.OutputIndex, PublicKey = keys[item.Output.OneTimeAddress] }).ToList() };
            return new UnsignedTokenExchange(tx, selected.ToArray(), userCount);
        }
        private static TransactionOutput Output(string asset, long amount, string address) => new TransactionOutput { AssetId = asset, Amount = amount, OneTimeAddress = address };
        private static string Key(UnspentOutput item) => item.TransactionId + ":" + item.OutputIndex;
        private static string Key(TransactionInput item) => item.TransactionId + ":" + item.OutputIndex;
    }

    public sealed class UnsignedTokenExchange
    {
        private readonly Transaction transaction;
        public int UserInputCount { get; }
        public IReadOnlyList<UnspentOutput> Sources { get; }
        public byte[] SigningPayload => Encoding.UTF8.GetBytes(transaction.SigningPayload());
        public long Fee => transaction.Fee;
        public long TimestampUtcTicks => transaction.TimestampUtcTicks;
        public TransactionKind Kind => transaction.Kind;
        public IReadOnlyList<TransactionOutput> Outputs => transaction.Outputs.Select(item => new TransactionOutput {
            AssetId = item.AssetId, Amount = item.Amount, OneTimeAddress = item.OneTimeAddress }).ToArray();
        public IReadOnlyList<TransactionInput> Inputs => transaction.Inputs.Select(item => new TransactionInput {
            TransactionId = item.TransactionId, OutputIndex = item.OutputIndex, PublicKey = item.PublicKey }).ToArray();
        internal UnsignedTokenExchange(Transaction tx, UnspentOutput[] sources, int userCount)
        { transaction = tx; Sources = Array.AsReadOnly(sources); UserInputCount = userCount; }
        public Transaction Complete(IEnumerable<string> signatures)
        {
            var values = signatures.ToArray();
            if (transaction.Kind == TransactionKind.LiquiditySwap && values.Length == UserInputCount)
            {
                if (values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("One signature is required for every funding input.");
                values = values.Concat(new[] { string.Empty, string.Empty }).ToArray();
            }
            else if (values.Length != transaction.Inputs.Count || values.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("One signature is required for every input.");
            var tx = new Transaction { Kind = transaction.Kind, Fee = transaction.Fee, TimestampUtcTicks = transaction.TimestampUtcTicks,
                LiquiditySwap = transaction.LiquiditySwap == null ? null : new LiquiditySwapTerms {
                    InputSide = transaction.LiquiditySwap.InputSide, Amount = transaction.LiquiditySwap.Amount },
                Outputs = Outputs.ToList(), Inputs = transaction.Inputs.Select((input, index) => new TransactionInput {
                    TransactionId = input.TransactionId, OutputIndex = input.OutputIndex, PublicKey = input.PublicKey, Signature = values[index] }).ToList() };
            tx.Id = tx.CalculateId(); return tx;
        }
    }
}
