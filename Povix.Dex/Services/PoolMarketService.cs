using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PrivateCoin.Core;

namespace Povix.Dex.Services
{
    public sealed partial class TokenNetworkService
    {
        // A fresh chain snapshot replaces the history on every read, including after a reorg.
        public object GetPoolMarket(string poolId, int interval)
        {
            lock (sync)
            {
                if (!new[] { 60, 300, 900, 3600, 86400 }.Contains(interval))
                    throw new ArgumentOutOfRangeException(nameof(interval));
                var pool = Snapshot(poolId);
                if (pool == null) return null;
                var history = new List<MarketMovement>();
                string reserveId = poolId;
                bool started = false, closed = false;
                long previousA = 0, previousB = 0;
                foreach (var block in blockchain.Blocks)
                foreach (var tx in block.Transactions)
                {
                    var record = submitted.FirstOrDefault(item => item.Transaction.Id == tx.Id && item.Exchange?.PoolId == poolId);
                    bool next = started && !closed && tx.Inputs.Any(input => input.TransactionId == reserveId && input.OutputIndex == 0) &&
                        tx.Inputs.Any(input => input.TransactionId == reserveId && input.OutputIndex == 1) &&
                        (tx.Kind == TransactionKind.LiquiditySwap || tx.Kind == TransactionKind.LiquidityClose || tx.Kind == TransactionKind.LiquidityAdd || tx.Kind == TransactionKind.LiquidityRemove || TokenLaunch.IsStep(tx.Kind) || tx.Kind == TransactionKind.InitialLaunchPurchase || record != null);
                    if (tx.Id != poolId && !next) continue;
                    if (tx.Outputs.Count < 2) continue;
                    string operation = tx.Id == poolId ? "pool" : tx.Kind == TransactionKind.LiquidityClose ? "close" :
                        tx.Kind == TransactionKind.InitialLaunchPurchase ? (tx.Outputs[0].Amount == InitialLaunch.PrincipalSupply ? "launch-open" : "launch-contribute") : tx.Kind == TransactionKind.LaunchRefund ? "close" : tx.Kind == TransactionKind.LaunchActivate ? "launch-open" : tx.Kind == TransactionKind.LaunchContribute ? "launch-contribute" : tx.Kind == TransactionKind.LiquiditySwap ? "swap" : tx.Kind == TransactionKind.LiquidityAdd ? "add" : tx.Kind == TransactionKind.LiquidityRemove ? "remove" : record.Exchange.Operation;
                    long a = tx.Outputs[0].Amount, b = tx.Outputs[1].Amount;
                    history.Add(new MarketMovement { Id = tx.Id, Operation = operation, Status = "confirmed", Height = block.Height,
                        Time = UnixTime(block.TimestampUtcTicks), ReserveA = a, ReserveB = b,
                        DeltaA = operation == "close" ? -previousA : a - previousA,
                        DeltaB = operation == "close" ? -previousB : b - previousB });
                    reserveId = tx.Id; previousA = a; previousB = b; started = true; closed = operation == "close";
                }
                // Pending and rejected records are visible in the cash book, never in candles.
                foreach (var record in submitted.Where(item => item.Exchange?.PoolId == poolId))
                {
                    if (history.Any(item => item.Id == record.Transaction.Id)) continue;
                    var tx = record.Transaction;
                    if (tx.Outputs.Count < 2) continue;
                    var queued = pending.FirstOrDefault(item => item.Id == tx.Id);
                    var prior = submitted.FirstOrDefault(item => item.Transaction.Id == record.Exchange.PreviousReserveId)?.Transaction;
                    long a = prior?.Outputs[0].Amount ?? 0, b = prior?.Outputs[1].Amount ?? 0;
                    history.Add(new MarketMovement { Id = tx.Id, Operation = record.Exchange.Operation,
                        Status = queued == null ? "rejected" : Blockchain.IsSelfValidatedOperation(queued) || blockchain.HasValidTransactionApproval(queued, pending) ? "validated" : "pending",
                        Time = UnixTime(tx.TimestampUtcTicks), ReserveA = tx.Outputs[0].Amount, ReserveB = tx.Outputs[1].Amount,
                        DeltaA = record.Exchange.Operation == "close" ? -a : tx.Outputs[0].Amount - a,
                        DeltaB = record.Exchange.Operation == "close" ? -b : tx.Outputs[1].Amount - b });
                }
                decimal scaleA = Scale(pool.AssetA.Decimals), scaleB = Scale(pool.AssetB.Decimals);
                var candles = history.Where(item => item.Status == "confirmed" && item.Operation == "swap" && item.ReserveA > 0 && item.ReserveB > 0)
                    .GroupBy(item => item.Time / interval * interval).OrderBy(group => group.Key)
                    .Select(group => {
                        var prices = group.Select(item => (item.ReserveB / scaleB) / (item.ReserveA / scaleA)).ToArray();
                        return new { time = group.Key, open = prices.First(), high = prices.Max(), low = prices.Min(), close = prices.Last(),
                            volume = group.Sum(item => Math.Abs(item.DeltaA / scaleA)) };
                    }).ToArray();
                var latest = history.LastOrDefault(item => item.Status == "confirmed");
                return new { pool = PoolResponse(pool), tokens = new[] { MarketToken(pool.AssetA) }, interval, updatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    price = latest == null || closed || !pool.Available || pool.Transaction.Kind == TransactionKind.LaunchCreate || pool.Transaction.Kind == TransactionKind.LaunchContribute || latest.ReserveA <= 0 ? (decimal?)null : (latest.ReserveB / scaleB) / (latest.ReserveA / scaleA),
                    candles = candles.Skip(Math.Max(0, candles.Length - 200)).ToArray(),
                    ledger = history.OrderByDescending(item => item.Time).ThenByDescending(item => item.Height).Take(100).Select(item => new {
                        transactionId = item.Id, operation = item.Operation, status = item.Status, time = item.Time, blockHeight = item.Height,
                        deltaAAtomic = Atomic(item.DeltaA), deltaBAtomic = Atomic(item.DeltaB),
                        reserveAAtomic = Atomic(item.ReserveA), reserveBAtomic = Atomic(item.ReserveB) }).ToArray(),
                    ledgerTotal = history.Count, peerCount = node.ConnectedPeerCount };
            }
        }
        private object MarketToken(TokenDefinition asset)
        {
            var token = asset.Id == "povix" ? asset : blockchain.GetTokens().FirstOrDefault(item => item.Id == asset.Id) ?? asset;
            var creation = blockchain.Blocks.SelectMany(block => block.Transactions)
                .FirstOrDefault(tx => tx.Kind == TransactionKind.TokenCreate && tx.Token?.Id == token.Id);
            bool native = token.Id == "povix";
            var blocks = blockchain.Blocks.ToArray();
            var creationBlock = blocks.FirstOrDefault(block => block.Transactions.Any(tx =>
                native ? tx.Outputs.Any(output => output.AssetId == null && output.Amount > 0) :
                tx.Kind == TransactionKind.TokenCreate && tx.Token?.Id == token.Id ||
                tx.Kind == TransactionKind.InitialLaunchCreate && InitialLaunch.FundingTokens().Any(item => item.Id == token.Id)));
            var addresses = blocks.SelectMany(block => block.Transactions).SelectMany(tx => tx.Outputs)
                .Where(output => native ? output.AssetId == null : output.AssetId == token.Id)
                .Select(output => output.OneTimeAddress).Distinct(StringComparer.Ordinal).ToArray();
            var outputs = blockchain.GetUnspentOutputs(addresses)
                .Where(item => (native ? item.Output.AssetId == null : item.Output.AssetId == token.Id) && item.Output.Amount > 0).ToArray();
            long supply = native ? outputs.Aggregate(0L, (sum, item) => checked(sum + item.Output.Amount)) : token.Supply;
            // Pool reserves and wallet balances circulate together; unreleased locks do not.
            bool initialLaunchActive = blockchain.GetInitialLaunchStatus().Active;
            long circulating = outputs.Where(item => item.Output.UnlockHeight <= blocks.Length &&
                    (!item.Output.InitialLaunchLocked || initialLaunchActive) && !item.InitialLaunchCollecting &&
                    item.TransactionKind != TransactionKind.StakeLock &&
                    item.TransactionKind != TransactionKind.LaunchCreate && item.TransactionKind != TransactionKind.LaunchContribute)
                .Aggregate(0L, (sum, item) => checked(sum + item.Output.Amount));
            int holders = outputs.Where(item => !item.IsPoolReserve)
                .Select(item => item.Output.OneTimeAddress).Distinct(StringComparer.Ordinal).Count();
            TokenProfile profile;
            profiles.TryGetValue(token.Id, out profile);
            bool matching = creation != null && profile != null && profile.CreationId == creation.Id;
            return new { id = token.Id, name = matching ? profile.Name : token.Name, symbol = matching ? profile.Symbol : token.Symbol, decimals = token.Decimals,
                supplyAtomic = Atomic(supply), circulatingSupplyAtomic = Atomic(circulating), holderCount = holders,
                createdUtc = creationBlock == null ? null : new DateTime(creationBlock.TimestampUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture), native,
                description = matching ? profile.Description : token.Policy?.Description };
        }
        private static long UnixTime(long ticks) => (ticks - 621355968000000000L) / TimeSpan.TicksPerSecond;
        private static decimal Scale(int decimals) { decimal result = 1; for (int i = 0; i < decimals; i++) result *= 10; return result; }
        private sealed class MarketMovement
        {
            public string Id, Operation, Status;
            public int? Height;
            public long Time, ReserveA, ReserveB, DeltaA, DeltaB;
        }
    }
}
