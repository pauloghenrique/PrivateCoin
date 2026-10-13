using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Povix.Dex.Models;
using PrivateCoin.Core;

namespace Povix.Dex.Services
{
    public sealed partial class TokenNetworkService
    {
        private readonly Dictionary<string, PreparedExchange> exchangeDrafts = new Dictionary<string, PreparedExchange>();

        public object GetExchangeCatalogue(string[] publicKeys)
        {
            lock (sync)
            {
                string[] addresses = publicKeys.Select(AddressFor).ToArray();
                var excluded = PoolInputKeys();
                var assets = new[] { NativeAsset() }.Concat(blockchain.GetTokens()).Select(token => new {
                    id = token.Id, name = token.Name, symbol = token.Symbol, decimals = token.Decimals,
                    balanceAtomic = Atomic((token.Id == "povix" ? blockchain.GetSpendableOutputs(addresses, pending) :
                        blockchain.GetSpendableTokenOutputs(addresses, pending, token.Id)).Where(item => !excluded.Contains(item.TransactionId + ":" + item.OutputIndex))
                        .Sum(item => item.Output.Amount)) }).ToArray();
                var pools = submitted.Where(item => item.Exchange?.Operation == "pool").Select(item => Snapshot(item.Transaction.Id))
                    .Where(item => item != null).Select(PoolResponse).ToArray();
                return new { assets, pools, poolFeeBasisPoints = TokenExchange.PoolFeeBasisPoints };
            }
        }

        public object PreparePool(CreatePoolViewModel model, string[] publicKeys, string address, string owner)
        {
            lock (sync)
            {
                RequireReady(); ReserveDraftSlot(owner);
                var a = Asset(model.AssetA); var b = Asset(model.AssetB);
                long amountA = ParseAmount(model.AmountA, a.Decimals), amountB = ParseAmount(model.AmountB, b.Decimals);
                string key = publicKeys.FirstOrDefault(value => AddressFor(value) == address);
                if (key == null) throw new TokenOperationException("wallet_invalid", "O endereço da pool deve pertencer à carteira aberta.");
                var tx = TokenExchange.PrepareLiquidity(blockchain, pending, publicKeys, AssetId(a), AssetId(b), amountA, amountB,
                    address, GetSelectedFee(model.FeePriority, model.FeeAtomic), PoolInputKeys());
                var draft = new PreparedExchange { Transaction = tx, Operation = "pool", Owner = owner, ProviderKey = key,
                    Address = address, AssetA = a, AssetB = b, ReserveA = amountA, ReserveB = amountB, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) };
                return AddExchangeDraft(draft);
            }
        }

        public object PrepareSwap(SwapViewModel model, string[] publicKeys, string address, string owner)
        {
            lock (sync)
            {
                RequireReady(); ReserveDraftSlot(owner);
                var pool = Snapshot(model.PoolId);
                if (pool == null) throw new TokenOperationException("pool_unavailable", "A pool não foi encontrada nesta instalação do DEX.");
                if (!pool.Available) throw new TokenOperationException(pool.Status == "pending" || pool.Status == "validated" ? "pool_unconfirmed" :
                    pool.Status == "busy" ? "pool_reserved" : "pool_unavailable", pool.StatusMessage,
                    pool.Status == "pending" || pool.Status == "validated" || pool.Status == "busy");
                if (model.InputSide != 0 && model.InputSide != 1) throw new TokenOperationException("fields_invalid", "Selecione um lado da troca.");
                var input = model.InputSide == 0 ? pool.AssetA : pool.AssetB;
                var output = model.InputSide == 0 ? pool.AssetB : pool.AssetA;
                long amount = ParseAmount(model.Amount, input.Decimals), minimum = ParseAmount(model.MinimumReceive, output.Decimals);
                var tx = TokenExchange.PrepareSwap(blockchain, pending, publicKeys, pool.ReserveA, pool.ReserveB, pool.ProviderKey,
                    model.InputSide, amount, minimum, address, GetSelectedFee(model.FeePriority, model.FeeAtomic), PoolInputKeys());
                var draft = new PreparedExchange { Transaction = tx, Operation = "swap", Owner = owner, ProviderKey = pool.ProviderKey,
                    PoolId = pool.Id, ReserveId = pool.Transaction.Id, Address = address, AssetA = pool.AssetA, AssetB = pool.AssetB,
                    ReserveA = pool.ReserveA.Output.Amount, ReserveB = pool.ReserveB.Output.Amount, InputSide = model.InputSide,
                    Amount = amount, MinimumReceive = minimum, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) };
                return AddExchangeDraft(draft);
            }
        }

        public object PrepareReserveOperation(string poolId, string operation, string[] publicKeys, int feePriority, long? feeAtomic, string owner)
        {
            lock (sync)
            {
                RequireReady(); ReserveDraftSlot(owner);
                var pool = Snapshot(poolId);
                if ((operation != "lock" && operation != "close") || pool == null || !pool.CanManage ||
                    pool.Locked != (operation == "close"))
                    throw new TokenOperationException("pool_unavailable", "A pool não está disponível para esta operação.");
                if (!publicKeys.Contains(pool.ProviderKey, StringComparer.Ordinal))
                    throw new TokenOperationException("wallet_invalid", "Abra a carteira provedora para administrar as reservas.");
                string address = AddressFor(pool.ProviderKey);
                var tx = TokenExchange.PrepareReserveOperation(blockchain, pending, publicKeys, pool.ReserveA, pool.ReserveB,
                    address, GetSelectedFee(feePriority, feeAtomic), operation == "close", PoolInputKeys());
                return AddExchangeDraft(new PreparedExchange { Transaction = tx, Operation = operation, Owner = owner,
                    ProviderKey = pool.ProviderKey, PoolId = poolId, ReserveId = pool.Transaction.Id, Address = address,
                    AssetA = pool.AssetA, AssetB = pool.AssetB, ReserveA = pool.ReserveA.Output.Amount,
                    ReserveB = pool.ReserveB.Output.Amount, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) });
            }
        }

        public async Task<string> SubmitReserveOperationAsync(string draftId, string[] signatures, string owner)
        {
            Transaction tx;
            lock (sync)
            {
                var prior = submitted.FirstOrDefault(item => item.DraftId == draftId &&
                    (item.Exchange?.Operation == "lock" || item.Exchange?.Operation == "close"));
                if (prior != null) return PriorExchange(draftId, prior.Exchange.Operation, signatures).Transaction.Id;
                RequireReady();
                PreparedExchange draft;
                if (!exchangeDrafts.TryGetValue(draftId ?? "", out draft) || (draft.Operation != "lock" && draft.Operation != "close"))
                    throw new TokenOperationException("draft_missing", "Prepare novamente a operação de reservas.");
                draft = ExchangeDraft(draftId, owner, draft.Operation);
                var pool = Snapshot(draft.PoolId);
                if (pool == null || !pool.CanManage || pool.Transaction.Id != draft.ReserveId)
                    throw new TokenOperationException("pool_changed", "As reservas mudaram. Prepare novamente.");
                VerifyExchangeSignatures(draft.Transaction, signatures, 0, draft.Transaction.Inputs.Count);
                tx = AcceptExchange(draftId, draft, signatures);
            }
            await Relay(tx).ConfigureAwait(false); return tx.Id;
        }

        public async Task<string> SubmitPoolAsync(string draftId, string[] signatures, string owner)
        {
            Transaction tx;
            lock (sync)
            {
                var prior = PriorExchange(draftId, "pool", signatures);
                if (prior != null) return prior.Transaction.Id;
                RequireReady();
                var draft = ExchangeDraft(draftId, owner, "pool");
                VerifyExchangeSignatures(draft.Transaction, signatures, 0, draft.Transaction.Inputs.Count);
                tx = AcceptExchange(draftId, draft, signatures);
            }
            await Relay(tx).ConfigureAwait(false); return tx.Id;
        }

        public async Task<string> SubmitSwapAsync(string draftId, string[] signatures, string owner)
        {
            Transaction tx;
            lock (sync)
            {
                var prior = PriorExchange(draftId, "swap", signatures);
                if (prior != null) return prior.Transaction.Id;
                RequireReady();
                var draft = ExchangeDraft(draftId, owner, "swap");
                RequireCurrentReserves(draft);
                VerifyExchangeSignatures(draft.Transaction, signatures, 0, draft.Transaction.UserInputCount);
                tx = AcceptExchange(draftId, draft, signatures);
            }
            await Relay(tx).ConfigureAwait(false); return tx.Id;
        }

        public ExchangeReceiptViewModel GetExchangeReceipt(string id)
        {
            lock (sync)
            {
                var record = submitted.FirstOrDefault(item => item.Transaction.Id == id && item.Exchange != null);
                if (record == null) return null;
                var block = blockchain.Blocks.FirstOrDefault(item => item.Transactions.Any(operation => operation.Id == id));
                var queued = pending.FirstOrDefault(item => item.Id == id);
                var tx = block?.Transactions.First(item => item.Id == id) ?? queued ?? record.Transaction;
                bool validated = block != null || queued != null && blockchain.HasValidTransactionApproval(queued, pending);
                var pool = Snapshot(record.Exchange.PoolId);
                return new ExchangeReceiptViewModel { TransactionId = id, PoolId = record.Exchange.PoolId, Operation = record.Exchange.Operation,
                    Status = block != null ? "confirmed" : queued == null ? "rejected" : validated ? "validated" : "pending",
                    Fee = tx.Fee, BlockHeight = block?.Height, BlockHash = block?.Hash, Confirmations = block == null ? 0 : blockchain.Blocks.Last().Height - block.Height + 1,
                    PeerCount = node.ConnectedPeerCount, ValidationCount = validated ? 1 : 0,
                    PoolAvailable = pool?.Available ?? false, PoolStatusMessage = pool?.StatusMessage ?? "A pool não foi encontrada nesta instalação do DEX.",
                    Outputs = tx.Outputs.Select((output, index) => {
                        var asset = output.AssetId == null ? NativeAsset() : output.AssetId == record.Exchange.AssetA.Id ? record.Exchange.AssetA : record.Exchange.AssetB;
                        return new ExchangeOutputViewModel { Amount = output.Amount, Symbol = asset.Symbol, Decimals = asset.Decimals, Address = output.OneTimeAddress,
                            Purpose = index < 2 ? record.Exchange.Operation == "close" ? "Reserva devolvida à carteira" :
                                tx.Kind == TransactionKind.LiquidityPool || tx.Kind == TransactionKind.LiquiditySwap ? "Reserva bloqueada da pool" : "Reserva da pool antiga (sem bloqueio)" :
                                record.Exchange.Operation == "swap" && index == 2 ? "Recebimento da troca" : "Troco da carteira" };
                    }).ToArray() };
            }
        }

        private Transaction AcceptExchange(string id, PreparedExchange draft, string[] signatures)
        {
            if (pending.Count >= MaximumPending) throw new TokenOperationException("pending_limit", "A fila está cheia. Repita o mesmo envio mais tarde.", true);
            var tx = draft.Transaction.Complete(signatures);
            try { blockchain.ValidatePendingTransactions(pending.Concat(new[] { tx })); }
            catch (Exception error) when (IsInvalidData(error))
            { throw new TokenOperationException("funding_unavailable", "As entradas foram gastas ou reservadas, ou a transação foi rejeitada. Atualize e prepare novamente."); }
            var nextPending = pending.Concat(new[] { tx }).ToList();
            var nextSubmitted = submitted.Concat(new[] { new SubmittedToken { DraftId = id, Transaction = tx,
                Exchange = new ExchangeOperation { Operation = draft.Operation, PoolId = draft.PoolId ?? tx.Id,
                    PreviousReserveId = draft.ReserveId, ProviderKey = draft.ProviderKey, AssetA = draft.AssetA, AssetB = draft.AssetB } } }).ToList();
            Save(blockchain, nextPending, nextSubmitted);
            pending = nextPending; submitted = nextSubmitted; exchangeDrafts.Remove(id); return tx;
        }
        private SubmittedToken PriorExchange(string id, string operation, string[] signatures)
        {
            var prior = submitted.FirstOrDefault(item => item.DraftId == id && item.Exchange?.Operation == operation);
            if (prior != null && (signatures == null || !prior.Transaction.Inputs.Take(operation == "swap" ? prior.Transaction.Inputs.Count - 2 : prior.Transaction.Inputs.Count)
                    .Select(input => input.Signature).SequenceEqual(signatures)))
                throw new TokenOperationException("signatures_invalid", "As assinaturas não correspondem ao envio já aceito.");
            return prior;
        }
        private void ReserveDraftSlot(string owner)
        {
            ExpireExchangeDrafts();
            foreach (var id in drafts.Where(item => item.Value.ExpiresUtc <= DateTime.UtcNow).Select(item => item.Key).ToArray()) drafts.Remove(id);
            foreach (var id in transferDrafts.Where(item => item.Value.ExpiresUtc <= DateTime.UtcNow).Select(item => item.Key).ToArray()) transferDrafts.Remove(id);
            if (drafts.Count + transferDrafts.Count + exchangeDrafts.Count >= 200 ||
                drafts.Values.Count(item => item.Owner == owner) + transferDrafts.Values.Count(item => item.Owner == owner) + exchangeDrafts.Values.Count(item => item.Owner == owner) >= 5)
                throw new TokenOperationException("draft_limit", "Há preparações em andamento. Aguarde alguns minutos.");
        }
        private void ExpireExchangeDrafts()
        { foreach (var id in exchangeDrafts.Where(item => item.Value.ExpiresUtc <= DateTime.UtcNow).Select(item => item.Key).ToArray()) exchangeDrafts.Remove(id); }
        private object AddExchangeDraft(PreparedExchange draft)
        {
            if (draft.Transaction.Inputs.Count > 1000) throw new TokenOperationException("inputs_limit", "A operação exige muitas entradas. Use uma quantidade menor.");
            string id = Guid.NewGuid().ToString("N"); exchangeDrafts.Add(id, draft); return DraftResponse(id, draft);
        }
        private PreparedExchange ExchangeDraft(string id, string owner, string operation)
        {
            PreparedExchange draft;
            if (!exchangeDrafts.TryGetValue(id ?? "", out draft) || draft.Operation != operation)
                throw new TokenOperationException("draft_missing", "A preparação não está mais disponível. Prepare novamente.");
            if (owner != null && owner != draft.Owner) throw new TokenOperationException("draft_session_changed", "A sessão mudou. Prepare novamente na mesma janela.");
            if (draft.ExpiresUtc <= DateTime.UtcNow) throw new TokenOperationException("draft_expired", "A preparação expirou após 15 minutos.");
            return draft;
        }
        private static void VerifyExchangeSignatures(UnsignedTokenExchange tx, string[] signatures, int offset, int count)
        {
            if (signatures == null || signatures.Length != count) throw new TokenOperationException("signatures_invalid", "É necessária uma assinatura para cada entrada desta carteira.");
            for (int i = 0; i < count; i++)
            {
                bool valid = false;
                try { using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(tx.Inputs[offset + i].PublicKey);
                    valid = rsa.VerifyData(tx.SigningPayload, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(signatures[i] ?? "")); } }
                catch (Exception error) when (error is FormatException || error is CryptographicException) { }
                if (!valid) throw new TokenOperationException("signature_invalid", "A assinatura não corresponde à operação preparada.");
            }
        }
        private bool CurrentReserves(PreparedExchange draft)
        { var pool = Snapshot(draft.PoolId); return pool != null && pool.Available && pool.Transaction.Id == draft.ReserveId; }
        private void RequireCurrentReserves(PreparedExchange draft)
        { if (!CurrentReserves(draft)) throw new TokenOperationException("pool_changed", "As reservas da pool mudaram ou estão ocupadas. Prepare outra troca."); }

        private PoolSnapshot Snapshot(string id)
        {
            var root = submitted.FirstOrDefault(item => item.Transaction.Id == id && item.Exchange?.Operation == "pool");
            if (root == null) return null;
            var confirmed = blockchain.Blocks.SelectMany(block => block.Transactions).ToDictionary(tx => tx.Id);
            Transaction current;
            bool rootConfirmed = confirmed.TryGetValue(id, out current);
            if (!rootConfirmed) current = root.Transaction;
            if (rootConfirmed)
            {
                while (true)
                {
                    var next = confirmed.Values.FirstOrDefault(tx =>
                        (tx.Kind == TransactionKind.LiquiditySwap || tx.Kind == TransactionKind.LiquidityClose ||
                            submitted.Any(item => item.Transaction.Id == tx.Id && (item.Exchange?.Operation == "lock" || item.Exchange?.Operation == "swap") && item.Exchange.PoolId == id)) &&
                        tx.Inputs.Any(input => input.TransactionId == current.Id && input.OutputIndex == 0) &&
                        tx.Inputs.Any(input => input.TransactionId == current.Id && input.OutputIndex == 1));
                    if (next == null) break;
                    current = next;
                    if (current.Kind == TransactionKind.LiquidityClose) break;
                }
            }
            string address = AddressFor(root.Exchange.ProviderKey);
            var outputs = blockchain.GetUnspentOutputs(new[] { address }).Where(item => item.TransactionId == current.Id && item.OutputIndex < 2).ToArray();
            var blocking = pending.FirstOrDefault(tx => tx.Inputs.Any(input => input.TransactionId == current.Id && input.OutputIndex < 2));
            bool closed = current.Kind == TransactionKind.LiquidityClose;
            bool locked = !closed && outputs.Length == 2 && outputs.All(output => output.IsPoolReserve);
            bool canManage = rootConfirmed && outputs.Length == 2 && blocking == null && !closed;
            bool available = canManage && locked;
            string status, reason, message;
            if (!rootConfirmed) {
                var creation = pending.FirstOrDefault(tx => tx.Id == id);
                bool approved = creation != null && blockchain.HasValidTransactionApproval(creation, pending);
                status = creation == null ? "rejected" : approved ? "validated" : "pending";
                reason = creation == null ? "creation_rejected" : approved ? "awaiting_block" : "awaiting_validation";
                message = creation == null ? "A criação da pool não está confirmada nem na fila atual. Confira o comprovante antes de criar outra pool." :
                    approved ? "A criação foi validada e aguarda confirmação em bloco. A pool ficará disponível após essa confirmação." :
                    "A criação aguarda validação da rede e confirmação em bloco. As reservas ainda não podem ser usadas em trocas.";
                blocking = null;
            } else if (closed) {
                status = "closed"; reason = "pool_closed";
                message = "Pool encerrada. As duas reservas foram devolvidas ao saldo livre da carteira provedora.";
                blocking = null;
            } else if (outputs.Length != 2) {
                blocking = blockchain.Blocks.Reverse().SelectMany(block => block.Transactions)
                    .FirstOrDefault(tx => tx.Inputs.Any(input => input.TransactionId == current.Id && input.OutputIndex < 2));
                status = "unavailable"; reason = blocking == null ? "reserves_missing" : "reserves_spent";
                message = blocking == null ? "As duas reservas não foram localizadas na cadeia atual. Confira os endereços e o comprovante da pool." :
                    "Uma reserva foi movimentada pela transação " + blocking.Id + ". Confira o saldo livre da carteira para criar outra pool.";
            } else if (blocking != null) {
                bool swap = submitted.Any(item => item.Transaction.Id == blocking.Id && item.Exchange?.Operation == "swap");
                status = "busy"; reason = swap ? "swap_pending" : "reserve_transaction_pending";
                message = (swap ? "Uma troca" : "Uma movimentação das reservas") + " aguarda confirmação na transação " + blocking.Id + ". Aguarde a atualização da rede.";
            } else if (!locked) {
                status = "unlocked"; reason = "legacy_reserves_unlocked";
                message = "Esta pool antiga ainda não tem bloqueio na blockchain. O provedor deve bloquear as reservas antes de trocar.";
            } else {
                status = "available"; reason = "confirmed_reserves";
                message = "Reservas bloqueadas na blockchain e disponíveis para troca. Transferências comuns não podem movimentá-las.";
            }
            return new PoolSnapshot { Id = id, Transaction = current, ProviderKey = root.Exchange.ProviderKey,
                AssetA = root.Exchange.AssetA, AssetB = root.Exchange.AssetB, Available = available, Locked = locked, CanManage = canManage,
                Status = status, StatusReason = reason, StatusMessage = message, BlockingTransactionId = blocking?.Id,
                ReserveA = outputs.FirstOrDefault(item => item.OutputIndex == 0), ReserveB = outputs.FirstOrDefault(item => item.OutputIndex == 1) };
        }
        private HashSet<string> PoolInputKeys()
        {
            var protectedPools = submitted.Where(item => item.Exchange?.Operation == "pool").Select(item => Snapshot(item.Transaction.Id))
                .Where(pool => pool.Status != "unavailable" && pool.Status != "rejected" && pool.Status != "closed");
            var exchangeIds = new HashSet<string>(submitted.Where(item => item.Exchange != null && item.Exchange.Operation != "close").Select(item => item.Transaction.Id), StringComparer.Ordinal);
            var locked = blockchain.GetUnspentOutputs(blockchain.Blocks.SelectMany(block => block.Transactions).SelectMany(tx => tx.Outputs)
                .Select(output => output.OneTimeAddress).Distinct()).Where(output => output.IsPoolReserve);
            return new HashSet<string>(protectedPools.Select(pool => pool.Transaction.Id).Concat(pending.Where(tx => exchangeIds.Contains(tx.Id)).Select(tx => tx.Id))
                .SelectMany(id => new[] { id + ":0", id + ":1" }).Concat(locked.Select(output => output.TransactionId + ":" + output.OutputIndex)), StringComparer.Ordinal);
        }
        private static object PoolResponse(PoolSnapshot pool) => new {
            poolId = pool.Id, providerAddress = AddressFor(pool.ProviderKey), assetA = AssetResponse(pool.AssetA), assetB = AssetResponse(pool.AssetB),
            reserveTransactionId = pool.Transaction.Id, reserveAAtomic = Atomic(pool.Transaction.Outputs[0].Amount), reserveBAtomic = Atomic(pool.Transaction.Outputs[1].Amount),
            status = pool.Status, available = pool.Available, locked = pool.Locked, canManage = pool.CanManage, statusReason = pool.StatusReason, statusMessage = pool.StatusMessage,
            blockingTransactionId = pool.BlockingTransactionId, poolFeeBasisPoints = TokenExchange.PoolFeeBasisPoints };
        private static object DraftResponse(string id, PreparedExchange draft) => new {
            draftId = id, operation = draft.Operation, transactionKind = (int)draft.Transaction.Kind, poolId = draft.PoolId, reserveTransactionId = draft.ReserveId,
            assetA = AssetResponse(draft.AssetA), assetB = AssetResponse(draft.AssetB), providerAddress = AddressFor(draft.ProviderKey),
            reserveAAtomic = Atomic(draft.ReserveA), reserveBAtomic = Atomic(draft.ReserveB), inputSide = draft.InputSide, amountAtomic = Atomic(draft.Amount),
            receiveAtomic = draft.Operation == "swap" ? Atomic(draft.Transaction.Outputs[2].Amount) : "0", minimumReceiveAtomic = Atomic(draft.MinimumReceive),
            changeAddress = draft.Address, feeAtomic = Atomic(draft.Transaction.Fee), userInputCount = draft.Transaction.UserInputCount,
            inputs = draft.Transaction.Inputs.Select((input, index) => new { transactionId = input.TransactionId, outputIndex = input.OutputIndex,
                address = draft.Transaction.Sources[index].Output.OneTimeAddress, assetId = draft.Transaction.Sources[index].Output.AssetId ?? "povix",
                amountAtomic = Atomic(draft.Transaction.Sources[index].Output.Amount) }).ToArray(),
            outputs = draft.Transaction.Outputs.Select(output => new { assetId = output.AssetId ?? "povix", amountAtomic = Atomic(output.Amount), address = output.OneTimeAddress }).ToArray(),
            signingPayload = Convert.ToBase64String(draft.Transaction.SigningPayload), timestampUtcTicks = Atomic(draft.Transaction.TimestampUtcTicks),
            networkId = Blockchain.NetworkId, consensusVersion = Blockchain.ConsensusVersion, expiresUtc = draft.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture),
            poolFeeBasisPoints = TokenExchange.PoolFeeBasisPoints };
        private TokenDefinition Asset(string id)
        { if (id == "povix") return NativeAsset(); var token = blockchain.GetTokens().FirstOrDefault(item => item.Id == id);
            if (token == null) throw new TokenOperationException("token_unconfirmed", "O token precisa estar confirmado na blockchain."); return token; }
        private static TokenDefinition NativeAsset() => new TokenDefinition { Id = "povix", Name = "POVIX", Symbol = "POVIX", Decimals = 8 };
        private static string AssetId(TokenDefinition token) => token.Id == "povix" ? null : token.Id;
        private static object AssetResponse(TokenDefinition token) => new { id = token.Id, name = token.Name, symbol = token.Symbol, decimals = token.Decimals };
        private static long ParseAmount(string text, int decimals)
        { long amount; if (!CreateTokenViewModel.TryParseSupply(text, decimals, out amount))
                throw new TokenOperationException("amount_invalid", "Informe uma quantidade positiva com a precisão do ativo."); return amount; }
        private static string Atomic(long value) => value.ToString(CultureInfo.InvariantCulture);

        private sealed class PreparedExchange
        {
            public UnsignedTokenExchange Transaction;
            public string Operation, Owner, ProviderKey, PoolId, ReserveId, Address;
            public TokenDefinition AssetA, AssetB;
            public long ReserveA, ReserveB, Amount, MinimumReceive;
            public int InputSide;
            public DateTime ExpiresUtc;
        }
        private sealed class PoolSnapshot
        {
            public string Id, ProviderKey, Status, StatusReason, StatusMessage, BlockingTransactionId;
            public Transaction Transaction;
            public TokenDefinition AssetA, AssetB;
            public UnspentOutput ReserveA, ReserveB;
            public bool Available, Locked, CanManage;
        }
        [DataContract]
        private sealed class ExchangeOperation
        {
            [DataMember] public string Operation { get; set; }
            [DataMember] public string PoolId { get; set; }
            [DataMember] public string PreviousReserveId { get; set; }
            [DataMember] public string ProviderKey { get; set; }
            [DataMember] public TokenDefinition AssetA { get; set; }
            [DataMember] public TokenDefinition AssetB { get; set; }
        }
    }
}
