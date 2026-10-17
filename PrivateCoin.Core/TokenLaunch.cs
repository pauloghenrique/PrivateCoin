using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace PrivateCoin.Core
{
    public static class TokenLaunch
    {
        public const int MaximumContributors = 100;
        public static bool IsOperation(TransactionKind kind) => kind == TransactionKind.LaunchCreate || IsStep(kind);
        public static bool IsStep(TransactionKind kind) => kind == TransactionKind.LaunchContribute || kind == TransactionKind.LaunchActivate || kind == TransactionKind.LaunchRefund;
        public static long Allocation(LaunchPoolState state, long contribution) => (long)((BigInteger)state.SaleTokens * contribution / state.Target);
        public static void ValidateState(LaunchPoolState state)
        {
            if (state == null || state.Target <= 1 || state.SaleTokens <= 0 || state.TotalTokens <= state.SaleTokens || state.DeadlineHeight <= 0 ||
                state.Contributions == null || state.Contributions.Count == 0 || state.Contributions.Count > MaximumContributors ||
                state.Contributions.Any(item => item.Key == null || item.Key.Length != 64 || item.Key.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) || item.Value <= 0 || Allocation(state, item.Value) <= 0) ||
                state.Raised > state.Target)
                throw new InvalidOperationException("Invalid launch terms or contributions.");
        }
        // These outputs are deterministic. The fee payer cannot choose payout addresses or amounts.
        public static List<TransactionOutput> ReserveOutputs(LaunchPoolState state, string assetA, string assetB, string provider, TransactionKind kind)
        {
            var holders = state.Contributions.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            if (kind == TransactionKind.LaunchRefund)
                return new[] { Output(assetA, state.TotalTokens, provider) }.Concat(holders.Select(item => Output(assetB, item.Value, item.Key))).ToList();
            if (kind != TransactionKind.LaunchActivate)
                return new List<TransactionOutput> { Output(assetA, state.TotalTokens, provider), Output(assetB, state.Raised, provider) };
            var payouts = holders.Select(item => Output(assetA, Allocation(state, item.Value), item.Key)).ToArray();
            long liquidity = state.TotalTokens - payouts.Sum(item => item.Amount);
            return new[] { Output(assetA, liquidity, provider), Output(assetB, state.Target, provider) }.Concat(payouts).ToList();
        }
        internal static void Validate(Transaction tx, UnspentOutput[] sources, int height)
        {
            ValidateState(tx.Launch);
            if (tx.PoolBurnEnabled || tx.LiquiditySwap != null || tx.LiquidityChange != null || tx.Outputs.Count < 2 || sources.Length == 0 || sources.Any(source => source == null))
                throw new InvalidOperationException("Invalid launch operation.");
            var state = tx.Launch;
            if (tx.Kind == TransactionKind.LaunchCreate)
            {
                if (sources.Any(source => source.IsPoolReserve) || tx.PoolParticipation != null || tx.Outputs[0].AssetId == null ||
                    tx.Outputs[0].AssetId == tx.Outputs[1].AssetId || tx.Outputs[0].OneTimeAddress != tx.Outputs[1].OneTimeAddress ||
                    state.Contributions.Count != 1 || !state.Contributions.ContainsKey(tx.Outputs[0].OneTimeAddress) || state.Raised >= state.Target ||
                    state.DeadlineHeight <= height || (long)state.DeadlineHeight - height > 1000000 ||
                    !tx.Inputs.Any(input => Crypto.Sha256(input.PublicKey) == tx.Outputs[0].OneTimeAddress))
                    throw new InvalidOperationException("Launch creation requires a token, creator funding and a future deadline.");
                Match(tx.Outputs, ReserveOutputs(state, tx.Outputs[0].AssetId, tx.Outputs[1].AssetId, tx.Outputs[0].OneTimeAddress, tx.Kind));
                return;
            }
            if (sources.Length < 3 || sources.Count(source => source.IsPoolReserve) != 2)
                throw new InvalidOperationException("A launch step requires both escrow reserves and free fee funding.");
            var a = sources[sources.Length - 2]; var b = sources[sources.Length - 1];
            if (!a.IsPoolReserve || !b.IsPoolReserve || a.Launch == null || b.Launch == null || a.Launch.Canonical() != b.Launch.Canonical() ||
                a.TransactionId != b.TransactionId || a.OutputIndex != 0 || b.OutputIndex != 1 || a.Output.OneTimeAddress != b.Output.OneTimeAddress ||
                a.Output.AssetId == null || a.Output.AssetId == b.Output.AssetId || a.Output.Amount != a.Launch.TotalTokens || b.Output.Amount != a.Launch.Raised ||
                state.Terms() != a.Launch.Terms())
                throw new InvalidOperationException("Launch escrow or immutable terms changed.");
            if (tx.Kind == TransactionKind.LaunchRefund)
            {
                if (height < state.DeadlineHeight || state.Raised >= state.Target || state.Canonical() != a.Launch.Canonical() || tx.PoolParticipation != null)
                    throw new InvalidOperationException("Refund requires a failed launch after its deadline.");
            }
            else
            {
                if (height >= state.DeadlineHeight || a.Launch.Raised >= state.Target)
                    throw new InvalidOperationException("The launch is no longer accepting contributions.");
                var changes = state.Contributions.Keys.Union(a.Launch.Contributions.Keys).Where(address =>
                    Value(state, address) != Value(a.Launch, address)).ToArray();
                if (changes.Length != 1 || Value(state, changes[0]) <= Value(a.Launch, changes[0]) ||
                    !tx.Inputs.Take(tx.Inputs.Count - 2).Any(input => Crypto.Sha256(input.PublicKey) == changes[0]) ||
                    tx.Kind != (state.Raised == state.Target ? TransactionKind.LaunchActivate : TransactionKind.LaunchContribute))
                    throw new InvalidOperationException("Exactly one signed contributor may increase their funding, without exceeding the target.");
                string expected = state.AllowUserLiquidity && tx.Kind == TransactionKind.LaunchActivate ?
                    new PoolParticipation { Shares = new Dictionary<string, long> { { a.Output.OneTimeAddress,
                        ReserveOutputs(state, a.Output.AssetId, b.Output.AssetId, a.Output.OneTimeAddress, tx.Kind)[0].Amount } } }.Canonical() : null;
                if (tx.PoolParticipation?.Canonical() != expected) throw new InvalidOperationException("Invalid initial pool shares on launch activation.");
            }
            Match(tx.Outputs, ReserveOutputs(state, a.Output.AssetId, b.Output.AssetId, a.Output.OneTimeAddress, tx.Kind));
        }
        private static long Value(LaunchPoolState state, string address) { long value; return state.Contributions.TryGetValue(address, out value) ? value : 0; }
        private static void Match(IList<TransactionOutput> actual, IList<TransactionOutput> expected)
        {
            if (actual.Count < expected.Count || expected.Where((item, index) => item.AssetId != actual[index].AssetId ||
                item.Amount != actual[index].Amount || item.OneTimeAddress != actual[index].OneTimeAddress).Any())
                throw new InvalidOperationException("Launch reserves and payouts must match the confirmed contributions exactly.");
        }
        private static TransactionOutput Output(string asset, long amount, string address) => new TransactionOutput { AssetId = asset, Amount = amount, OneTimeAddress = address };
    }

    public static partial class TokenExchange
    {
        public static UnsignedTokenExchange PrepareLaunch(Blockchain chain, IEnumerable<Transaction> pending, IEnumerable<string> publicKeys,
            string assetA, string assetB, string address, long initialContribution, long saleTokens, long liquidityTokens, long target,
            int deadlineHeight, bool allowUserLiquidity, long fee, IEnumerable<string> excludedInputs = null)
        {
            var queue = pending.ToArray(); ValidatePair(chain, queue, assetA, assetB);
            var state = new LaunchPoolState { Target = target, SaleTokens = saleTokens, TotalTokens = checked(saleTokens + liquidityTokens),
                DeadlineHeight = deadlineHeight, AllowUserLiquidity = allowUserLiquidity, Contributions = new Dictionary<string, long> { { address, initialContribution } } };
            TokenLaunch.ValidateState(state);
            if (assetA == null || liquidityTokens <= 0 || initialContribution >= target || deadlineHeight <= chain.Blocks.Count)
                throw new InvalidOperationException("Invalid launch creation.");
            var keys = Keys(publicKeys, address, fee); var selected = new List<UnspentOutput>();
            var outputs = TokenLaunch.ReserveOutputs(state, assetA, assetB, address, TransactionKind.LaunchCreate);
            Fund(chain, queue, keys, new Dictionary<string, long> { { assetA, state.TotalTokens }, { assetB ?? "povix", initialContribution } },
                address, fee, selected, outputs, new HashSet<string>(excludedInputs ?? new string[0]));
            return Build(selected, keys, outputs, selected.Count, fee, TransactionKind.LaunchCreate, launch: state);
        }
        public static UnsignedTokenExchange PrepareLaunchStep(Blockchain chain, IEnumerable<Transaction> pending, IEnumerable<string> publicKeys,
            UnspentOutput a, UnspentOutput b, string providerKey, string address, long amount, bool refund, long fee, IEnumerable<string> excludedInputs = null)
        {
            if (a?.Launch == null || b?.Launch == null || a.TransactionId != b.TransactionId || a.OutputIndex != 0 || b.OutputIndex != 1 ||
                a.Output.OneTimeAddress != Crypto.Sha256(providerKey) || b.Output.OneTimeAddress != Crypto.Sha256(providerKey))
                throw new InvalidOperationException("Invalid launch escrow.");
            var state = a.Launch.Copy();
            if (refund) {
                if (chain.Blocks.Count < state.DeadlineHeight || state.Raised >= state.Target) throw new InvalidOperationException("Launch refund is not yet available.");
            } else {
                if (amount <= 0 || chain.Blocks.Count >= state.DeadlineHeight) throw new InvalidOperationException("Launch contribution is not available.");
                long owned; state.Contributions.TryGetValue(address, out owned); state.Contributions[address] = checked(owned + amount);
            }
            TokenLaunch.ValidateState(state);
            var kind = refund ? TransactionKind.LaunchRefund : state.Raised == state.Target ? TransactionKind.LaunchActivate : TransactionKind.LaunchContribute;
            var keys = Keys(publicKeys, address, fee); var selected = new List<UnspentOutput>();
            var outputs = TokenLaunch.ReserveOutputs(state, a.Output.AssetId, b.Output.AssetId, a.Output.OneTimeAddress, kind);
            var amounts = new Dictionary<string, long>(); if (!refund) amounts.Add(b.Output.AssetId ?? "povix", amount);
            Fund(chain, pending.ToArray(), keys, amounts, address, fee, selected, outputs,
                new HashSet<string>((excludedInputs ?? new string[0]).Concat(new[] { Key(a), Key(b) })));
            int count = selected.Count; selected.Add(a); selected.Add(b); keys[Crypto.Sha256(providerKey)] = providerKey;
            PoolParticipation shares = kind == TransactionKind.LaunchActivate && state.AllowUserLiquidity ? new PoolParticipation {
                Shares = new Dictionary<string, long> { { a.Output.OneTimeAddress, outputs[0].Amount } } } : null;
            return Build(selected, keys, outputs, count, fee, kind, participation: shares, launch: state);
        }
    }
}
