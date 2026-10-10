using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using PrivateCoin.Core;
internal static class PrivateCoinTokenConfirmationRegression
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static List<Transaction> Copy(List<Transaction> values)
    {
        var serializer = new DataContractSerializer(typeof(List<Transaction>));
        using (var stream = new MemoryStream()) { serializer.WriteObject(stream, values); stream.Position = 0; return (List<Transaction>)serializer.ReadObject(stream); }
    }
    public static int Main()
    {
        try
        {
            using (var creator = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
            {
                var chain = new Blockchain(); var pending = new List<Transaction>();
                string owner = creator.CreateReceiveAddress(), a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress();
                foreach (string address in new[] { owner, a, b }) pending.Add(chain.CreateWalletCreationTransaction(address, pending));
                pending.Add(first.CreateStakeLockTransaction(chain, pending, a, Blockchain.WalletCreationReward, 0));
                pending.Add(second.CreateStakeLockTransaction(chain, pending, b, Blockchain.WalletCreationReward, 0));
                var validators = new[] { first.CreateValidatorStake(a, Blockchain.WalletCreationReward), second.CreateValidatorStake(b, Blockchain.WalletCreationReward) };
                var token = creator.CreateTokenTransaction(chain, pending, "Troco", "TRO", 0, 100, owner, 7); pending.Add(token);
                Check(chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, pending) == 0,
                    "before approval token creation reserves funding without claiming its change");
                token.TransactionApproval = chain.CreateTransactionApproval(token, pending, validators.Take(1));
                Check(chain.HasValidTransactionApproval(token, pending) && chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, pending) == Blockchain.WalletCreationReward - 7 &&
                    chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 7 && chain.Blocks.Count == 1,
                    "approval deducts only the exact chosen fee and immediately returns the rest to the original wallet without a block");
                Check(new Blockchain(chain.Blocks).GetSpendableBalance(creator.OwnedOneTimeAddresses, Copy(pending)) == Blockchain.WalletCreationReward - 7,
                    "restart preserves immediately spendable token creation change");
                var forged = Copy(pending); forged.Single(tx => tx.Id == token.Id).TransactionApproval.Proof.Signature = Convert.ToBase64String(new byte[256]);
                Check(chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, forged) == 0,
                    "forged approval cannot release token creation change");
                var entry = chain.GetTokenBalances(creator.OwnedOneTimeAddresses, pending).Single();
                Check(chain.GetTokens(pending).Single().Id == token.Token.Id && entry.Amount == 100 && entry.CreationHeight == null && entry.Confirmations == 0 && entry.ValidationCount == 1,
                    "validated creation registers real token supply without inventing a block or block confirmations");
                Check(chain.GetTokens(forged).Count == 0 && chain.GetRegisteredTokenOutputs(creator.OwnedOneTimeAddresses, forged).Count == 0,
                    "forged approval cannot confirm token registration or mint spendable assets");
                var stake = creator.CreateStakeLockTransaction(chain, pending, owner, Blockchain.OneCoin, 0); pending.Add(stake);
                Check(stake.Inputs.Single().TransactionId == token.Id && chain.GetActiveValidators(pending).Count == 3 && chain.Blocks.Count == 1,
                    "the exact returned change can lock collateral immediately without a block");
                var payment = creator.CreateTokenTransferTransaction(chain, pending, token.Token.Id, new string('e', 64), 30, 5); pending.Add(payment);
                payment.TransactionApproval = chain.CreateTransactionApproval(payment, pending, validators.Take(1));
                Check(chain.HasValidTransactionApproval(payment, pending) && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 12 &&
                    chain.GetSpendableTokenOutputs(new[] { new string('e', 64) }, pending, token.Token.Id).Count == 0,
                    "a confirmed creation funds an actual signed token movement and pays its validator fee without a block");
                while (pending.Count < 20) pending.Add(chain.CreateWalletCreationTransaction("change-batch-" + pending.Count, pending));
                var block = chain.AddProofOfStakeBlock(chain.SelectApprovedValidationBatch(pending), validators);
                Check(chain.IsValid() && block.ConsensusVersion == Blockchain.ConsensusVersion && chain.Blocks.Count == 2 && chain.GetTokenBalance(new[] { new string('e', 64) }, token.Token.Id) == 30,
                    "a later complete batch includes the registered creation and confirms its token movement");
                var historical = chain.Blocks.ToList(); historical.Last().ConsensusVersion = 12;
                LegacyConsensusFixture.Resign(historical.Last(), validators); LegacyConsensusFixture.Mine(historical.Last());
                Check(new Blockchain(historical).IsValid(), "historical v12 rules restore without changing their native change policy");
                Check(chain.GetBalancesByAddress().Values.Sum() == 15 * Blockchain.WalletCreationReward + ProofOfStake.GetBlockReward(block.Height),
                    "immediate change and fee credits preserve native supply without duplicate payments");
            }
            Console.WriteLine(checks + " token confirmation checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
