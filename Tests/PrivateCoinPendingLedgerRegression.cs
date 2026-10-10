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
                LegacyConsensusFixture.FundBatch(senderChain, address, wallet.CreateReceiveAddress());
                receiverChain = new Blockchain(senderChain.Blocks);
                int height = senderChain.Blocks.Count;
                pending.Add(senderChain.CreateWalletCreationTransaction(wallet.CreateReceiveAddress(), pending));
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
                        "peers receive wallet creation, collateral lock and independent transfer in valid order");
                    Check(senderChain.Blocks.Count == height && receiverChain.Blocks.Count == height && Blockchain.SelectValidationBatch(received).Count == 1,
                        "propagation alone creates no block; one operation is ready for an individual block");
                    Check(receiverChain.GetActiveValidators(received).Count == 0 &&
                        receiverChain.GetSpendableBalance(wallet.OwnedOneTimeAddresses, received) == 0 &&
                        receiverChain.GetSpendableBalance(new[] { "recipient" }, received) == 0,
                        "remote pending operations reserve inputs without activating collateral or releasing transfer outputs");
                }
            }
            Console.WriteLine("3 pending ledger P2P checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int value = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return value; }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
