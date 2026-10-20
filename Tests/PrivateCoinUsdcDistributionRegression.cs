using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PrivateCoin.Core;
// Regression for removal of the former registration-funded USDC distribution.
class PrivateCoinUsdcDistributionRegression
{
    static int checks;
    static void Check(bool ok) { if (!ok) throw new Exception("Check failed: " + checks); checks++; }
    static string Id(Transaction tx) => (string)typeof(Transaction).GetMethod("CalculateId", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tx, null);
    static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception("Expected rejection"); }
    public static void Main() {
        using (var owner = new Wallet()) {
            var chain = new Blockchain(); string first = owner.CreateReceiveAddress(); var bootstrap = chain.CreateWalletCreationTransaction(first, new Transaction[0]);
            Check(bootstrap.Outputs.Count == 3 && bootstrap.Outputs[1].Amount == 0 && bootstrap.Outputs[1].AssetId == InitialLaunch.PusdtTokenId);
            chain.AddBlock(new[] { bootstrap }); Check(chain.GetTokens().Count == 1 && chain.GetTokens().Single().Supply == 0);
            Check(chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Issued == 0 && chain.GetTokenBalance(new[] { first }, InitialLaunch.PusdtTokenId) == 0);
            string next = new string('f', 64); var registration = chain.CreateWalletCreationTransaction(next, new Transaction[0]);
            Check(registration.Outputs.Count == 1 && registration.Outputs[0].Amount == 0);
            var forged = new Transaction { Kind = TransactionKind.WalletCreate, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                Outputs = new List<TransactionOutput> { registration.Outputs[0], new TransactionOutput { AssetId = "d0c0ff968d7093a5b3e4b9a617112e66e4d62d5765833d0852387b5ea7d1ab98", Amount = 100000000, OneTimeAddress = next } } };
            forged.Id = Id(forged); Reject(() => chain.ValidatePendingTransactions(new[] { forged }));
            forged.Outputs[1].AssetId = InitialLaunch.PusdtTokenId; forged.Id = Id(forged); Reject(() => chain.ValidatePendingTransactions(new[] { forged }));
            chain.AddProofOfStakeBlock(new[] { registration }, new[] { owner.CreateValidatorStake(first, Blockchain.WalletCreationReward) });
            Check(chain.GetTokenBalances(new[] { next }).Count == 1 && chain.GetTokenBalances(new[] { next }).Single().Amount == 0);
            Check(chain.GetTokenRuntime(InitialLaunch.PusdtTokenId).Issued == 0 && chain.IsValid());
            Console.WriteLine(checks + " removed-USDC and zero-pUSDT registration checks passed");
        }
    }
}
