using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Povix.Dex.Models;
using PrivateCoin.Core;

namespace Povix.Dex.Services
{
    /// <summary>Validates public data and relays locally signed creation transactions to the existing network.</summary>
    public sealed class TokenNetworkService : IDisposable
    {
        private const int MaximumPending = 5000;
        private readonly object sync = new object();
        private readonly string statePath;
        private readonly PeerNode node;
        private readonly Dictionary<string, PreparedToken> drafts = new Dictionary<string, PreparedToken>();
        private Blockchain blockchain;
        private List<Transaction> pending;
        private List<SubmittedToken> submitted;
        private bool synchronized;

        public TokenNetworkService(string dataDirectory, int port, IEnumerable<string> seeds)
            : this(dataDirectory, port, seeds, FinalityPolicy.FromConfiguration()) { }

        public TokenNetworkService(string dataDirectory, int port, IEnumerable<string> seeds, FinalityPolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            Directory.CreateDirectory(dataDirectory);
            statePath = Path.Combine(dataDirectory, "dex-network.json");
            if (File.Exists(statePath))
            {
                NetworkState state = Deserialize(File.ReadAllBytes(statePath));
                if (state.NetworkId != Blockchain.NetworkId || (state.ConsensusVersion != Blockchain.ConsensusVersion && state.ConsensusVersion != 3))
                    throw new InvalidOperationException("O cache pertence a outra rede ou versão de consenso.");
                blockchain = new Blockchain(state.Blocks, policy);
                pending = state.Pending ?? new List<Transaction>();
                submitted = state.Submitted ?? new List<SubmittedToken>();
                blockchain.ValidatePendingTransactions(pending);
            }
            else
            {
                blockchain = new Blockchain(policy);
                pending = new List<Transaction>();
                submitted = new List<SubmittedToken>();
            }
            node = new PeerNode(port, false, Path.Combine(dataDirectory, "dex-peers.dat"), policy);
            node.ChainReceived += ReceiveChain;
            node.TransactionReceived += ReceiveTransaction;
            node.SynchronizationRequested += (sender, args) => RelayChain();
            node.PeerCountChanged += (sender, args) => { lock (sync) { if (node.ConnectedPeerCount == 0) synchronized = false; } };
            try { node.Start(seeds); }
            catch { node.Dispose(); throw; }
        }

        public TokenNetworkViewModel GetNetwork()
        {
            lock (sync)
                return new TokenNetworkViewModel { Height = blockchain.Blocks.Last().Height,
                    PeerCount = node.ConnectedPeerCount, Synchronized = synchronized,
                    Fees = new[] { 1, 2, 4 }.Select(priority => Blockchain.CalculateAutomaticFee(pending.Count, priority)).ToArray() };
        }

        public long GetBalance(string[] addresses)
        {
            lock (sync) return blockchain.GetConfirmedView().GetSpendableBalance(addresses, pending);
        }

        public object Prepare(CreateTokenViewModel model, string[] publicKeys, string changeAddress, string owner)
        {
            lock (sync)
            {
                RequireReady();
                foreach (string id in drafts.Where(item => item.Value.ExpiresUtc < DateTime.UtcNow).Select(item => item.Key).ToArray()) drafts.Remove(id);
                if (drafts.Count >= 200 || drafts.Values.Count(item => item.Owner == owner) >= 5)
                    throw new InvalidOperationException("Há preparações em andamento. Aguarde alguns minutos e tente novamente.");
                long amount;
                if (!CreateTokenViewModel.TryParseSupply(model.Supply, model.Decimals, out amount))
                    throw new ArgumentException("Quantidade inválida.");
                long fee = Blockchain.CalculateAutomaticFee(pending.Count, model.FeePriority);
                var creation = TokenCreation.Prepare(blockchain, pending, publicKeys, model.Name, model.Symbol,
                    model.Decimals, amount, model.DestinationAddress, changeAddress, fee);
                string draftId = Guid.NewGuid().ToString("N");
                var draft = new PreparedToken { Creation = creation, Owner = owner, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) };
                drafts.Add(draftId, draft);
                return new { draftId, signingPayload = Convert.ToBase64String(creation.SigningPayload),
                    inputAddresses = creation.InputAddresses, tokenId = creation.Token.Id,
                    timestampUtcTicks = creation.TimestampUtcTicks.ToString(CultureInfo.InvariantCulture),
                    inputs = creation.Inputs.Select((input, index) => new { transactionId = input.TransactionId,
                        outputIndex = input.OutputIndex, address = creation.InputAddresses[index] }).ToArray(),
                    name = creation.Token.Name, symbol = creation.Token.Symbol, decimals = creation.Token.Decimals,
                    supplyAtomic = amount.ToString(CultureInfo.InvariantCulture), destinationAddress = creation.DestinationAddress,
                    changeAddress = creation.ChangeAddress, changeAtomic = creation.ChangeAmount.ToString(CultureInfo.InvariantCulture),
                    feeAtomic = fee.ToString(CultureInfo.InvariantCulture), networkId = Blockchain.NetworkId,
                    consensusVersion = Blockchain.ConsensusVersion, expiresUtc = draft.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture) };
            }
        }

        public async Task<string> SubmitAsync(string draftId, string[] signatures, string owner)
        {
            Transaction transaction;
            lock (sync)
            {
                SubmittedToken prior = submitted.FirstOrDefault(item => item.DraftId == draftId);
                if (prior != null) return prior.Transaction.Id;
                RequireReady();
                PreparedToken draft;
                if (!drafts.TryGetValue(draftId, out draft) || draft.Owner != owner || draft.ExpiresUtc < DateTime.UtcNow)
                    throw new InvalidOperationException("A preparação expirou. Revise o formulário e prepare novamente.");
                if (pending.Count >= MaximumPending) throw new InvalidOperationException("A fila está cheia. Tente novamente mais tarde.");
                transaction = draft.Creation.Complete(signatures);
                blockchain.ValidatePendingTransactions(pending.Concat(new[] { transaction }));
                var nextPending = pending.Concat(new[] { transaction }).ToList();
                var nextSubmitted = submitted.Concat(new[] { new SubmittedToken { DraftId = draftId, Transaction = transaction } }).ToList();
                Save(blockchain, nextPending, nextSubmitted);
                pending = nextPending;
                submitted = nextSubmitted;
                drafts.Remove(draftId);
            }
            // Durable acceptance precedes gossip; reconnect/synchronization also retries the queue.
            await Relay(transaction).ConfigureAwait(false);
            return transaction.Id;
        }

        public TokenRegistrationViewModel GetRegistration(string transactionId)
        {
            lock (sync)
            {
                Block block = blockchain.Blocks.FirstOrDefault(item => item.Transactions.Any(tx => tx.Id == transactionId));
                Transaction transaction = block?.Transactions.FirstOrDefault(tx => tx.Id == transactionId) ??
                    submitted.FirstOrDefault(item => item.Transaction.Id == transactionId)?.Transaction;
                if (transaction == null || transaction.Kind != TransactionKind.TokenCreate) return null;
                return new TokenRegistrationViewModel { TransactionId = transaction.Id, Token = transaction.Token,
                    Fee = transaction.Fee, DestinationAddress = transaction.Outputs.First(output => output.AssetId == transaction.Token.Id).OneTimeAddress,
                    Status = block != null ? "confirmed" : pending.Any(item => item.Id == transactionId) ? "pending" : "rejected",
                    BlockHeight = block?.Height, BlockHash = block?.Hash, PeerCount = node.ConnectedPeerCount,
                    Confirmations = block == null ? 0 : blockchain.Blocks.Last().Height - block.Height + 1 };
            }
        }

        private void RequireReady()
        {
            if (node.ConnectedPeerCount == 0 || !synchronized)
                throw new InvalidOperationException("Aguarde a conexão e a sincronização com a rede POVIX.");
        }

        private void ReceiveChain(object sender, ChainReceivedEventArgs args)
        {
            try
            {
                lock (sync)
                {
                    var candidate = new Blockchain(blockchain.Blocks, blockchain.Finality);
                    if (candidate.TryReplaceChain(args.Blocks))
                    {
                        var confirmed = new HashSet<string>(candidate.Blocks.SelectMany(block => block.Transactions).Select(tx => tx.Id));
                        var valid = new List<Transaction>();
                        foreach (Transaction transaction in Blockchain.OrderByFeePriority(pending.Where(tx => !confirmed.Contains(tx.Id))))
                        {
                            try { candidate.ValidatePendingTransactions(valid.Concat(new[] { transaction })); valid.Add(transaction); }
                            catch (Exception error) when (IsInvalidData(error)) { }
                        }
                        Save(candidate, valid, submitted);
                        blockchain = candidate;
                        pending = valid;
                    }
                    synchronized = blockchain.Finality == null || blockchain.FinalizedHeight >= blockchain.Finality.AnchorHeight;
                }
                RelayPending();
            }
            catch (Exception error) when (IsInvalidData(error) || error is IOException || error is UnauthorizedAccessException)
            { Trace.TraceWarning("DEX: não foi possível aceitar a cadeia recebida ({0}).", error.GetType().Name); }
        }

        private void ReceiveTransaction(object sender, TransactionReceivedEventArgs args)
        {
            try
            {
                lock (sync)
                {
                    Transaction transaction = args.Transaction;
                    if (transaction == null || pending.Count >= MaximumPending || pending.Any(tx => tx.Id == transaction.Id) ||
                        blockchain.Blocks.Any(block => block.Transactions.Any(tx => tx.Id == transaction.Id))) return;
                    blockchain.ValidatePendingTransactions(pending.Concat(new[] { transaction }));
                    var next = pending.Concat(new[] { transaction }).ToList();
                    Save(blockchain, next, submitted);
                    pending = next;
                }
            }
            catch (Exception error) when (IsInvalidData(error) || error is IOException || error is UnauthorizedAccessException)
            { Trace.TraceWarning("DEX: transação recebida descartada ({0}).", error.GetType().Name); }
        }

        private async void RelayPending()
        {
            Transaction[] transactions;
            lock (sync) transactions = pending.ToArray();
            foreach (Transaction transaction in transactions) await Relay(transaction).ConfigureAwait(false);
        }

        private async Task Relay(Transaction transaction)
        {
            try { await node.BroadcastAsync(transaction).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException || error is System.Net.Sockets.SocketException || error is ObjectDisposedException || error is OperationCanceledException)
            { Trace.TraceWarning("DEX: propagação será repetida ao sincronizar ({0}).", error.GetType().Name); }
        }

        private async void RelayChain()
        {
            Block[] blocks;
            lock (sync) blocks = blockchain.Blocks.ToArray();
            try { await node.BroadcastChainAsync(blocks).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException || error is System.Net.Sockets.SocketException || error is ObjectDisposedException || error is OperationCanceledException)
            { Trace.TraceWarning("DEX: resposta de sincronização interrompida ({0}).", error.GetType().Name); }
        }

        internal static bool IsInvalidData(Exception error) => error is InvalidOperationException || error is ArgumentException ||
            error is FormatException || error is OverflowException || error is CryptographicException ||
            error is System.Xml.XmlException || error is NullReferenceException || error is KeyNotFoundException;

        private void Save(Blockchain chain, List<Transaction> transactions, List<SubmittedToken> receipts)
        {
            var state = new NetworkState { NetworkId = Blockchain.NetworkId, ConsensusVersion = Blockchain.ConsensusVersion,
                Blocks = chain.Blocks.ToList(), Pending = transactions, Submitted = receipts };
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(NetworkState)).WriteObject(stream, state);
                AtomicFile.Write(statePath, stream.ToArray());
            }
        }

        private static NetworkState Deserialize(byte[] data)
        {
            using (var stream = new MemoryStream(data)) return (NetworkState)new DataContractJsonSerializer(typeof(NetworkState)).ReadObject(stream);
        }

        public void Dispose() { node.Dispose(); }

        private sealed class PreparedToken
        {
            public UnsignedTokenCreation Creation { get; set; }
            public string Owner { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }

        [DataContract]
        private sealed class SubmittedToken
        {
            [DataMember] public string DraftId { get; set; }
            [DataMember] public Transaction Transaction { get; set; }
        }

        [DataContract]
        private sealed class NetworkState
        {
            [DataMember] public string NetworkId { get; set; }
            [DataMember] public int ConsensusVersion { get; set; }
            [DataMember] public List<Block> Blocks { get; set; }
            [DataMember] public List<Transaction> Pending { get; set; }
            [DataMember] public List<SubmittedToken> Submitted { get; set; }
        }
    }
}
