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

        public sealed class SwapQuote
        {
            public long GrossReceive { get; internal set; }
            public long Receive { get; internal set; }
            public long InputBurn { get; internal set; }
            public long OutputBurn { get; internal set; }
            public long InputDebit { get; internal set; }
        }
        public static SwapQuote QuoteWithBurn(long reserveIn, long reserveOut, long amount, TokenPolicy inputPolicy, TokenPolicy outputPolicy)
        {
            long gross = Quote(reserveIn, reserveOut, amount);
            long inputBurn = inputPolicy?.CalculateTransferBurn(amount) ?? 0;
            long outputBurn = outputPolicy?.CalculateTransferBurn(gross) ?? 0;
            if (outputBurn >= gross) throw new InvalidOperationException("Automatic burn consumes the entire swap payout.");
            return new SwapQuote { GrossReceive = gross, Receive = gross - outputBurn, InputBurn = inputBurn, OutputBurn = outputBurn, InputDebit = checked(amount + inputBurn) };
        }

        public static long Quote(long reserveIn, long reserveOut, long amount)
        {
            if (reserveIn <= 0 || reserveOut <= 0 || amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            BigInteger adjusted = (BigInteger)amount * (10000 - PoolFeeBasisPoints);
            long result = (long)((BigInteger)reserveOut * adjusted / ((BigInteger)reserveIn * 10000 + adjusted));
            if (result <= 0 || result >= reserveOut) throw new InvalidOperationException("Insufficient liquidity.");
            return result;
        }

        public static UnsignedTokenExchange PrepareLiquidity(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, string assetA, string assetB, long amountA, long amountB, string address, long fee, IEnumerable<string> excludedInputs = null, bool allowUserLiquidity = false, PoolBurnPolicy burnPolicy = null)
        {
            var queue = pending.ToArray();
            ValidatePair(chain, queue, assetA, assetB);
            if (amountA <= 0 || amountB <= 0) throw new ArgumentOutOfRangeException(nameof(amountA));
            var keys = Keys(publicKeys, address, fee);
            var selected = new List<UnspentOutput>();
            var outputs = new List<TransactionOutput> { Output(assetA, amountA, address), Output(assetB, amountB, address) };
            Fund(chain, queue, keys, new Dictionary<string, long> {
                { assetA ?? "povix", amountA }, { assetB ?? "povix", amountB } }, address, fee, selected, outputs, new HashSet<string>(excludedInputs ?? new string[0]));
            return Build(selected, keys, outputs, selected.Count, fee, TransactionKind.LiquidityPool, participation: allowUserLiquidity ?
                new PoolParticipation { Shares = new Dictionary<string, long> { { address, amountA } } } : null, burnPolicy: burnPolicy);
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
                    current.Output.Amount != reserve.Output.Amount || current.PoolBurnPolicy?.Canonical() != reserve.PoolBurnPolicy?.Canonical()) throw new InvalidOperationException("Pool reserves are unavailable.");
            }
            var input = inputSide == 0 ? reserveA : reserveB;
            var output = inputSide == 0 ? reserveB : reserveA;
            var quote = QuoteWithBurn(input.Output.Amount, output.Output.Amount, amount,
                input.Output.AssetId != null ? reserveA.PoolBurnPolicy?.ForAsset(inputSide) : null, output.Output.AssetId != null ? reserveA.PoolBurnPolicy?.ForAsset(1 - inputSide) : null);
            long receive = quote.Receive;
            if (receive < minimumReceive) throw new InvalidOperationException("Minimum receive is not met.");
            long nextA = inputSide == 0 ? checked(reserveA.Output.Amount + amount) : reserveA.Output.Amount - quote.GrossReceive;
            long nextB = inputSide == 1 ? checked(reserveB.Output.Amount + amount) : reserveB.Output.Amount - quote.GrossReceive;
            var outputs = new List<TransactionOutput> { Output(reserveA.Output.AssetId, nextA, provider),
                Output(reserveB.Output.AssetId, nextB, provider), Output(output.Output.AssetId, receive, address) };
            var selected = new List<UnspentOutput>();
            Fund(chain, queue, keys, new Dictionary<string, long> { { input.Output.AssetId ?? "povix", quote.InputDebit } },
                address, fee, selected, outputs, new HashSet<string>((excludedInputs ?? new string[0]).Concat(new[] { Key(reserveA), Key(reserveB) })));
            int userCount = selected.Count;
            selected.Add(reserveA); selected.Add(reserveB); keys[provider] = providerKey;
            return Build(selected, keys, outputs, userCount, fee, TransactionKind.LiquiditySwap,
                new LiquiditySwapTerms { InputSide = inputSide, Amount = amount, InputBurn = quote.InputBurn, OutputBurn = quote.OutputBurn }, reserveA.PoolParticipation?.Copy(), burnPolicy: reserveA.PoolBurnPolicy);
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
            return Build(selected, keys, outputs, userCount, fee, close ? TransactionKind.LiquidityClose : TransactionKind.LiquidityPool, burnPolicy: reserveA.PoolBurnPolicy);
        }

        internal static bool IsPoolOperation(TransactionKind kind) => kind == TransactionKind.LiquidityPool ||
            kind == TransactionKind.LiquiditySwap || kind == TransactionKind.LiquidityClose ||
            kind == TransactionKind.LiquidityAdd || kind == TransactionKind.LiquidityRemove;

        internal static void ValidatePoolOperation(Transaction tx, IDictionary<string, UnspentOutput> utxo)
        {
            var sources = tx.Inputs.Select(input => { UnspentOutput source;
                return utxo.TryGetValue(Key(input), out source) ? source : null; }).ToArray();
            var locked = sources.Where(source => source != null && source.IsPoolReserve).ToArray();
            if (tx.PoolParticipation != null && (!IsPoolOperation(tx.Kind) || tx.Kind == TransactionKind.LiquidityClose))
                throw new InvalidOperationException("Invalid pool participation metadata.");
            bool change = tx.Kind == TransactionKind.LiquidityAdd || tx.Kind == TransactionKind.LiquidityRemove;
            if ((tx.LiquidityChange != null) != change) throw new InvalidOperationException("Invalid liquidity change terms.");
            ValidateParticipation(tx.PoolParticipation);
            if (!IsPoolOperation(tx.Kind))
            {
                if (locked.Length != 0) throw new InvalidOperationException("Pool reserves cannot fund ordinary movements.");
                return;
            }
            if (tx.Outputs.Count < 2 || tx.Outputs[0].AssetId == tx.Outputs[1].AssetId ||
                tx.Outputs[0].OneTimeAddress != tx.Outputs[1].OneTimeAddress)
                throw new InvalidOperationException("A pool requires two distinct assets at the provider address.");
            tx.PoolBurnPolicy?.Validate(tx.Outputs[0].AssetId, tx.Outputs[1].AssetId);
            if (tx.Kind == TransactionKind.LiquidityPool)
            {
                if (locked.Length != 0 || !tx.Inputs.Any(input => Crypto.Sha256(input.PublicKey) == tx.Outputs[0].OneTimeAddress))
                    throw new InvalidOperationException("Creating a pool requires the provider's authorization and free funds.");
                if (tx.PoolParticipation != null && (tx.PoolParticipation.Shares.Count != 1 ||
                    !tx.PoolParticipation.Shares.ContainsKey(tx.Outputs[0].OneTimeAddress) || tx.PoolParticipation.Total != tx.Outputs[0].Amount))
                    throw new InvalidOperationException("Initial shares must belong to the provider.");
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
            if (a.PoolBurnPolicy?.Canonical() != b.PoolBurnPolicy?.Canonical() || tx.PoolBurnPolicy?.Canonical() != a.PoolBurnPolicy?.Canonical())
                throw new InvalidOperationException("Pool burn choice is permanent and must follow both reserves.");
            if (tx.Kind == TransactionKind.LiquidityClose && a.PoolParticipation != null &&
                (a.PoolParticipation.Shares.Count != 1 || !a.PoolParticipation.Shares.ContainsKey(a.Output.OneTimeAddress)))
                throw new InvalidOperationException("Other providers must withdraw their shares before pool closure.");
            if (tx.Kind == TransactionKind.LiquiditySwap && (tx.PoolParticipation?.Canonical() != a.PoolParticipation?.Canonical()))
                throw new InvalidOperationException("Swaps must preserve liquidity shares.");
            if (change) { ValidateLiquidityChange(tx, a, b); return; }
            long nextA = a.Output.Amount, nextB = b.Output.Amount;
            if (tx.Kind == TransactionKind.LiquiditySwap)
            {
                var terms = tx.LiquiditySwap;
                if (terms == null || terms.InputSide < 0 || terms.InputSide > 1 || terms.Amount <= 0 || tx.Outputs.Count < 3)
                    throw new InvalidOperationException("Missing swap terms.");
                var quote = QuoteWithBurn(terms.InputSide == 0 ? nextA : nextB, terms.InputSide == 0 ? nextB : nextA, terms.Amount,
                    (terms.InputSide == 0 ? a.Output.AssetId : b.Output.AssetId) != null ? a.PoolBurnPolicy?.ForAsset(terms.InputSide) : null,
                    (terms.InputSide == 0 ? b.Output.AssetId : a.Output.AssetId) != null ? a.PoolBurnPolicy?.ForAsset(1 - terms.InputSide) : null);
                if (terms.InputBurn != quote.InputBurn || terms.OutputBurn != quote.OutputBurn)
                    throw new InvalidOperationException("Swap automatic burn differs from pool policy.");
                nextA = terms.InputSide == 0 ? checked(nextA + terms.Amount) : nextA - quote.GrossReceive;
                nextB = terms.InputSide == 1 ? checked(nextB + terms.Amount) : nextB - quote.GrossReceive;
                if (tx.Outputs[2].AssetId != (terms.InputSide == 0 ? b.Output.AssetId : a.Output.AssetId) || tx.Outputs[2].Amount != quote.Receive)
                    throw new InvalidOperationException("The swap payout must match the pool quote.");
            }
            if (tx.Outputs[0].Amount != nextA || tx.Outputs[1].Amount != nextB)
                throw new InvalidOperationException("Pool reserves must follow the swap quote or return intact to the provider on closure.");
        }

        private static void ValidateParticipation(PoolParticipation state)
        {
            if (state == null) return;
            if (state.Shares == null || state.Shares.Count == 0 || state.Shares.Count > 1000 || state.Shares.Any(item =>
                item.Key == null || item.Key.Length != 64 || item.Key.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) || item.Value <= 0) || state.Total <= 0)
                throw new InvalidOperationException("Invalid liquidity shares.");
        }
        public static long LiquidityAmount(long reserve, long shares, long total, bool roundUp)
        {
            if (reserve <= 0 || shares <= 0 || total <= 0) throw new ArgumentOutOfRangeException(nameof(shares));
            return (long)(((BigInteger)reserve * shares + (roundUp ? total - 1 : 0)) / total);
        }
        private static PoolParticipation ChangedParticipation(UnspentOutput a, string address, long shares, bool add)
        {
            if (a.PoolParticipation == null) throw new InvalidOperationException("User liquidity is disabled for this pool.");
            var next = a.PoolParticipation.Copy();
            long owned; next.Shares.TryGetValue(address, out owned);
            if (shares <= 0 || !add && (shares > owned || shares >= next.Total || address == a.Output.OneTimeAddress && shares == owned))
                throw new InvalidOperationException("Insufficient liquidity shares. Close the pool to withdraw its final position.");
            long balance = add ? checked(owned + shares) : owned - shares;
            if (balance == 0) next.Shares.Remove(address); else next.Shares[address] = balance;
            ValidateParticipation(next); return next;
        }
        private static void ValidateLiquidityChange(Transaction tx, UnspentOutput a, UnspentOutput b)
        {
            var terms = tx.LiquidityChange;
            bool add = tx.Kind == TransactionKind.LiquidityAdd;
            if (terms == null || !tx.Inputs.Take(tx.Inputs.Count - 2).Any(input => Crypto.Sha256(input.PublicKey) == terms.Address))
                throw new InvalidOperationException("The liquidity holder must sign free funding inputs.");
            var next = ChangedParticipation(a, terms.Address, terms.Shares, add);
            if (tx.PoolParticipation?.Canonical() != next.Canonical()) throw new InvalidOperationException("Invalid liquidity share update.");
            long amountA = LiquidityAmount(a.Output.Amount, terms.Shares, a.PoolParticipation.Total, add);
            long amountB = LiquidityAmount(b.Output.Amount, terms.Shares, a.PoolParticipation.Total, add);
            if (amountA <= 0 || amountB <= 0 || tx.Outputs[0].Amount != (add ? checked(a.Output.Amount + amountA) : a.Output.Amount - amountA) ||
                tx.Outputs[1].Amount != (add ? checked(b.Output.Amount + amountB) : b.Output.Amount - amountB))
                throw new InvalidOperationException("Liquidity must follow the current reserve ratio.");
            if (!add && (tx.Outputs.Count < 4 || tx.Outputs[2].AssetId != a.Output.AssetId || tx.Outputs[2].Amount != amountA ||
                tx.Outputs[2].OneTimeAddress != terms.Address || tx.Outputs[3].AssetId != b.Output.AssetId ||
                tx.Outputs[3].Amount != amountB || tx.Outputs[3].OneTimeAddress != terms.Address))
                throw new InvalidOperationException("Withdrawals must pay both assets to the share owner.");
        }
        public static UnsignedTokenExchange PrepareLiquidityChange(Blockchain chain, IEnumerable<Transaction> pending,
            IEnumerable<string> publicKeys, UnspentOutput reserveA, UnspentOutput reserveB, string providerKey,
            string address, long shares, bool add, long fee, IEnumerable<string> excludedInputs = null)
        {
            var queue = pending.ToArray();
            var keys = Keys(publicKeys, address, fee);
            if (reserveA == null || reserveB == null || !reserveA.IsPoolReserve || !reserveB.IsPoolReserve ||
                reserveA.TransactionId != reserveB.TransactionId || reserveA.OutputIndex != 0 || reserveB.OutputIndex != 1 ||
                reserveA.Output.OneTimeAddress != Crypto.Sha256(providerKey) || reserveB.Output.OneTimeAddress != Crypto.Sha256(providerKey))
                throw new InvalidOperationException("Invalid pool reserves.");
            var next = ChangedParticipation(reserveA, address, shares, add);
            long amountA = LiquidityAmount(reserveA.Output.Amount, shares, reserveA.PoolParticipation.Total, add);
            long amountB = LiquidityAmount(reserveB.Output.Amount, shares, reserveA.PoolParticipation.Total, add);
            if (amountA <= 0 || amountB <= 0) throw new InvalidOperationException("Liquidity amount is too small.");
            var outputs = new List<TransactionOutput> {
                Output(reserveA.Output.AssetId, add ? checked(reserveA.Output.Amount + amountA) : reserveA.Output.Amount - amountA, reserveA.Output.OneTimeAddress),
                Output(reserveB.Output.AssetId, add ? checked(reserveB.Output.Amount + amountB) : reserveB.Output.Amount - amountB, reserveB.Output.OneTimeAddress) };
            if (!add) { outputs.Add(Output(reserveA.Output.AssetId, amountA, address)); outputs.Add(Output(reserveB.Output.AssetId, amountB, address)); }
            var amounts = new Dictionary<string, long>();
            if (add) { amounts.Add(reserveA.Output.AssetId ?? "povix", amountA); amounts.Add(reserveB.Output.AssetId ?? "povix", amountB); }
            var selected = new List<UnspentOutput>();
            Fund(chain, queue, keys, amounts, address, fee, selected, outputs,
                new HashSet<string>((excludedInputs ?? new string[0]).Concat(new[] { Key(reserveA), Key(reserveB) })));
            int count = selected.Count; selected.Add(reserveA); selected.Add(reserveB); keys[Crypto.Sha256(providerKey)] = providerKey;
            return Build(selected, keys, outputs, count, fee, add ? TransactionKind.LiquidityAdd : TransactionKind.LiquidityRemove,
                participation: next, change: new LiquidityChangeTerms { Address = address, Shares = shares }, burnPolicy: reserveA.PoolBurnPolicy);
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
            List<TransactionOutput> outputs, int userCount, long fee, TransactionKind kind, LiquiditySwapTerms terms = null, PoolParticipation participation = null, LiquidityChangeTerms change = null, PoolBurnPolicy burnPolicy = null)
        {
            var tx = new Transaction { Kind = kind, PoolBurnPolicy = burnPolicy?.Copy(), LiquiditySwap = terms, PoolParticipation = participation, LiquidityChange = change, TimestampUtcTicks = DateTime.UtcNow.Ticks, Fee = fee, Outputs = outputs,
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
        public PoolParticipation PoolParticipation => transaction.PoolParticipation?.Copy();
        public LiquidityChangeTerms LiquidityChange => transaction.LiquidityChange;
        public long Fee => transaction.Fee;
        public bool BurnEnabled => transaction.PoolBurnEnabled;
        public PoolBurnPolicy BurnPolicy => transaction.PoolBurnPolicy?.Copy();
        public long InputBurn => transaction.LiquiditySwap?.InputBurn ?? 0;
        public long OutputBurn => transaction.LiquiditySwap?.OutputBurn ?? 0;
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
            if ((transaction.Kind == TransactionKind.LiquiditySwap || transaction.Kind == TransactionKind.LiquidityAdd || transaction.Kind == TransactionKind.LiquidityRemove) && values.Length == UserInputCount)
            {
                if (values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("One signature is required for every funding input.");
                values = values.Concat(new[] { string.Empty, string.Empty }).ToArray();
            }
            else if (values.Length != transaction.Inputs.Count || values.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("One signature is required for every input.");
            var tx = new Transaction { PoolBurnPolicy = transaction.PoolBurnPolicy?.Copy(), PoolParticipation = transaction.PoolParticipation?.Copy(), LiquidityChange = transaction.LiquidityChange, Kind = transaction.Kind, Fee = transaction.Fee, TimestampUtcTicks = transaction.TimestampUtcTicks,
                LiquiditySwap = transaction.LiquiditySwap == null ? null : new LiquiditySwapTerms {
                    InputSide = transaction.LiquiditySwap.InputSide, Amount = transaction.LiquiditySwap.Amount,
                    InputBurn = transaction.LiquiditySwap.InputBurn, OutputBurn = transaction.LiquiditySwap.OutputBurn },
                Outputs = Outputs.ToList(), Inputs = transaction.Inputs.Select((input, index) => new TransactionInput {
                    TransactionId = input.TransactionId, OutputIndex = input.OutputIndex, PublicKey = input.PublicKey, Signature = values[index] }).ToList() };
            tx.Id = tx.CalculateId(); return tx;
        }
    }
}
