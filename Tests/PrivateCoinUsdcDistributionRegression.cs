using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PrivateCoin.Core;
class PrivateCoinUsdcDistributionRegression
{
    static int checks;
    static void Check(bool value) { if (!value) throw new Exception("Check failed: " + checks); checks++; }
    static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static string Id(Transaction tx) => (string)typeof(Transaction).GetMethod("CalculateId", Hidden).Invoke(tx, null);
    static TokenRuntime Runtime(UnspentOutput item) => (TokenRuntime)typeof(UnspentOutput).GetProperty("TokenRuntime", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(item, null);
    static void Apply(Transaction tx, Dictionary<string, UnspentOutput> state) => typeof(Blockchain).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { tx, state, false, false, Blockchain.ConsensusVersion, 1000 });
    static void Reject(Action action) { try { action(); } catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { checks++; return; } catch (InvalidOperationException) { checks++; return; } throw new Exception("Expected rejection"); }
    static Transaction Registration(string address, bool reward) {
        var tx = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = DateTime.UtcNow.Ticks,
            Outputs = new List<TransactionOutput> { new TransactionOutput { OneTimeAddress = address, Amount = 0 } } };
        if (reward) tx.Outputs.Add(new TransactionOutput { OneTimeAddress = address, AssetId = InitialLaunch.UsdcTokenId, Amount = InitialLaunch.WalletUsdcReward });
        tx.Id = Id(tx); return tx;
    }
    public static void Main() {
        var chain = new Blockchain();
        string first = new Wallet().CreateReceiveAddress(), second = new Wallet().CreateReceiveAddress();
        var bootstrap = chain.CreateWalletCreationTransaction(first, new Transaction[0]);
        Check(bootstrap.Outputs[2].Amount == 100 * InitialLaunch.FundingUnit);
        chain.AddBlock(new[] { bootstrap });
        Check(chain.GetTokenRuntime(InitialLaunch.UsdcTokenId).Issued == InitialLaunch.WalletUsdcReward);
        var pending = chain.CreateWalletCreationTransaction(second, new Transaction[0]);
        Check(pending.Outputs.Count == 2 && pending.Outputs[1].Amount == InitialLaunch.WalletUsdcReward);
        Check(chain.GetTokenBalance(new[] { second }, InitialLaunch.UsdcTokenId) == 0);
        Reject(() => chain.CreateWalletCreationTransaction(second, new[] { pending }));
        chain.AddBlock(new[] { pending });
        Check(chain.GetTokenBalance(new[] { second }, InitialLaunch.UsdcTokenId) == InitialLaunch.WalletUsdcReward);
        var state = (Dictionary<string, UnspentOutput>)typeof(Blockchain).GetMethod("BuildUtxo", Hidden).Invoke(chain, null);
        Reject(() => Apply(Registration(second, true), state));
        // Advance through all remaining rewards using the real consensus Apply path.
        for (int index = 2; index < InitialLaunch.UsdcRewardedWalletLimit; index++) {
            string address = index.ToString("x64");
            var tx = Registration(address, true);
            if (index == 9999) {
                var invalid = Registration(address, true); invalid.Outputs[1].Amount++; invalid.Id = Id(invalid);
                Reject(() => Apply(invalid, state));
            }
            Apply(tx, state);
        }
        var runtime = state.Values.Where(item => Runtime(item) != null && Runtime(item).Token.Id == InitialLaunch.UsdcTokenId).Select(Runtime).Single();
        Check(runtime.Issued == InitialLaunch.FundingTokenSupply && runtime.Supply == InitialLaunch.FundingTokenSupply);
        Check(state.Values.Where(item => item.OutputIndex >= 0 && item.Output.AssetId == InitialLaunch.UsdcTokenId).Sum(item => item.Output.Amount) == InitialLaunch.FundingTokenSupply);
        string last = new string('f',64);
        Reject(() => Apply(Registration(last, true), state));
        Apply(Registration(last, false), state);
        Check(state.Values.Where(item => Runtime(item) != null && Runtime(item).Token.Id == InitialLaunch.UsdcTokenId).Select(Runtime).Single().Issued == InitialLaunch.FundingTokenSupply);
        Console.WriteLine(checks + " USDC distribution checks passed; all 10000 rewards applied through consensus");
    }
}
