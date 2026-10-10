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
    /// <summary>Validates public data and relays locally signed token transactions to the existing network.</summary>
    public sealed partial class TokenNetworkService : IDisposable
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
        {
            Directory.CreateDirectory(dataDirectory);
            statePath = Path.Combine(dataDirectory, "dex-network.json");
            if (File.Exists(statePath))
            {
                NetworkState state = Deserialize(File.ReadAllBytes(statePath));
                if (state.NetworkId != Blockchain.NetworkId ||
                    (state.ConsensusVersion != Blockchain.ConsensusVersion && state.ConsensusVersion != 3 && state.ConsensusVersion != 4 && state.ConsensusVersion != 7 && state.ConsensusVersion != 8 && state.ConsensusVersion != 9 && state.ConsensusVersion != 10 && state.ConsensusVersion != 11))
                    throw new InvalidOperationException("O cache pertence a outra rede ou versão de consenso.");
                blockchain = new Blockchain(state.Blocks);
                pending = state.Pending ?? new List<Transaction>();
                submitted = state.Submitted ?? new List<SubmittedToken>();
                pending = pending.Concat(blockchain.GetUncountedWalletCreations()).GroupBy(tx => tx.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
                blockchain.ValidatePendingTransactions(pending);
            }
            else
            {
                blockchain = new Blockchain();
                pending = new List<Transaction>();
                submitted = new List<SubmittedToken>();
            }
            node = new PeerNode(port, false, Path.Combine(dataDirectory, "dex-peers.dat"));
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
            lock (sync) return blockchain.GetSpendableBalance(addresses, pending);
        }

        public object GetBalanceDetails(string[] addresses)
        {
            lock (sync)
            {
                var confirmed = blockchain.GetUnspentOutputs(addresses).Where(item => item.Output.AssetId == null).ToArray();
                var reserved = new HashSet<string>(pending.SelectMany(tx => tx.Inputs).Select(input =>
                    input.TransactionId + ":" + input.OutputIndex.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal);
                var pendingIds = new HashSet<string>(pending.Select(tx => tx.Id), StringComparer.Ordinal);
                var available = new HashSet<string>(blockchain.GetSpendableOutputs(addresses, pending).Select(item =>
                    item.TransactionId + ":" + item.OutputIndex.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal);
                long reservedAmount = confirmed.Where(item => item.TransactionKind != TransactionKind.StakeLock &&
                    reserved.Contains(item.TransactionId + ":" + item.OutputIndex.ToString(CultureInfo.InvariantCulture)))
                    .Sum(item => item.Output.Amount);
                long incoming = blockchain.GetUnspentOutputs(addresses, pending).Where(item => item.Output.AssetId == null &&
                    item.TransactionKind != TransactionKind.StakeLock && pendingIds.Contains(item.TransactionId) &&
                    !available.Contains(item.TransactionId + ":" + item.OutputIndex.ToString(CultureInfo.InvariantCulture))).Sum(item => item.Output.Amount);
                var owned = new HashSet<string>(addresses, StringComparer.Ordinal);
                long pendingFees = pending.Where(tx => tx.Inputs.Any(input => owned.Contains(AddressFor(input.PublicKey))))
                    .Sum(tx => tx.Fee);
                return new {
                    pendingFeesAtomic = pendingFees.ToString(CultureInfo.InvariantCulture),
                    balanceAtomic = blockchain.GetSpendableBalance(addresses, pending).ToString(CultureInfo.InvariantCulture),
                    confirmedAtomic = confirmed.Sum(item => item.Output.Amount).ToString(CultureInfo.InvariantCulture),
                    reservedAtomic = reservedAmount.ToString(CultureInfo.InvariantCulture),
                    pendingIncomingAtomic = incoming.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        private long GetSelectedFee(int priority, long? selected)
        {
            if (priority != 1 && priority != 2 && priority != 4 ||
                selected.HasValue && (selected.Value < Blockchain.TransferFeeStep || selected.Value > Blockchain.MaximumTransferFee))
                throw new TokenOperationException("fee_invalid", "Selecione uma taxa válida em POVIX.");
            return selected ?? Blockchain.CalculateAutomaticFee(pending.Count, priority);
        }

        public object Prepare(CreateTokenViewModel model, string[] publicKeys, string changeAddress, string owner)
        {
            lock (sync)
            {
                RequireReady();
                foreach (string id in drafts.Where(item => item.Value.ExpiresUtc < DateTime.UtcNow).Select(item => item.Key).ToArray()) drafts.Remove(id);
                foreach (string id in transferDrafts.Where(item => item.Value.ExpiresUtc < DateTime.UtcNow).Select(item => item.Key).ToArray()) transferDrafts.Remove(id);
                if (drafts.Count + transferDrafts.Count >= 200 || drafts.Values.Count(item => item.Owner == owner) + transferDrafts.Values.Count(item => item.Owner == owner) >= 5)
                    throw new InvalidOperationException("Há preparações em andamento. Aguarde alguns minutos e tente novamente.");
                long amount;
                if (!CreateTokenViewModel.TryParseSupply(model.Supply, model.Decimals, out amount))
                    throw new ArgumentException("Quantidade inválida.");
                long fee = GetSelectedFee(model.FeePriority, model.FeeAtomic);
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
                SubmittedToken prior = submitted.FirstOrDefault(item => item.DraftId == draftId && item.Transaction.Kind == TransactionKind.TokenCreate);
                if (prior != null) return prior.Transaction.Id;
                RequireReady();
                PreparedToken draft;
                if (draftId == null || !drafts.TryGetValue(draftId, out draft))
                    throw new TokenOperationException("draft_missing", "A preparação não está mais disponível no DEX. Se o servidor foi reiniciado, prepare novamente.");
                if (draft.Owner != owner)
                    throw new TokenOperationException("draft_session_changed", "A sessão do navegador mudou desde a preparação. Permita os cookies deste site e prepare novamente na mesma janela.");
                if (draft.ExpiresUtc <= DateTime.UtcNow)
                    throw new TokenOperationException("draft_expired", "A preparação expirou após 15 minutos. Revise o formulário e prepare novamente.");
                if (pending.Count >= MaximumPending)
                    throw new TokenOperationException("pending_limit", "A fila está cheia. Aguarde e tente enviar a mesma preparação novamente.", true);
                if (signatures == null || signatures.Length != draft.Creation.Inputs.Count || signatures.Any(string.IsNullOrWhiteSpace))
                    throw new TokenOperationException("signatures_invalid", "O envio não contém uma assinatura para cada entrada da transação. Recarregue o DEX e prepare novamente.");
                transaction = draft.Creation.Complete(signatures);
                try { blockchain.ValidatePendingTransactions(pending.Concat(new[] { transaction })); }
                catch (Exception error) when (IsInvalidData(error))
                {
                    // Return fixed diagnostics, never exception text supplied by transaction data.
                    if (error.Message == "Missing or already spent input." || error.Message == "Locked validator collateral requires an unlock transaction.")
                        throw new TokenOperationException("funding_unavailable", "O saldo escolhido para pagar a taxa foi usado, reservado ou bloqueado desde a preparação. Atualize o saldo e prepare novamente.");
                    if (error.Message == "Invalid signature." || error is CryptographicException || error is FormatException)
                        throw new TokenOperationException("signature_invalid", "A assinatura não corresponde à transação preparada. Recarregue o DEX, abra a carteira novamente e prepare outra criação.");
                    throw new TokenOperationException("transaction_invalid", "A blockchain rejeitou a validação da transação. Atualize o DEX e o PrivateCoin.Core e prepare novamente.");
                }
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
                    pending.FirstOrDefault(item => item.Id == transactionId) ?? submitted.FirstOrDefault(item => item.Transaction.Id == transactionId)?.Transaction;
                if (transaction == null || transaction.Kind != TransactionKind.TokenCreate) return null;
                string status = block != null ? "confirmed" : pending.Any(item => item.Id == transactionId) ?
                    blockchain.HasValidTransactionApproval(transaction, pending) ? "validated" : "pending" : "rejected";
                string waitingReason = null;
                if (status == "pending")
                {
                    var active = blockchain.GetActiveValidators(pending);
                    var participants = new HashSet<string>(transaction.Inputs.Select(input => AddressFor(input.PublicKey))
                        .Concat(transaction.Outputs.Select(output => output.OneTimeAddress)), StringComparer.Ordinal);
                    waitingReason = active.Count == 0 ? "A criação aguarda uma carteira validadora com tokens bloqueados na rede sincronizada." :
                        !active.Any(stake => !stake.OwnedAddresses.Any(participants.Contains)) ?
                            "Os tokens bloqueados pertencem à carteira que cria ou recebe este token. Pelas regras atuais, outra carteira com tokens bloqueados precisa validar a criação." :
                            "Há tokens bloqueados elegíveis. A criação aguarda a assinatura de um nó conectado com a carteira validadora aberta.";
                }
                return new TokenRegistrationViewModel { TransactionId = transaction.Id, Token = transaction.Token,
                    Fee = transaction.Fee, DestinationAddress = transaction.Outputs.First(output => output.AssetId == transaction.Token.Id).OneTimeAddress,
                    PovixOutputs = transaction.Outputs.Where(output => output.AssetId == null).Select(output =>
                        new TransactionOutput { Amount = output.Amount, OneTimeAddress = output.OneTimeAddress }).ToArray(),
                    Status = status, WaitingReason = waitingReason,
                    BlockHeight = block?.Height, BlockHash = block?.Hash, PeerCount = node.ConnectedPeerCount,
                    Confirmations = block == null ? 0 : blockchain.Blocks.Last().Height - block.Height + 1 };
            }
        }

        private void RequireReady()
        {
            if (node.ConnectedPeerCount == 0 || !synchronized)
                throw new TokenOperationException("network_not_ready", "Aguarde a conexão e a sincronização com a rede POVIX e tente novamente.", true);
        }

        private void ReceiveChain(object sender, ChainReceivedEventArgs args)
        {
            try
            {
                lock (sync)
                {
                    var candidate = new Blockchain(blockchain.Blocks);
                    bool changed;
                    if (!candidate.TrySynchronizeChain(args.Blocks, out changed)) return;
                    if (changed)
                    {
                        var confirmed = new HashSet<string>(candidate.Blocks.SelectMany(block => block.Transactions).Select(tx => tx.Id));
                        var valid = new List<Transaction>();
                        var recoverable = pending.Concat(candidate.GetUncountedWalletCreations()).Concat(blockchain.Blocks.SelectMany(block => block.Transactions)
                                .Where(tx => tx.Inputs.Count > 0 || tx.Kind == TransactionKind.WalletCreate))
                            .Concat(submitted.Select(item => item.Transaction)).Where(tx => !confirmed.Contains(tx.Id))
                            .GroupBy(tx => tx.Id, StringComparer.Ordinal).Select(group => group.First());
                        foreach (Transaction transaction in Blockchain.OrderByFeePriority(recoverable))
                        {
                            if (valid.Count == MaximumPending) break;
                            try { candidate.ValidatePendingTransactions(valid.Concat(new[] { transaction })); valid.Add(transaction); }
                            catch (Exception error) when (IsInvalidData(error)) { }
                        }
                        Save(candidate, valid, submitted);
                        blockchain = candidate;
                        pending = valid;
                    }
                    synchronized = node.ConnectedPeerCount > 0; // Identical validated chains also complete synchronization.
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
                    if (transaction == null || blockchain.Blocks.Any(block => block.Transactions.Any(tx => tx.Id == transaction.Id))) return;
                    Transaction existing = pending.FirstOrDefault(tx => tx.Id == transaction.Id);
                    if (existing != null)
                    {
                        if (blockchain.HasValidTransactionApproval(existing, pending) ||
                            !blockchain.HasValidTransactionApproval(transaction, pending)) return;
                        var approved = pending.Select(tx => tx.Id == transaction.Id ? transaction : tx).ToList();
                        Save(blockchain, approved, submitted);
                        pending = approved;
                        return;
                    }
                    if (pending.Count >= MaximumPending) return;
                    if (transaction.TransactionApproval != null && !blockchain.HasValidTransactionApproval(transaction, pending)) return;
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
            lock (sync) transactions = Blockchain.OrderByFeePriority(pending).ToArray();
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
            try { await node.BroadcastChainAsync(blocks).ConfigureAwait(false); RelayPending(); }
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
            [DataMember(EmitDefaultValue = false)] public TokenDefinition Token { get; set; }
            [DataMember(EmitDefaultValue = false)] public string CreatorKey { get; set; }
            [DataMember(EmitDefaultValue = false)] public string CreatorSignature { get; set; }
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

    public sealed class TokenOperationException : InvalidOperationException
    {
        public TokenOperationException(string code, string message, bool retryable = false) : base(message)
        { Code = code; Retryable = retryable; }
        public string Code { get; }
        public bool Retryable { get; }
    }
}
