using PrivateCoin.Core;
using PrivateCoin.Desktop;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class PrivateCoinDesktopTokenRegression
{
    private static int checks;
    public static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine(checks + " Desktop token checks passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var issuer = new Wallet()) using (var recipient = new Wallet())
        using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain();
            LegacyConsensusFixture.Fund(chain, issuer.CreateReceiveAddress());
            string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
            LegacyConsensusFixture.Fund(chain, firstAddress);
            LegacyConsensusFixture.Fund(chain, secondAddress);
            LegacyConsensusFixture.SelfBlock(chain, first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, Blockchain.OneCoin, 0));
            LegacyConsensusFixture.SelfBlock(chain, second.CreateStakeLockTransaction(chain, new Transaction[0], secondAddress, Blockchain.OneCoin, 0));
            var validators = new[] { first.CreateValidatorStake(firstAddress, Blockchain.OneCoin), second.CreateValidatorStake(secondAddress, Blockchain.OneCoin) };
            using (var batches = new ValidationBatchFixture(chain))
            {
            var desktopChain = new Blockchain(chain.Blocks);
            var commonBlocks = chain.Blocks.ToArray();
            string destination = issuer.CreateReceiveAddress(), recipientAddress = recipient.CreateReceiveAddress();
            Transaction create = PrepareAndSign(chain, issuer, "Aurora 🌙", "AUR", 8, long.MaxValue, destination);
            chain.ValidatePendingTransactions(new[] { create });
            Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Count == 0 && chain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Count == 0,
                "a prepared or pending DEX creation is excluded from the Desktop list");

            int sourcePort = Port(), desktopPort = Port();
            using (var source = new PeerNode(sourcePort, false, Path.Combine(directory, "source-peers.json")))
            using (var desktop = new PeerNode(desktopPort, false, Path.Combine(directory, "desktop-peers.json")))
            {
                Exception synchronizationError = null;
                desktop.ChainReceived += (sender, data) => {
                    try { desktopChain.TryReplaceChain(data.Blocks); }
                    catch (Exception error) { synchronizationError = error; }
                };
                source.SynchronizationRequested += (sender, data) => source.BroadcastChainAsync(chain.Blocks);
                source.Start(new string[0]); desktop.Start(new string[0]);
                desktop.ConnectAsync("127.0.0.1", sourcePort).GetAwaiter().GetResult();
                Transaction receivedCreation = null;
                desktop.TransactionReceived += (sender, data) => { if (data.Transaction.Id == create.Id) Interlocked.Exchange(ref receivedCreation, data.Transaction); };
                create.TransactionApproval = chain.CreateTransactionApproval(create, new[] { create }, validators);
                source.BroadcastAsync(create).GetAwaiter().GetResult();
                Wait(() => receivedCreation != null, "Desktop receives the signed creation approval over P2P before its block");
                var immediate = desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses, new[] { receivedCreation }).Single();
                Check(immediate.Amount == long.MaxValue && immediate.CreationHeight == null && immediate.Confirmations == 0 && immediate.ValidationCount == 1 && desktopChain.Blocks.Count == commonBlocks.Length,
                    "Desktop lists the confirmed token and its exact supply without inventing a creation block");
                Block creationBlock = batches.Confirm(chain, new[] { create }, validators);
                source.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                Wait(() => desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Count == 1,
                    "a confirmed native DEX token appears after P2P blockchain synchronization");
                Check(synchronizationError == null && desktopChain.IsValid(), "the Desktop adopts the validated existing blockchain");
                TokenBalance owned = desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Single();
                Check(owned.Id == create.Token.Id && owned.Name == "Aurora 🌙" && owned.Symbol == "AUR" && owned.Decimals == 8 && owned.Supply == long.MaxValue && owned.Amount == long.MaxValue,
                    "the list exposes native metadata and the exact selected-wallet balance");
                Check(owned.CreationHeight == creationBlock.Height && owned.Confirmations == 1,
                    "creation height and confirmations come from the received block");
                Check(desktopChain.GetTokenBalances(recipient.OwnedOneTimeAddresses).Single().Amount == 0 && desktopChain.GetTokenBalances(null).Single().Amount == 0,
                    "public tokens remain visible with zero balance for other wallets");
                CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
                try {
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
                    Check(TokenAmount.Format(long.MaxValue, 8) == "92.233.720.368,54775807" && TokenAmount.Format(123, 0) == "123" && TokenAmount.Format(123, 2) == "1,23",
                        "Desktop quantities preserve all token decimals and Int64 precision");
                } finally { Thread.CurrentThread.CurrentCulture = previousCulture; }

                Transaction another = PrepareAndSign(chain, issuer, "Outro Aurora", "AUR", 0, 123, recipientAddress);
                batches.Confirm(chain, new[] { another }, validators);
                source.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                Wait(() => desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Count == 2, "new confirmed creations refresh the list");
                Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Select(item => item.Id).Distinct().Count() == 2 &&
                    desktopChain.GetTokenBalances(recipient.OwnedOneTimeAddresses).Single(item => item.Id == another.Token.Id).Amount == 123,
                    "tokens with the same symbol retain distinct IDs and wallet balances");

                Transaction transfer = issuer.CreateTokenTransferTransaction(chain, new Transaction[0], create.Token.Id, recipientAddress, 543210, 1);
                chain.ValidatePendingTransactions(new[] { transfer });
                Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Amount == long.MaxValue,
                    "a pending transfer does not alter the confirmed balance");
                batches.Confirm(chain, new[] { transfer }, validators);
                source.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                Wait(() => desktopChain.GetTokenBalances(recipient.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Amount == 543210,
                    "a confirmed token transfer refreshes the recipient balance");
                Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Amount == long.MaxValue - 543210 &&
                    desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Confirmations == 3,
                    "the sender balance and confirmation count follow the synchronized chain");

                var fork = new Blockchain(commonBlocks);
                for (int i = 0; i < 4; i++) LegacyConsensusFixture.Fund(fork, issuer.CreateReceiveAddress());
                chain = fork;
                source.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                Wait(() => desktopChain.Blocks.Last().Hash == fork.Blocks.Last().Hash && desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Count == 0,
                    "a valid longer fork removes token records and balances absent from the adopted chain");
            }
            }
        }
    }

    private static Transaction PrepareAndSign(Blockchain chain, Wallet wallet, string name, string symbol, int decimals, long supply, string destination)
    {
        string change = wallet.CreateReceiveAddress();
        var keys = wallet.ExportPrivateKeys().Select(xml => {
            using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(xml); return new { Address = Hash(rsa.ToXmlString(false)), Public = rsa.ToXmlString(false), Private = xml }; }
        }).ToDictionary(item => item.Address);
        var prepared = TokenCreation.Prepare(chain, new Transaction[0], keys.Values.Select(item => item.Public), name, symbol, decimals, supply, destination, change, 1);
        return prepared.Complete(prepared.InputAddresses.Select(address => {
            using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(keys[address].Private); return Convert.ToBase64String(rsa.SignData(prepared.SigningPayload, CryptoConfig.MapNameToOID("SHA256"))); }
        }).ToArray());
    }

    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static string Hash(string value) { using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(item => item.ToString("x2"))); }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static void Wait(Func<bool> ready, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.ElapsedMilliseconds < 15000) Thread.Sleep(25);
        Check(ready(), label);
    }
}
