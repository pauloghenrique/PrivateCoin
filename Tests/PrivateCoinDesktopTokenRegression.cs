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
            Check(TokenWalletOperations.GetBalances(desktopChain, issuer, new[] { create }).Count == 0,
                "unapproved creations stay outside the Desktop wallet list");

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
                Wait(() => source.ConnectedPeerCount == 1 && desktop.ConnectedPeerCount == 1, "both Desktop peers complete the consensus handshake");
                Transaction receivedCreation = null;
                desktop.TransactionReceived += (sender, data) => { if (data.Transaction.Id == create.Id) Interlocked.Exchange(ref receivedCreation, data.Transaction); };
                create.TransactionApproval = chain.CreateTransactionApproval(create, new[] { create }, validators);
                source.BroadcastAsync(create).GetAwaiter().GetResult();
                Wait(() => receivedCreation != null, "Desktop receives the signed creation approval over P2P before its block");
                Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses, new[] { receivedCreation }).Count == 0 &&
                    TokenWalletOperations.GetBalances(desktopChain, issuer, new[] { receivedCreation }).Count == 0 && desktopChain.Blocks.Count == commonBlocks.Length,
                    "approved creation stays outside the Desktop registry until its block");
                string validProof = receivedCreation.TransactionApproval.Proof.Signature;
                try
                {
                    receivedCreation.TransactionApproval.Proof.Signature = Convert.ToBase64String(new byte[256]);
                    Check(TokenWalletOperations.GetBalances(desktopChain, issuer, new[] { receivedCreation }).Count == 0,
                        "the Desktop does not expose token supply from a forged approval");
                }
                finally { receivedCreation.TransactionApproval.Proof.Signature = validProof; }
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, issuer, new[] { receivedCreation }, create.Token.Id, recipientAddress, 1, 1),
                    "the Desktop cannot spend tokens before their creation block");
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
                Check(TokenWalletOperations.GetBalances(desktopChain, issuer).Single().Id == create.Token.Id &&
                    TokenWalletOperations.GetBalances(desktopChain, recipient).Count == 0 && TokenWalletOperations.GetBalances(desktopChain, null).Count == 0,
                    "the Desktop defaults to positive balances belonging to the selected wallet");
                Check(TokenWalletOperations.GetBalances(desktopChain, recipient, false).Single().Amount == 0,
                    "the optional public registry retains zero balances without granting ownership");
                CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
                try {
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
                    Check(TokenAmount.Format(long.MaxValue, 8) == "92.233.720.368,54775807" && TokenAmount.Format(123, 0) == "123" && TokenAmount.Format(123, 2) == "1,23",
                        "Desktop quantities preserve all token decimals and Int64 precision");
                    Check(TokenAmount.Parse("92.233.720.368,54775807", 8) == long.MaxValue && TokenAmount.Parse("1,23", 2) == 123 &&
                        TokenAmount.Parse("0,00000001", 8) == 1 && TokenAmount.Parse("123", 0) == 123,
                        "Desktop input converts local decimal quantities to exact atomic token units");
                    foreach (string invalid in new[] { "", "0", "-1", "abc", "1,231", "1,0000000000000000000000000000001", "9223372036854775808", "79228162514264337593543950335" })
                        Reject(() => TokenAmount.Parse(invalid, 2), "invalid or inexact token quantity is rejected: " + invalid);
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                    Check(TokenAmount.Parse("92,233,720,368.54775807", 8) == long.MaxValue && TokenAmount.Parse("1.23", 2) == 123,
                        "quantity input also respects a culture with a decimal point");
                } finally { Thread.CurrentThread.CurrentCulture = previousCulture; }

                Transaction another = PrepareAndSign(chain, issuer, "Outro Aurora", "AUR", 0, 123, recipientAddress);
                batches.Confirm(chain, new[] { another }, validators);
                source.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                Wait(() => desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Count == 2, "new confirmed creations refresh the list");
                Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Select(item => item.Id).Distinct().Count() == 2 &&
                    desktopChain.GetTokenBalances(recipient.OwnedOneTimeAddresses).Single(item => item.Id == another.Token.Id).Amount == 123,
                    "tokens with the same symbol retain distinct IDs and wallet balances");

                Check(TokenWalletOperations.GetBalances(desktopChain, issuer).Single().Id == create.Token.Id &&
                    TokenWalletOperations.GetBalances(desktopChain, recipient).Single().Id == another.Token.Id,
                    "wallet switching isolates positive balances even when token symbols match");
                int keyCount = issuer.OwnedOneTimeAddresses.Count;
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, recipient, new Transaction[0], create.Token.Id, destination, 1, 1),
                    "a different wallet cannot move a token it does not own");
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, issuer, new Transaction[0], create.Token.Id, "invalid", 1, 1),
                    "an invalid destination is rejected before signing");
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, issuer, new Transaction[0], create.Token.Id, recipientAddress, 0, 1),
                    "zero token amounts are rejected before signing");
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, issuer, new Transaction[0], create.Token.Id, recipientAddress, 1, Blockchain.MaximumTransferFee + 1),
                    "a fee outside the allowed range is rejected before signing");
                Check(issuer.OwnedOneTimeAddresses.Count == keyCount, "rejected Desktop transfers do not generate change keys");
                long povixBefore = desktopChain.GetBalance(issuer.OwnedOneTimeAddresses);
                Transaction transfer = TokenWalletOperations.CreateTransfer(desktopChain, issuer, new Transaction[0], create.Token.Id, recipientAddress, 543210, 1);
                chain.ValidatePendingTransactions(new[] { transfer });
                Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Amount == long.MaxValue,
                    "a pending transfer does not alter the confirmed balance");
                Check(TokenWalletOperations.GetAvailableBalance(desktopChain, issuer, new[] { transfer }, create.Token.Id) == 0 &&
                    TokenWalletOperations.GetBalances(desktopChain, issuer).Single().Amount == long.MaxValue,
                    "block ownership stays unchanged while unapproved pending inputs are reserved for sending");
                Check(TokenWalletOperations.GetBalances(desktopChain, issuer, new[] { transfer }).Count == 0,
                    "the default wallet list excludes tokens with no available balance while their outputs are reserved");
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, issuer, new[] { transfer }, create.Token.Id, recipientAddress, 1, 1),
                    "the same token output cannot be sent twice while pending");
                bool transactionReceived = false;
                source.TransactionReceived += (sender, data) => { if (data.Transaction.Id == transfer.Id) transactionReceived = true; };
                desktop.BroadcastAsync(transfer).GetAwaiter().GetResult();
                Wait(() => transactionReceived, "the signed Desktop token transfer propagates through the real P2P connection");
                Transaction receivedMovement = null;
                desktop.TransactionReceived += (sender, data) => { if (data.Transaction.Id == transfer.Id) Interlocked.Exchange(ref receivedMovement, data.Transaction); };
                transfer.TransactionApproval = chain.CreateTransactionApproval(transfer, new[] { transfer }, validators);
                int blocksBeforeMovement = desktopChain.Blocks.Count;
                source.BroadcastAsync(transfer).GetAwaiter().GetResult();
                Wait(() => receivedMovement != null, "Desktop receives token movement approval before the block");
                Check(desktopChain.GetTokenBalances(recipient.OwnedOneTimeAddresses, new[] { receivedMovement }).Single(item => item.Id == create.Token.Id).Amount == 0 &&
                    desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses, new[] { receivedMovement }).Single(item => item.Id == create.Token.Id).Amount == long.MaxValue &&
                    desktopChain.Blocks.Count == blocksBeforeMovement,
                    "approved movements preserve block-confirmed recipient and sender balances");
                Check(TokenWalletOperations.GetAvailableBalance(desktopChain, issuer, new[] { receivedMovement }, create.Token.Id) == 0 &&
                    TokenWalletOperations.GetBalances(desktopChain, recipient, new[] { receivedMovement }).All(token => token.Id != create.Token.Id),
                    "the Desktop reserves pending token inputs without releasing receiver or change outputs");
                batches.Confirm(chain, new[] { transfer }, validators);
                source.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                Wait(() => desktopChain.GetTokenBalances(recipient.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Amount == 543210,
                    "a confirmed token transfer refreshes the recipient balance");
                Check(desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Amount == long.MaxValue - 543210 &&
                    desktopChain.GetTokenBalances(issuer.OwnedOneTimeAddresses).Single(item => item.Id == create.Token.Id).Confirmations == 3,
                    "the sender balance and confirmation count follow the synchronized chain");
                Check(desktopChain.GetBalance(issuer.OwnedOneTimeAddresses) == povixBefore - transfer.Fee &&
                    transfer.Outputs.Where(output => output.AssetId == create.Token.Id).Sum(output => output.Amount) == long.MaxValue &&
                    transfer.Outputs.Skip(1).All(output => issuer.OwnedOneTimeAddresses.Contains(output.OneTimeAddress)),
                    "token value is conserved, both changes stay in the sender wallet and only the fee consumes POVIX");
                using (var restored = Wallet.FromPrivateKeys(issuer.ExportPrivateKeys()))
                    Check(TokenWalletOperations.GetBalances(new Blockchain(desktopChain.Blocks), restored).Single().Amount == long.MaxValue - 543210,
                        "restoring the wallet keys and chain preserves ownership of token change");
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, recipient, new Transaction[0], create.Token.Id, destination, 543211, 1),
                    "a quantity above the selected wallet balance is rejected");
                Reject(() => TokenWalletOperations.CreateTransfer(desktopChain, recipient, new Transaction[0], create.Token.Id, destination, 543210, 1),
                    "a token holder without POVIX cannot pay the transfer fee");
                LegacyConsensusFixture.Fund(chain, recipientAddress);
                Transaction returnAll = TokenWalletOperations.CreateTransfer(chain, recipient, new Transaction[0], create.Token.Id, destination, 543210, 1);
                Check(TokenWalletOperations.GetAvailableBalance(chain, recipient, new[] { returnAll }, create.Token.Id) == 0,
                    "sending the entire token balance reserves it immediately");
                batches.Confirm(chain, new[] { returnAll }, validators);
                source.BroadcastChainAsync(chain.Blocks).GetAwaiter().GetResult();
                Wait(() => !TokenWalletOperations.GetBalances(desktopChain, recipient).Any(token => token.Id == create.Token.Id),
                    "a token leaves the owning wallet list after its entire balance is confirmed as spent");
                Check(TokenWalletOperations.GetBalances(desktopChain, issuer).Single().Amount == long.MaxValue &&
                    TokenWalletOperations.GetBalances(desktopChain, recipient).Single().Id == another.Token.Id,
                    "a recipient can move its own tokens independently of the creator and other assets remain isolated");

                var fork = new Blockchain(commonBlocks);
                for (int i = 0; i < 6; i++) LegacyConsensusFixture.Fund(fork, issuer.CreateReceiveAddress());
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
    private static void Reject(Action operation, string label)
    {
        try { operation(); }
        catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void Wait(Func<bool> ready, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.ElapsedMilliseconds < 15000) Thread.Sleep(25);
        Check(ready(), label);
    }
}
