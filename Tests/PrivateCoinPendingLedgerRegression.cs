using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PrivateCoin.Core;

internal static class PrivateCoinPendingLedgerRegression
{
    public static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(args[0]);
            var senderChain = new Blockchain(); var receiverChain = new Blockchain();
            var received = new List<Transaction>(); var gate = new object(); Exception failure = null;
            int senderPort = Port(), receiverPort = Port();
            using (var wallet = new Wallet())
            using (var sender = new PeerNode(senderPort, false, Path.Combine(args[0], "sender.json")))
            using (var receiver = new PeerNode(receiverPort, false, Path.Combine(args[0], "receiver.json")))
            {
                string address = wallet.CreateReceiveAddress(); var pending = new List<Transaction>();
                pending.Add(senderChain.CreateWalletCreationTransaction(address, pending));
                pending.Add(wallet.CreateStakeLockTransaction(senderChain, pending, address, Blockchain.OneCoin, 0));
                pending.Add(wallet.CreateTransaction(senderChain, pending, "recipient", Blockchain.OneCoin, 11));
                receiver.TransactionReceived += (source, message) =>
                {
                    try
                    {
                        lock (gate)
                        {
                            if (received.Any(tx => tx.Id == message.Transaction.Id)) return;
                            receiverChain.ValidatePendingTransactions(received.Concat(new[] { message.Transaction }));
                            received.Add(message.Transaction);
                        }
                    }
                    catch (Exception error) { failure = error; }
                };
                sender.Start(new string[0]); receiver.Start(new string[0]);
                receiver.ConnectAsync("127.0.0.1", senderPort).GetAwaiter().GetResult();
                foreach (Transaction transaction in Blockchain.OrderByFeePriority(pending)) sender.BroadcastAsync(transaction).GetAwaiter().GetResult();
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline) { lock (gate) { if (received.Count == 3 || failure != null) break; } Thread.Sleep(50); }
                if (failure != null) throw failure;
                lock (gate)
                {
                    Check(received.Count == 3 && received.Select(tx => tx.Id).SequenceEqual(Blockchain.OrderByFeePriority(pending).Select(tx => tx.Id)),
                        "peers receive wallet creation, collateral lock and dependent transfer in valid order");
                    Check(senderChain.Blocks.Count == 1 && receiverChain.Blocks.Count == 1 && Blockchain.SelectValidationBatch(received).Count == 0,
                        "propagating three validations creates no block on either node");
                    Check(receiverChain.GetActiveValidators(received).Single().LockedAmount == Blockchain.OneCoin &&
                        receiverChain.GetSpendableBalance(wallet.OwnedOneTimeAddresses, received) == 0 &&
                        receiverChain.GetSpendableBalance(new[] { "recipient" }, received) == 0,
                        "the remote pending ledger locks collateral and reserves the spend without releasing transfer outputs");
                }
            }
            Console.WriteLine("3 pending ledger P2P checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int value = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return value; }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
