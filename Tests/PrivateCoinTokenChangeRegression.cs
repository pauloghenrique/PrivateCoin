using System;
using System.Linq;
using PrivateCoin.Core;
internal static class PrivateCoinTokenChangeRegression
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    public static int Main()
    {
        try
        {
            using (var creator = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
            {
                var chain = new Blockchain();
                string owner = creator.CreateReceiveAddress(), a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress();
                LegacyConsensusFixture.FundBatch(chain, owner, a, b);
                LegacyConsensusFixture.SelfBlock(chain, new[] {
                    first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 0),
                    second.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 0) });
                var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
                using (var batches = new ValidationBatchFixture(chain))
                {
                    var token = creator.CreateTokenTransaction(chain, new Transaction[0], "Troco", "TRO", 0, 100, owner, 7);
                    var pending = new[] { token };
                    token.TransactionApproval = chain.CreateTransactionApproval(token, pending, validators.Take(1));
                    int height = chain.Blocks.Count;
                    long before = chain.GetBalance(creator.OwnedOneTimeAddresses);
                    Check(chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, pending) == 0 && chain.GetBalance(creator.OwnedOneTimeAddresses) == before,
                        "approval reserves funding while confirmed native balance remains unchanged");
                    bool rejected = false;
                    try { creator.CreateStakeLockTransaction(chain, pending, owner, Blockchain.OneCoin, 0); } catch (InvalidOperationException) { rejected = true; }
                    Check(rejected && chain.Blocks.Count == height, "unconfirmed token creation change cannot activate collateral");
                    batches.Confirm(chain, pending, validators);
                    Check(chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, new Transaction[0]) == before - 7,
                        "block confirmation deducts exactly the selected fee and releases native change");
                    var stake = creator.CreateStakeLockTransaction(chain, new Transaction[0], owner, Blockchain.OneCoin, 0);
                    Check(stake.Inputs.Single().TransactionId == token.Id && chain.GetActiveValidators(new[] { stake }).Count == 2,
                        "confirmed creation change can fund collateral which still waits for its own block");
                    LegacyConsensusFixture.SelfBlock(chain, stake);
                    Check(chain.IsValid() && chain.GetActiveValidators().Count == 3 &&
                        chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, new Transaction[0]) == before - 7 - Blockchain.OneCoin,
                        "collateral becomes active and returns change only after its block");
                }
            }
            Console.WriteLine(checks + " block token change checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
