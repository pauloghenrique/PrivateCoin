using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using PrivateCoin.Core;
using PrivateCoin.Site.Models;

namespace PrivateCoin.Site.Services
{
    /// <summary>Maintains the explorer's read-only view of the public peer network.</summary>
    public sealed class PublicNetworkService : IDisposable
    {
        private const int LedgerPageSize = 100;
        private readonly object sync = new object();
        private Blockchain blockchain;
        private readonly string statePath;
        private readonly List<Transaction> pending = new List<Transaction>();
        private readonly HashSet<string> pendingIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly PeerNode node;

        public PublicNetworkService()
        {
            int port;
            if (!int.TryParse(ConfigurationManager.AppSettings["ListenPort"], out port)) port = 4779;
            string cachePath = HostingEnvironment.MapPath("~/App_Data/peers.dat");
            if (!string.IsNullOrEmpty(cachePath)) Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
            string directory = Path.GetDirectoryName(cachePath) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data");
            statePath = Path.Combine(directory, "explorer-network.json");
            string nodeId = NodeIdentity.LoadOrCreate(Path.Combine(directory, "node-id.dat"));
            blockchain = File.Exists(statePath) ? BlockchainStateStore.Load(statePath, nodeId) : new Blockchain(nodeId);
            node = new PeerNode(port, false, cachePath);
            node.ChainReceived += ReceiveChain;
            node.FinalityVoteReceived += ReceiveFinalityVote;
            node.TransactionReceived += ReceiveTransaction;
            node.SynchronizationRequested += (sender, args) => node.BroadcastChainAsync(blockchain);
            string[] seeds = (ConfigurationManager.AppSettings["PeerSeeds"] ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
            node.Start(seeds);
        }

        public HomeViewModel GetDashboard()
        {
            lock (sync)
            {
                var query = new BlockchainQueryApi(blockchain);
                return new HomeViewModel
                {
                    Summary = query.GetSummary(),
                    Ledger = query.GetLedger(0, LedgerPageSize),
                    ValidationQueue = Blockchain.OrderByFeePriority(pending).ToArray(),
                    ConnectedPeers = node.ConnectedPeers,
                    KnownPeerCount = node.KnownPeers.Length,
                    UpdatedAtUtc = DateTime.UtcNow
                };
            }
        }

        private static string HashPublicKey(string publicKey)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(publicKey ?? string.Empty))
                    .Select(value => value.ToString("x2")));
        }

        // Read-only treasury queries, protected by SwapNetworkController's server token.
        public object GetSwapBalance(string[] addresses, int confirmations)
        {
            lock (sync)
            {
                var blocks = blockchain.Blocks.ToArray();
                int tip = blocks.Last().Height;
                var heights = blocks.SelectMany(block => block.Transactions.Select(transaction =>
                    new { transaction.Id, block.Height })).ToDictionary(item => item.Id, item => item.Height);
                long amount = blockchain.GetSpendableOutputs(addresses, pending)
                    .Where(output => tip - heights[output.TransactionId] + 1 >= confirmations)
                    .Aggregate(0L, (total, output) => checked(total + output.Output.Amount));
                return new
                {
                    network_id = Blockchain.NetworkId, genesis_hash = Blockchain.GenesisHash,
                    connected_peers = node.ConnectedPeers.Length, height = tip,
                    amount_atomic = amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
            }
        }

        public object GetSwapTransaction(string id)
        {
            lock (sync)
            {
                var blocks = blockchain.Blocks.ToArray();
                int tip = blocks.Last().Height;
                foreach (Block block in blocks)
                {
                    Transaction transaction = block.Transactions.FirstOrDefault(item => item.Id == id);
                    if (transaction == null) continue;
                    return new
                    {
                        network_id = Blockchain.NetworkId, genesis_hash = Blockchain.GenesisHash,
                        connected_peers = node.ConnectedPeers.Length, height = block.Height,
                        block_hash = block.Hash, confirmations = tip - block.Height + 1,
                        timestamp = (long)(new DateTime(block.TimestampUtcTicks, DateTimeKind.Utc) -
                            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
                        kind = transaction.Kind == TransactionKind.Transfer && transaction.Inputs.Count > 0
                            ? "transfer" : "issuance_or_stake",
                        input_addresses = transaction.Inputs.Select(input => HashPublicKey(input.PublicKey)).ToArray(),
                        outputs = transaction.Outputs.Select(output => new
                        {
                            address = output.OneTimeAddress,
                            amount_atomic = output.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }).ToArray()
                    };
                }
                return new { error = "Transação não encontrada na cadeia validada." };
            }
        }

        private void ReceiveChain(object sender, ChainReceivedEventArgs args)
        {
            lock (sync)
            {
                try
                {
                    var candidate = new Blockchain(blockchain.Blocks, blockchain.LocalNodeId, blockchain.GetFinalityState());
                    if (!candidate.TryReplaceChain(args.Blocks, args.Finality)) return;
                    BlockchainStateStore.Save(statePath, candidate);
                    blockchain = candidate;
                    var valid = new List<Transaction>();
                    foreach (Transaction transaction in Blockchain.OrderByFeePriority(pending))
                    {
                        try
                        {
                            blockchain.ValidatePendingTransactions(valid.Concat(new[] { transaction }));
                            valid.Add(transaction);
                        }
                        catch (InvalidOperationException) { pendingIds.Remove(transaction.Id); }
                    }
                    pending.Clear();
                    pending.AddRange(valid);
                }
                catch (Exception error) when (error is InvalidOperationException || error is IOException || error is UnauthorizedAccessException) { }
            }
        }

        private void ReceiveFinalityVote(object sender, FinalityVoteReceivedEventArgs args)
        {
            lock (sync)
            {
                try
                {
                    var candidate = new Blockchain(blockchain.Blocks, blockchain.LocalNodeId, blockchain.GetFinalityState());
                    candidate.AddFinalityVote(args.Vote);
                    BlockchainStateStore.Save(statePath, candidate);
                    blockchain = candidate;
                }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is System.Security.Cryptography.CryptographicException || error is IOException || error is UnauthorizedAccessException) { }
            }
        }

        private void ReceiveTransaction(object sender, TransactionReceivedEventArgs args)
        {
            lock (sync)
            {
                if (args.Transaction == null || string.IsNullOrEmpty(args.Transaction.Id) || pendingIds.Contains(args.Transaction.Id)) return;
                try
                {
                    blockchain.ValidatePendingTransactions(pending.Concat(new[] { args.Transaction }));
                    pending.Add(args.Transaction);
                    pendingIds.Add(args.Transaction.Id);
                }
                catch (InvalidOperationException) { }
            }
        }

        public void Dispose() { node.Dispose(); }
    }
}
