using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using PrivateCoin.Core;
internal static class PrivateCoinTokenChangeRegression
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
                var stake = creator.CreateStakeLockTransaction(chain, pending, owner, Blockchain.OneCoin, 0); pending.Add(stake);
                Check(stake.Inputs.Single().TransactionId == token.Id && chain.GetActiveValidators(pending).Count == 3 && chain.Blocks.Count == 1,
                    "the exact returned change can lock collateral immediately without a block");
                var payment = creator.CreateTransaction(chain, pending, "change-recipient", 123, 5); pending.Add(payment);
                payment.TransactionApproval = chain.CreateTransactionApproval(payment, pending, validators.Take(1));
                Check(chain.HasValidTransactionApproval(payment, pending) && chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == 12 &&
                    chain.GetSpendableBalance(new[] { "change-recipient" }, pending) == 0,
                    "change-funded native transfer credits only its validator fee before confirmation");
                while (pending.Count < 20) pending.Add(chain.CreateWalletCreationTransaction("change-batch-" + pending.Count, pending));
                var block = chain.AddProofOfStakeBlock(chain.SelectApprovedValidationBatch(pending), validators);
                Check(chain.IsValid() && block.ConsensusVersion == Blockchain.ConsensusVersion && chain.Blocks.Count == 2 && chain.GetBalance(new[] { "change-recipient" }) == 123,
                    "a later complete batch independently confirms the change-funded stake and payment");
                Check(chain.GetBalancesByAddress().Values.Sum() == 15 * Blockchain.WalletCreationReward + ProofOfStake.GetBlockReward(block.Height),
                    "immediate change and fee credits preserve native supply without duplicate payments");
            }
            Console.WriteLine(checks + " immediate token change checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
