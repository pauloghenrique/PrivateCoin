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
        private readonly Blockchain blockchain = new Blockchain();
        private readonly List<Transaction> pending = new List<Transaction>();
        private readonly HashSet<string> pendingIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly PeerNode node;

        public PublicNetworkService()
        {
            int port;
            if (!int.TryParse(ConfigurationManager.AppSettings["ListenPort"], out port)) port = 4779;
            string cachePath = HostingEnvironment.MapPath("~/App_Data/peers.dat");
            if (!string.IsNullOrEmpty(cachePath)) Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
            node = new PeerNode(port, false, cachePath);
            node.ChainReceived += ReceiveChain;
            node.TransactionReceived += ReceiveTransaction;
            node.SynchronizationRequested += (sender, args) => node.BroadcastChainAsync(blockchain.Blocks);
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

        private void ReceiveChain(object sender, ChainReceivedEventArgs args)
        {
            lock (sync)
            {
                try
                {
                    if (!blockchain.TryReplaceChain(args.Blocks)) return;
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
                catch (InvalidOperationException) { }
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
