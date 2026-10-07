using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DEXPovix.Models;
using PrivateCoin.Core;

namespace DEXPovix.Services
{
    [DataContract]
    internal sealed class NetworkSnapshot
    {
        [DataMember] public string NetworkId { get; set; }
        [DataMember] public Block[] Blocks { get; set; }
        [DataMember] public Transaction[] Pending { get; set; }
    }

    public sealed class PreparedToken
    {
        public string DraftId { get; set; }
        public string Payload { get; set; }
        public string[] InputAddresses { get; set; }
        public string TokenId { get; set; }
        public string Fee { get; set; }
        public string Supply { get; set; }
    }

    /// <summary>A public P2P node; it never holds wallet secrets or signs transactions.</summary>
    public sealed class TokenNetworkService : IDisposable
    {
        private sealed class Draft
        {
            internal TokenCreationDraft Creation;
            internal DateTime Expires;
            internal Transaction Submitted;
        }

        private readonly object sync = new object();
        private readonly Blockchain chain;
        private readonly List<Transaction> pending = new List<Transaction>();
        private readonly Dictionary<string, Draft> drafts = new Dictionary<string, Draft>();
        private readonly string snapshotPath;
        private readonly PeerNode node;

        public TokenNetworkService(int port, string[] seeds, string snapshotPath)
        {
            this.snapshotPath = snapshotPath;
            chain = new Blockchain();
            if (!string.IsNullOrEmpty(snapshotPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath));
                if (File.Exists(snapshotPath))
                {
                    using (var stream = File.OpenRead(snapshotPath))
                    {
                        var saved = (NetworkSnapshot)new DataContractJsonSerializer(typeof(NetworkSnapshot)).ReadObject(stream);
                        if (saved.NetworkId != Blockchain.NetworkId) throw new InvalidOperationException("A cache pertence a outra rede.");
                        chain = new Blockchain(saved.Blocks);
                        RevalidatePending(saved.Pending ?? new Transaction[0]);
                    }
                }
            }
            node = new PeerNode(port, false, string.IsNullOrEmpty(snapshotPath) ? null :
                Path.Combine(Path.GetDirectoryName(snapshotPath), "peers.dat"));
            node.ChainReceived += (sender, args) => ReceiveChain(args.Blocks);
            node.TransactionReceived += (sender, args) => ReceiveTransaction(args.Transaction);
            node.SynchronizationRequested += (sender, args) => node.BroadcastChainAsync(chain.Blocks);
            node.Start(seeds ?? new string[0]);
        }

        public TokenDashboard GetDashboard()
        {
            lock (sync)
            {
                var confirmed = chain.Blocks.SelectMany(block => block.Transactions
                    .Where(tx => tx.Kind == TransactionKind.TokenCreate).Select(tx => Row(tx, block.Height)));
                return new TokenDashboard {
                    Tokens = confirmed.Concat(pending.Where(tx => tx.Kind == TransactionKind.TokenCreate)
                        .Select(tx => Row(tx, null))).Reverse().ToArray(),
                    ConnectedPeers = node.ConnectedPeerCount, Height = chain.Blocks.Last().Height,
                    NetworkId = Blockchain.NetworkId, Fee = TokenAmounts.Format(Blockchain.CalculateAutomaticFee(pending.Count, 1), 8)
                };
            }
        }

        private static TokenRow Row(Transaction tx, int? height) => new TokenRow {
            Id = tx.Token.Id, Name = tx.Token.Name, Symbol = tx.Token.Symbol, Decimals = tx.Token.Decimals,
            Supply = TokenAmounts.Format(tx.Token.Supply, tx.Token.Decimals), TransactionId = tx.Id,
            Height = height, Status = height.HasValue ? "Confirmado" : "Pendente"
        };

        public PreparedToken Prepare(PrepareTokenRequest request)
        {
            if (request == null || request.PublicKeys == null || request.PublicKeys.Length == 0 || request.PublicKeys.Length > 100)
                throw new ArgumentException("Conecte uma carteira com até 100 endereços.");
            long supply = TokenAmounts.Parse(request.Supply, request.Decimals);
            lock (sync)
            {
                RequirePeers();
                foreach (string key in drafts.Where(item => item.Value.Expires < DateTime.UtcNow).Select(item => item.Key).ToArray())
                    drafts.Remove(key);
                if (drafts.Count >= 500) throw new InvalidOperationException("Muitas solicitações pendentes. Tente novamente em alguns minutos.");
                var creation = TokenCreationDraft.Prepare(chain, pending, request.PublicKeys,
                    (request.Name ?? "").Trim(), (request.Symbol ?? "").Trim().ToUpperInvariant(), request.Decimals,
                    supply, request.Recipient, request.ChangeAddress, Blockchain.CalculateAutomaticFee(pending.Count, 1));
                string id = Guid.NewGuid().ToString("N");
                drafts.Add(id, new Draft { Creation = creation, Expires = DateTime.UtcNow.AddMinutes(5) });
                return new PreparedToken { DraftId = id, Payload = creation.SigningPayload,
                    InputAddresses = creation.InputAddresses, TokenId = creation.TokenId,
                    Fee = TokenAmounts.Format(creation.Fee, 8), Supply = TokenAmounts.Format(supply, request.Decimals) };
            }
        }

        public async Task<string> SubmitAsync(SubmitTokenRequest request)
        {
            Transaction transaction;
            lock (sync)
            {
                Draft draft;
                if (request == null || string.IsNullOrEmpty(request.DraftId) ||
                    !drafts.TryGetValue(request.DraftId, out draft) || draft.Expires < DateTime.UtcNow)
                    throw new ArgumentException("A preparação expirou. Prepare o token novamente.");
                RequirePeers();
                if (draft.Submitted != null) transaction = draft.Submitted;
                else
                {
                    transaction = draft.Creation.Complete(request.Signatures);
                    chain.ValidatePendingTransactions(pending.Concat(new[] { transaction }));
                    pending.Add(transaction);
                    try { Save(); }
                    catch { pending.Remove(transaction); throw; }
                    draft.Submitted = transaction;
                }
            }
            // Submission enters the mempool; only a validator-confirmed block creates the token.
            await node.BroadcastAsync(transaction).ConfigureAwait(false);
            return transaction.Id;
        }

        private void RequirePeers()
        {
            if (node.ConnectedPeerCount == 0)
                throw new InvalidOperationException("Nenhum par conectado. Configure PeerSeeds e aguarde a sincronização.");
        }

        private void ReceiveChain(Block[] blocks)
        {
            lock (sync)
            {
                try
                {
                    if (!chain.TryReplaceChain(blocks)) return;
                    RevalidatePending(pending.ToArray());
                    Save();
                }
                catch (Exception error) when (IsInvalidNetworkData(error)) { }
            }
        }

        private void ReceiveTransaction(Transaction transaction)
        {
            lock (sync)
            {
                if (transaction == null || pending.Any(tx => tx.Id == transaction.Id)) return;
                try
                {
                    chain.ValidatePendingTransactions(pending.Concat(new[] { transaction }));
                    pending.Add(transaction);
                    Save();
                }
                catch (Exception error) when (IsInvalidNetworkData(error)) { }
            }
        }

        private void RevalidatePending(IEnumerable<Transaction> transactions)
        {
            pending.Clear();
            foreach (Transaction transaction in Blockchain.OrderByFeePriority(transactions))
            {
                try
                {
                    chain.ValidatePendingTransactions(pending.Concat(new[] { transaction }));
                    pending.Add(transaction);
                }
                catch (Exception error) when (IsInvalidNetworkData(error)) { }
            }
        }

        private static bool IsInvalidNetworkData(Exception error) => error is InvalidOperationException ||
            error is ArgumentException || error is CryptographicException || error is FormatException || error is OverflowException;

        private void Save()
        {
            if (string.IsNullOrEmpty(snapshotPath)) return;
            string temporary = snapshotPath + ".tmp";
            using (var stream = File.Create(temporary))
                new DataContractJsonSerializer(typeof(NetworkSnapshot)).WriteObject(stream,
                    new NetworkSnapshot { NetworkId = Blockchain.NetworkId, Blocks = chain.Blocks.ToArray(), Pending = pending.ToArray() });
            if (File.Exists(snapshotPath)) File.Replace(temporary, snapshotPath, null);
            else File.Move(temporary, snapshotPath);
        }

        public void Dispose() { node.Dispose(); }
    }
}
