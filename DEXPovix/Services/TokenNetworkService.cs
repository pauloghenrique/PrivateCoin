using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Hosting;
using PrivateCoin.Core;

namespace DEXPovix.Services
{
    // A peer on the existing Core network; never issues local blocks or holds wallet keys.
    public sealed class TokenNetworkService : IDisposable
    {
        private readonly object sync = new object();
        private readonly Blockchain chain = new Blockchain();
        private readonly List<Transaction> pending = new List<Transaction>();
        private readonly PeerNode node;
        private bool synchronized;
        public TokenNetworkService()
        {
            int port;
            if (!int.TryParse(ConfigurationManager.AppSettings["ListenPort"], out port)) port = 4780;
            string cache = HostingEnvironment.MapPath("~/App_Data/peers.dat");
            if (cache != null) Directory.CreateDirectory(Path.GetDirectoryName(cache));
            node = new PeerNode(port, false, cache);
            node.ChainReceived += (sender, args) =>
            {
                lock (sync)
                {
                    try
                    {
                        bool replaced = chain.TryReplaceChain(args.Blocks);
                        if (!replaced) return;
                        synchronized = true;
                        var confirmed = new HashSet<string>(chain.Blocks.SelectMany(b => b.Transactions).Select(t => t.Id));
                        var valid = new List<Transaction>();
                        foreach (var tx in pending.Where(t => !confirmed.Contains(t.Id)))
                        {
                            try { chain.ValidatePendingTransactions(valid.Concat(new[] { tx })); valid.Add(tx); }
                            catch (InvalidOperationException) { }
                        }
                        pending.Clear(); pending.AddRange(valid);
                    }
                    catch (InvalidOperationException) { }
                }
            };
            node.TransactionReceived += (sender, args) =>
            {
                lock (sync)
                {
                    if (args.Transaction == null || pending.Any(t => t.Id == args.Transaction.Id) || pending.Count >= 1000) return;
                    try { chain.ValidatePendingTransactions(pending.Concat(new[] { args.Transaction })); pending.Add(args.Transaction); }
                    catch (InvalidOperationException) { }
                }
            };
            node.SynchronizationRequested += (sender, args) => node.BroadcastChainAsync(chain.Blocks);
            node.Start((ConfigurationManager.AppSettings["PeerSeeds"] ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        }
        public async Task SubmitAsync(Transaction tx)
        {
            lock (sync)
            {
                if (!synchronized || node.ConnectedPeers.Length == 0)
                    throw new InvalidOperationException("Aguarde a sincronização com os nós da blockchain.");
                if (chain.Blocks.SelectMany(b => b.Transactions).Any(t => t.Id == tx.Id)) return;
                if (!pending.Any(t => t.Id == tx.Id))
                {
                    if (pending.Count >= 1000) throw new InvalidOperationException("Fila cheia. Tente novamente mais tarde.");
                    chain.ValidatePendingTransactions(pending.Concat(new[] { tx }));
                    pending.Add(tx);
                }
            }
            // Repeated submissions rebroadcast the same signed transaction without spending twice.
            await node.BroadcastAsync(tx);
        }
        public object GetStatus(string id)
        {
            lock (sync)
            {
                var block = chain.Blocks.FirstOrDefault(b => b.Transactions.Any(t => t.Id == id));
                return new { TransactionId = id, State = block != null ? "Confirmado na blockchain" :
                    pending.Any(t => t.Id == id) ? "Pendente de confirmação" : "Não encontrado na cadeia ou fila deste nó",
                    BlockHeight = block == null ? (int?)null : block.Height,
                    ConnectedPeers = node.ConnectedPeers.Length };
            }
        }
        public void Dispose() { node.Dispose(); }
    }
}
