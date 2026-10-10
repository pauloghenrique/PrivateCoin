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
    static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; } catch (ArgumentException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    public static int Main()
    {
        try
        {
            using (var creator = new Wallet()) using (var receiver = new Wallet()) using (var first = new Wallet()) using (var second = new Wallet())
            {
                var chain = new Blockchain();
                string owner = creator.CreateReceiveAddress(), destination = receiver.CreateReceiveAddress(), a = first.CreateReceiveAddress(), b = second.CreateReceiveAddress();
                LegacyConsensusFixture.FundBatch(chain, owner, destination, a, b);
                LegacyConsensusFixture.SelfBlock(chain, new[] {
                    first.CreateStakeLockTransaction(chain, new Transaction[0], a, Blockchain.OneCoin, 0),
                    second.CreateStakeLockTransaction(chain, new Transaction[0], b, Blockchain.OneCoin, 0) });
                var validators = new[] { first.CreateValidatorStake(a, Blockchain.OneCoin), second.CreateValidatorStake(b, Blockchain.OneCoin) };
                using (var batches = new ValidationBatchFixture(chain))
                {
                    int height = chain.Blocks.Count;
                    long validatorBefore = chain.GetSpendableBalance(first.OwnedOneTimeAddresses, new Transaction[0]);
                    var token = creator.CreateTokenTransaction(chain, new Transaction[0], "Blocos", "BLO", 0, 100, owner, 7);
                    var pending = new List<Transaction> { token };
                    token.TransactionApproval = chain.CreateTransactionApproval(token, pending, validators.Take(1));
                    Check(chain.HasValidTransactionApproval(token, pending) && chain.Blocks.Count == height && chain.GetTokens(pending).Count == 0,
                        "approval waits for the block before registering token supply");
                    Check(chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, pending) == 0 &&
                        chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == validatorBefore &&
                        chain.GetSpendableTokenOutputs(creator.OwnedOneTimeAddresses, pending, token.Token.Id).Count == 0,
                        "approved creation keeps funding reserved and does not release change, tokens or validator fees");
                    Reject(() => creator.CreateTokenTransferTransaction(chain, pending, token.Token.Id, destination, 30, 5),
                        "an approved creation cannot fund a transfer before the block");
                    var restored = new Blockchain(chain.Blocks); var saved = Copy(pending);
                    Check(restored.HasValidTransactionApproval(saved[0], saved) && restored.GetTokens(saved).Count == 0 && restored.GetSpendableBalance(creator.OwnedOneTimeAddresses, saved) == 0,
                        "restart preserves pending approval without confirming its assets");
                    var forged = Copy(pending); forged[0].TransactionApproval.Proof.Signature = Convert.ToBase64String(new byte[256]);
                    Check(!chain.HasValidTransactionApproval(forged[0], forged) && chain.GetTokens(forged).Count == 0,
                        "forged approval cannot register a token");
                    Block creationBlock = batches.Confirm(chain, pending, validators);
                    Check(chain.IsValid() && creationBlock.ConsensusVersion == 15 && chain.GetTokenBalance(creator.OwnedOneTimeAddresses, token.Token.Id) == 100 &&
                        chain.GetSpendableBalance(creator.OwnedOneTimeAddresses, new Transaction[0]) == Blockchain.WalletCreationReward - 7,
                        "creation block registers exact token supply and returns the original native change");
                    var entry = chain.GetTokenBalances(creator.OwnedOneTimeAddresses).Single();
                    Check(entry.CreationHeight == creationBlock.Height && entry.Confirmations == 1 && entry.ValidationCount == 1,
                        "creation metadata identifies its actual block and confirmation");
                    var transfer = creator.CreateTokenTransferTransaction(chain, new Transaction[0], token.Token.Id, destination, 30, 5);
                    pending = new List<Transaction> { transfer };
                    long feeBefore = chain.GetSpendableBalance(first.OwnedOneTimeAddresses, new Transaction[0]);
                    transfer.TransactionApproval = chain.CreateTransactionApproval(transfer, pending, validators.Take(1));
                    Check(chain.HasValidTransactionApproval(transfer, pending) && chain.GetTokenBalances(receiver.OwnedOneTimeAddresses, pending).Single().Amount == 0 &&
                        chain.GetTokenBalances(creator.OwnedOneTimeAddresses, pending).Single().Amount == 100 &&
                        chain.GetSpendableBalance(first.OwnedOneTimeAddresses, pending) == feeBefore,
                        "approved movement preserves confirmed ownership and waits to pay its fee");
                    Check(chain.GetSpendableTokenOutputs(receiver.OwnedOneTimeAddresses, pending, token.Token.Id).Count == 0 &&
                        chain.GetSpendableTokenOutputs(creator.OwnedOneTimeAddresses, pending, token.Token.Id).Count == 0,
                        "pending movement reserves sender inputs without releasing recipient or change outputs");
                    Reject(() => creator.CreateTokenTransferTransaction(chain, pending, token.Token.Id, destination, 20, 1),
                        "pending movement change cannot be spent twice");
                    saved = Copy(pending); restored = new Blockchain(chain.Blocks);
                    Check(restored.HasValidTransactionApproval(saved[0], saved) && restored.GetTokenBalance(receiver.OwnedOneTimeAddresses, token.Token.Id) == 0,
                        "movement approval survives restart while its balance remains unconfirmed");
                    Block movementBlock = batches.Confirm(chain, pending, validators);
                    Check(chain.IsValid() && chain.GetConfirmations(transfer.Id) == 1 && movementBlock.Transactions.Any(tx => tx.Id == transfer.Id) &&
                        chain.GetTokenBalance(receiver.OwnedOneTimeAddresses, token.Token.Id) == 30 && chain.GetTokenBalance(creator.OwnedOneTimeAddresses, token.Token.Id) == 70,
                        "movement block updates both wallets without changing total token supply");
                    Check(new Blockchain(chain.Blocks).GetTokenBalance(receiver.OwnedOneTimeAddresses, token.Token.Id) == 30,
                        "confirmed token ownership restores independently of the pending queue");
                }
            }
            Console.WriteLine(checks + " block token confirmation checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
