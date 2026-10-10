using System;
using System.Collections.Generic;
using System.Linq;
using PrivateCoin.Core;
using PrivateCoin.Desktop;
internal static class PrivateCoinDeferredValidationRegression
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    public static int Main()
    {
        try
        {
            using (var creator = new Wallet()) using (var validator = new Wallet())
            {
                var chain = new Blockchain(); var queued = new List<Transaction>();
                string owner = creator.CreateReceiveAddress(), reward = validator.CreateReceiveAddress();
                queued.Add(chain.CreateWalletCreationTransaction(owner, queued));
                queued.Add(chain.CreateWalletCreationTransaction(reward, queued));
                queued.Add(validator.CreateStakeLockTransaction(chain, queued, reward, Blockchain.OneCoin, 0));
                var token = creator.CreateTokenTransaction(chain, queued, "Deferred", "DEF", 0, 100, owner, 3);
                queued.Add(token);
                var readiness = new NetworkReadiness(); long epoch = readiness.Epoch;
                foreach (var tx in queued.AsEnumerable().Reverse()) readiness.Defer(epoch, tx);
                readiness.Defer(epoch, token);
                Check(readiness.TakeDeferred(epoch).Length == 0 && chain.Blocks.Count == 1,
                    "receiving creation before synchronization cannot validate or create a block");
                Check(readiness.Accept(epoch, () => true, () => chain.IsValid()), "a valid synchronized chain releases received operations");
                var received = Blockchain.OrderByFeePriority(readiness.TakeDeferred(epoch)).ToList();
                Check(received.Count == 4 && readiness.TakeDeferred(epoch).Length == 0,
                    "deferred messages drain once and duplicates do not increase operation count");
                chain.ValidatePendingTransactions(received);
                token.TransactionApproval = chain.CreateTransactionApproval(token, received,
                    new[] { validator.CreateValidatorStake(reward, Blockchain.OneCoin) });
                Check(chain.HasValidTransactionApproval(token, received) && chain.Blocks.Count == 1 &&
                    chain.GetSpendableBalance(new[] { reward }, received) == 3 &&
                    chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, received) == Blockchain.WalletCreationReward - 3,
                    "a received token creation is approved and its exact fee paid after synchronization without a block");
                readiness.Disconnect(); epoch = readiness.Epoch;
                readiness.Defer(epoch - 1, token); readiness.Defer(epoch, token); readiness.Disconnect();
                epoch = readiness.Epoch; readiness.Accept(epoch, () => true, () => true);
                Check(readiness.TakeDeferred(epoch).Length == 0,
                    "disconnection discards the old inbox and stale callbacks cannot restore it");
            }
            Console.WriteLine(checks + " deferred validation checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
