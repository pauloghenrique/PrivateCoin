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
                LegacyConsensusFixture.FundBatch(chain, senderAddress, firstAddress, secondAddress);
                LegacyConsensusFixture.SelfBlock(chain, new[] {
                    first.CreateStakeLockTransaction(chain, pending, firstAddress, Blockchain.WalletCreationReward, 0),
                    second.CreateStakeLockTransaction(chain, pending, secondAddress, Blockchain.WalletCreationReward, 0),
                    sender.CreateStakeLockTransaction(chain, pending, senderAddress, Blockchain.OneCoin, 0) });
                int height = chain.Blocks.Count;
                var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.WalletCreationReward), second.CreateValidatorStake(secondAddress, Blockchain.WalletCreationReward) };
                long initial = Blockchain.WalletCreationReward - Blockchain.OneCoin;
                Balance(chain, sender, pending, initial, initial, "locked collateral is excluded from the displayed balance");
                Balance(chain, null, pending, 0, 0, "no selected wallet displays zero");

                Transaction payment = sender.CreateTransaction(chain, pending, recipientAddress, Blockchain.OneCoin, 11);
                var sending = pending.Concat(new[] { payment }).ToList();
                long remaining = initial - Blockchain.OneCoin - 11;
                Balance(chain, sender, sending, remaining, 0, "the sender's remaining POVIX stays visible before approval or a block");
                Balance(chain, recipient, sending, 0, 0, "unconfirmed incoming POVIX is not added to the recipient's balance");
                Check(chain.Blocks.Count == height, "refreshing the display does not create a block");
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

                var multiChain = new Blockchain(chain.Blocks);
                LegacyConsensusFixture.FundBatch(multiChain, sender.CreateReceiveAddress());
                Transaction partial = sender.CreateTransaction(multiChain, pending, recipientAddress, Blockchain.OneCoin, 11);
                Balance(multiChain, sender, new[] { partial }, initial + Blockchain.WalletCreationReward - Blockchain.OneCoin - 11,
                    Blockchain.WalletCreationReward, "unspent funds and reserved change are combined exactly once");

                Transaction creation = sender.CreateTokenTransaction(chain, pending, "Saldo", "SLD", 0, 100, senderAddress, 7);
                var creating = pending.Concat(new[] { creation }).ToList();
                Balance(chain, sender, creating, initial - 7, 0,
                    "token creation keeps POVIX change visible without counting the token supply as POVIX");
                creation.TransactionApproval = chain.CreateTransactionApproval(creation, creating, validators.Take(1));
                Balance(chain, sender, creating, initial - 7, 0,
                    "approved token creation change remains pending and is not counted twice");
                Reject(() => sender.CreateTokenTransferTransaction(chain, creating, creation.Token.Id, recipientAddress, 30, 5),
                    "the balance display does not release unconfirmed created tokens for sending");
                var tokenChain = new Blockchain(chain.Blocks);
                using (var batches = new ValidationBatchFixture(tokenChain))
                {
                    batches.Confirm(tokenChain, creating, validators);
                    Transaction movement = sender.CreateTokenTransferTransaction(tokenChain, new Transaction[0], creation.Token.Id, recipientAddress, 30, 5);
                    var moving = new[] { movement };
                    Balance(tokenChain, sender, moving, initial - 12, 0, "token movement preserves native change for display while its inputs are reserved");
                    movement.TransactionApproval = tokenChain.CreateTransactionApproval(movement, moving, validators.Take(1));
                    Balance(tokenChain, sender, moving, initial - 12, 0, "approved token movement change remains pending until its block");
                }

                payment.TransactionApproval = chain.CreateTransactionApproval(payment, sending, validators.Take(1));
                Balance(chain, sender, sending, remaining, 0, "native transfer approval preserves the displayed remainder");
                Balance(chain, recipient, sending, 0, 0, "native transfer approval keeps recipient funds pending until the block");
                Balance(chain, sender, sending, remaining, 0, "repeated refreshes do not accumulate change");
                Balance(chain, first, sending, 0, 0, "validator fees stay pending until block confirmation");
                Reject(() => first.CreateTransaction(chain, sending, recipientAddress, 1, 1), "pending validator fees cannot fund a payment");
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
