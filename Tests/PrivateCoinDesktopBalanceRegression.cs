using PrivateCoin.Core;
using PrivateCoin.Desktop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

internal static class PrivateCoinDesktopBalanceRegression
{
    private static int checks;

    public static int Main()
    {
        try
        {
            using (var sender = new Wallet()) using (var recipient = new Wallet())
            using (var first = new Wallet()) using (var second = new Wallet())
            {
                var chain = new Blockchain();
                var pending = new List<Transaction>();
                string senderAddress = sender.CreateReceiveAddress(), recipientAddress = recipient.CreateReceiveAddress();
                string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
                foreach (string address in new[] { senderAddress, firstAddress, secondAddress })
                    pending.Add(chain.CreateWalletCreationTransaction(address, pending));
                pending.Add(first.CreateStakeLockTransaction(chain, pending, firstAddress, Blockchain.WalletCreationReward, 0));
                pending.Add(second.CreateStakeLockTransaction(chain, pending, secondAddress, Blockchain.WalletCreationReward, 0));
                pending.Add(sender.CreateStakeLockTransaction(chain, pending, senderAddress, Blockchain.OneCoin, 0));
                var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.WalletCreationReward), second.CreateValidatorStake(secondAddress, Blockchain.WalletCreationReward) };
                long initial = Blockchain.WalletCreationReward - Blockchain.OneCoin;
                Balance(chain, sender, pending, initial, initial, "locked collateral is excluded from the displayed balance");
                Balance(chain, null, pending, 0, 0, "no selected wallet displays zero");

                Transaction payment = sender.CreateTransaction(chain, pending, recipientAddress, Blockchain.OneCoin, 11);
                var sending = pending.Concat(new[] { payment }).ToList();
                long remaining = initial - Blockchain.OneCoin - 11;
                Balance(chain, sender, sending, remaining, 0, "the sender's remaining POVIX stays visible before approval or a block");
                Balance(chain, recipient, sending, 0, 0, "unconfirmed incoming POVIX is not added to the recipient's balance");
                Check(chain.Blocks.Count == 1, "refreshing the display does not create a block");
                Reject(() => sender.CreateTransaction(chain, sending, recipientAddress, 1, 1),
                    "displayed pending change cannot be spent twice");
                Balance(chain, sender, pending, initial, initial, "removing a pending transfer restores the original balance");

                using (var restored = Wallet.FromPrivateKeys(sender.ExportPrivateKeys()))
                    Balance(new Blockchain(chain.Blocks), restored, Copy(sending), remaining, 0,
                        "restoring the wallet and pending queue preserves the displayed change");
                Transaction selfPayment = sender.CreateTransaction(chain, pending, senderAddress, Blockchain.OneCoin, 11);
                Balance(chain, sender, pending.Concat(new[] { selfPayment }), initial - 11, 0,
                    "a transfer to the same wallet deducts only its fee");
                Transaction fullPayment = sender.CreateTransaction(chain, pending, recipientAddress, initial - 11, 11);
                Balance(chain, sender, pending.Concat(new[] { fullPayment }), 0, 0,
                    "sending the full available balance displays zero without inventing change");

                var multipleOutputs = pending.ToList();
                multipleOutputs.Add(chain.CreateWalletCreationTransaction(sender.CreateReceiveAddress(), multipleOutputs));
                Transaction partial = sender.CreateTransaction(chain, multipleOutputs, recipientAddress, Blockchain.OneCoin, 11);
                multipleOutputs.Add(partial);
                Balance(chain, sender, multipleOutputs, initial + Blockchain.WalletCreationReward - Blockchain.OneCoin - 11,
                    Blockchain.WalletCreationReward, "unspent funds and reserved change are combined exactly once");

                Transaction creation = sender.CreateTokenTransaction(chain, pending, "Saldo", "SLD", 0, 100, senderAddress, 7);
                var creating = pending.Concat(new[] { creation }).ToList();
                Balance(chain, sender, creating, initial - 7, 0,
                    "token creation keeps POVIX change visible without counting the token supply as POVIX");
                creation.TransactionApproval = chain.CreateTransactionApproval(creation, creating, validators.Take(1));
                Balance(chain, sender, creating, initial - 7, initial - 7,
                    "immediately available token creation change is not counted again");
                Transaction movement = sender.CreateTokenTransferTransaction(chain, creating, creation.Token.Id, recipientAddress, 30, 5);
                creating.Add(movement);
                Balance(chain, sender, creating, initial - 12, 0,
                    "a token transfer also preserves the sender's remaining POVIX for display");
                movement.TransactionApproval = chain.CreateTransactionApproval(movement, creating, validators.Take(1));
                Balance(chain, sender, creating, initial - 12, initial - 12,
                    "approved token transfer change is not duplicated in the native balance");

                payment.TransactionApproval = chain.CreateTransactionApproval(payment, sending, validators.Take(1));
                Balance(chain, sender, sending, remaining, 0, "native transfer approval preserves the displayed remainder");
                Balance(chain, recipient, sending, 0, 0, "native transfer approval keeps recipient funds pending until the block");
                Balance(chain, sender, sending, remaining, 0, "repeated refreshes do not accumulate change");
                Balance(chain, first, sending, 11, 11,
                    "validator fees remain immediately available in the display");
                Transaction feePayment = first.CreateTransaction(chain, sending, recipientAddress, 1, 1);
                Balance(chain, first, sending.Concat(new[] { feePayment }), 9, 0,
                    "spending a pending validator fee preserves its remaining change for display");
                while (sending.Count < Blockchain.ValidationsPerBlock)
                    sending.Add(chain.CreateWalletCreationTransaction("balance-fixture-" + sending.Count, sending));
                chain.AddProofOfStakeBlock(chain.SelectApprovedValidationBatch(sending), validators);
                Balance(chain, sender, new Transaction[0], remaining, remaining,
                    "block confirmation releases the same remaining balance without a visual jump");
                Balance(chain, recipient, new Transaction[0], Blockchain.OneCoin, Blockchain.OneCoin,
                    "recipient POVIX becomes visible after confirmation");
                Check(chain.IsValid(), "display queries preserve the valid blockchain");
            }
            Console.WriteLine(checks + " Desktop balance checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Balance(Blockchain chain, Wallet wallet, IEnumerable<Transaction> pending, long total, long available, string label)
    {
        WalletBalanceSummary value = WalletBalanceSummary.Read(chain, wallet, pending);
        Check(value.Total == total && value.Available == available && value.PendingChange == total - available, label);
    }

    private static List<Transaction> Copy(List<Transaction> pending)
    {
        var serializer = new DataContractSerializer(typeof(List<Transaction>));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, pending);
            stream.Position = 0;
            return (List<Transaction>)serializer.ReadObject(stream);
        }
    }

    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception(label);
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS " + label);
    }
}
