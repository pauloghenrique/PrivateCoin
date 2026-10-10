using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Povix.Dex.Models;
using PrivateCoin.Core;

namespace Povix.Dex.Services
{
    public sealed partial class TokenNetworkService
    {
        private readonly Dictionary<string, PreparedTransfer> transferDrafts = new Dictionary<string, PreparedTransfer>();

        public object GetCreatedTokens(string[] publicKeys)
        {
            lock (sync)
            {
                var keys = new HashSet<string>(publicKeys, StringComparer.Ordinal);
                var addresses = new HashSet<string>(publicKeys.Select(AddressFor), StringComparer.Ordinal);
                var creations = blockchain.Blocks.SelectMany(block => block.Transactions).Where(tx => tx.Kind == TransactionKind.TokenCreate &&
                    keys.Contains(tx.Inputs[0].PublicKey)).ToArray();
                var receivingAddresses = creations.SelectMany(tx => tx.Outputs.Where(output => output.AssetId == tx.Token.Id))
                    .Select(output => output.OneTimeAddress);
                // Query registered outputs once; missing receiving keys are diagnostic only.
                // An issuance to another address must never count as this wallet's spendable balance.
                blockchain.ValidatePendingTransactions(pending);
                var knownOutputs = blockchain.GetRegisteredTokenOutputs(addresses.Concat(receivingAddresses).Distinct(StringComparer.Ordinal), pending)
                    .Where(item => item.Output.AssetId != null).ToArray();
                var outputs = knownOutputs.Where(item => addresses.Contains(item.Output.OneTimeAddress)).ToArray();
                var receivingBalances = knownOutputs.GroupBy(item => item.Output.AssetId + ":" + item.Output.OneTimeAddress, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.Output.Amount), StringComparer.Ordinal);
                var reserved = new HashSet<string>(pending.SelectMany(tx => tx.Inputs).Select(input =>
                    input.TransactionId + ":" + input.OutputIndex.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal);
                var confirmed = outputs.GroupBy(item => item.Output.AssetId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(item => item.Output.Amount), StringComparer.Ordinal);
                var available = outputs.Where(item => !reserved.Contains(item.TransactionId + ":" + item.OutputIndex.ToString(CultureInfo.InvariantCulture)))
                    .GroupBy(item => item.Output.AssetId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(item => item.Output.Amount), StringComparer.Ordinal);
                return creations.Select(tx => new {
                        tokenId = tx.Token.Id, name = tx.Token.Name, symbol = tx.Token.Symbol, decimals = tx.Token.Decimals,
                        supplyAtomic = tx.Token.Supply.ToString(CultureInfo.InvariantCulture), creationTransactionId = tx.Id,
                        creatorAddress = AddressFor(tx.Inputs[0].PublicKey),
                        receivingAddresses = tx.Outputs.Where(output => output.AssetId == tx.Token.Id)
                            .Select(output => output.OneTimeAddress).Distinct(StringComparer.Ordinal).Select(address => new {
                                address, owned = addresses.Contains(address),
                                confirmedAtomic = (receivingBalances.ContainsKey(tx.Token.Id + ":" + address) ? receivingBalances[tx.Token.Id + ":" + address] : 0).ToString(CultureInfo.InvariantCulture)
                            }).ToArray(),
                        balanceAtomic = (available.ContainsKey(tx.Token.Id) ? available[tx.Token.Id] : 0).ToString(CultureInfo.InvariantCulture),
                        confirmedAtomic = (confirmed.ContainsKey(tx.Token.Id) ? confirmed[tx.Token.Id] : 0).ToString(CultureInfo.InvariantCulture)
                    }).ToArray();
            }
        }

        public object PrepareTransfer(TokenTransferViewModel model, string[] publicKeys, string changeAddress, string owner)
        {
            lock (sync)
            {
                RequireReady();
                foreach (string id in transferDrafts.Where(item => item.Value.ExpiresUtc <= DateTime.UtcNow).Select(item => item.Key).ToArray()) transferDrafts.Remove(id);
                foreach (string id in drafts.Where(item => item.Value.ExpiresUtc <= DateTime.UtcNow).Select(item => item.Key).ToArray()) drafts.Remove(id);
                if (drafts.Count + transferDrafts.Count >= 200 || drafts.Values.Count(item => item.Owner == owner) + transferDrafts.Values.Count(item => item.Owner == owner) >= 5)
                    throw new TokenOperationException("draft_limit", "Há preparações em andamento. Aguarde alguns minutos e tente novamente.");
                Transaction creation = FindCreation(model.TokenId);
                if (creation == null) throw new TokenOperationException("token_unconfirmed", "O token precisa ter sua criação confirmada em bloco.");
                string creatorKey = creation.Inputs[0].PublicKey;
                if (!publicKeys.Contains(creatorKey, StringComparer.Ordinal))
                    throw new TokenOperationException("creator_required", "Somente a carteira que criou este token pode movimentá-lo nesta tela.");
                long amount;
                if (!CreateTokenViewModel.TryParseSupply(model.Amount, creation.Token.Decimals, out amount))
                    throw new TokenOperationException("amount_invalid", "Confira a quantidade e as casas decimais do token.");
                var transfer = TokenTransfer.Prepare(blockchain, pending, publicKeys, model.TokenId, model.DestinationAddress,
                    amount, changeAddress, GetSelectedFee(model.FeePriority, model.FeeAtomic));
                if (transfer.Inputs.Count > 1000) throw new TokenOperationException("inputs_limit", "Esta movimentação precisa de mais entradas do que a tela permite. Tente uma quantidade menor.");
                string draftId = Guid.NewGuid().ToString("N");
                var draft = new PreparedTransfer { Transfer = transfer, Owner = owner, CreatorKey = creatorKey, ExpiresUtc = DateTime.UtcNow.AddMinutes(15) };
                transferDrafts.Add(draftId, draft);
                string payload = Convert.ToBase64String(transfer.SigningPayload);
                return new { draftId, signingPayload = payload, inputAddresses = transfer.InputAddresses,
                    inputs = transfer.Inputs.Select((input, index) => new { transactionId = input.TransactionId, outputIndex = input.OutputIndex, address = transfer.InputAddresses[index] }).ToArray(),
                    tokenId = transfer.TokenId, name = creation.Token.Name, symbol = creation.Token.Symbol, decimals = creation.Token.Decimals,
                    amountAtomic = transfer.Amount.ToString(CultureInfo.InvariantCulture), destinationAddress = transfer.DestinationAddress,
                    feeAtomic = transfer.Fee.ToString(CultureInfo.InvariantCulture), changeAddress = transfer.ChangeAddress,
                    tokenChangeAtomic = transfer.TokenChangeAmount.ToString(CultureInfo.InvariantCulture), povixChangeAtomic = transfer.PovixChangeAmount.ToString(CultureInfo.InvariantCulture),
                    timestampUtcTicks = transfer.TimestampUtcTicks.ToString(CultureInfo.InvariantCulture), networkId = Blockchain.NetworkId, consensusVersion = Blockchain.ConsensusVersion,
                    creatorAddress = AddressFor(creatorKey), authorizationPayload = Convert.ToBase64String(AuthorizationPayload(draftId, transfer)),
                    expiresUtc = draft.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture) };
            }
        }

        public async Task<string> SubmitTransferAsync(string draftId, string[] signatures, string creatorSignature, string owner)
        {
            Transaction transaction;
            lock (sync)
            {
                SubmittedToken prior = submitted.FirstOrDefault(item => item.DraftId == draftId && item.Transaction.Kind == TransactionKind.TokenTransfer);
                if (prior != null) return prior.Transaction.Id;
                RequireReady();
                PreparedTransfer draft;
                if (draftId == null || !transferDrafts.TryGetValue(draftId, out draft)) throw new TokenOperationException("draft_missing", "A preparação não está mais disponível. Prepare novamente.");
                if (draft.Owner != owner) throw new TokenOperationException("draft_session_changed", "A sessão mudou. Prepare novamente na mesma janela.");
                if (draft.ExpiresUtc <= DateTime.UtcNow) throw new TokenOperationException("draft_expired", "A preparação expirou após 15 minutos. Prepare novamente.");
                if (pending.Count >= MaximumPending) throw new TokenOperationException("pending_limit", "A fila está cheia. Aguarde e tente o mesmo envio novamente.", true);
                var creation = FindCreation(draft.Transfer.TokenId);
                if (creation == null || creation.Inputs[0].PublicKey != draft.CreatorKey) throw new TokenOperationException("creator_changed", "A criação deste token mudou na cadeia. Atualize a tela e prepare novamente.");
                bool authorized = false;
                try
                {
                    using (var rsa = new RSACryptoServiceProvider())
                    {
                        rsa.PersistKeyInCsp = false; rsa.FromXmlString(draft.CreatorKey);
                        authorized = rsa.VerifyData(AuthorizationPayload(draftId, draft.Transfer), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(creatorSignature ?? ""));
                    }
                }
                catch (Exception error) when (error is FormatException || error is CryptographicException) { }
                if (!authorized) throw new TokenOperationException("creator_signature_invalid", "A assinatura do criador não autoriza esta movimentação.");
                if (signatures == null || signatures.Length != draft.Transfer.Inputs.Count || signatures.Any(string.IsNullOrWhiteSpace))
                    throw new TokenOperationException("signatures_invalid", "É necessária uma assinatura para cada entrada da transação.");
                transaction = draft.Transfer.Complete(signatures);
                try { blockchain.ValidatePendingTransactions(pending.Concat(new[] { transaction })); }
                catch (Exception error) when (IsInvalidData(error))
                {
                    if (error.Message == "Missing or already spent input.") throw new TokenOperationException("funding_unavailable", "O saldo do token ou dos POVIX foi usado ou reservado. Atualize e prepare novamente.");
                    if (error.Message == "Invalid signature." || error is CryptographicException || error is FormatException)
                        throw new TokenOperationException("signature_invalid", "A assinatura não corresponde à movimentação preparada.");
                    throw new TokenOperationException("transaction_invalid", "A blockchain rejeitou a movimentação. Atualize a tela e prepare novamente.");
                }
                var nextPending = pending.Concat(new[] { transaction }).ToList();
                var nextSubmitted = submitted.Concat(new[] { new SubmittedToken { DraftId = draftId, Transaction = transaction,
                    Token = new TokenDefinition { Id = creation.Token.Id, Name = creation.Token.Name, Symbol = creation.Token.Symbol,
                        Decimals = creation.Token.Decimals, Supply = creation.Token.Supply }, CreatorKey = draft.CreatorKey, CreatorSignature = creatorSignature } }).ToList();
                Save(blockchain, nextPending, nextSubmitted);
                pending = nextPending; submitted = nextSubmitted; transferDrafts.Remove(draftId);
            }
            await Relay(transaction).ConfigureAwait(false);
            return transaction.Id;
        }

        public TokenTransferReceiptViewModel GetTransferReceipt(string id)
        {
            lock (sync)
            {
                Block block = blockchain.Blocks.FirstOrDefault(item => item.Transactions.Any(tx => tx.Id == id));
                var record = submitted.FirstOrDefault(item => item.Transaction.Id == id);
                var transaction = block?.Transactions.FirstOrDefault(tx => tx.Id == id) ??
                    pending.FirstOrDefault(tx => tx.Id == id) ?? record?.Transaction;
                if (record == null || transaction == null || transaction.Kind != TransactionKind.TokenTransfer) return null;
                var destination = transaction.Outputs[0];
                var token = FindCreation(destination.AssetId)?.Token ?? record.Token;
                if (token == null) return null;
                return new TokenTransferReceiptViewModel { TransactionId = id, Token = token, Amount = destination.Amount, Fee = transaction.Fee,
                    DestinationAddress = destination.OneTimeAddress, ChangeOutputs = transaction.Outputs.Skip(1).Select(output =>
                        new TransactionOutput { Amount = output.Amount, AssetId = output.AssetId, OneTimeAddress = output.OneTimeAddress }).ToArray(),
                    Status = block != null ? "confirmed" : pending.Any(tx => tx.Id == id) ?
                        blockchain.HasValidTransactionApproval(transaction, pending) ? "validated" : "pending" : "rejected",
                    ValidationCount = (block != null || pending.Any(tx => tx.Id == id) && blockchain.HasValidTransactionApproval(transaction, pending)) &&
                        (transaction.TransactionApproval != null || block?.TransactionValidations?.Any(proof => proof.TransactionId == id) == true) ? 1 : 0, BlockHeight = block?.Height,
                    BlockHash = block?.Hash, Confirmations = block == null ? 0 : blockchain.Blocks.Last().Height - block.Height + 1, PeerCount = node.ConnectedPeerCount };
            }
        }

        public object GetTransferHistory(string[] publicKeys, string tokenId)
        {
            lock (sync)
            {
                var creation = FindCreation(tokenId);
                if (creation == null || !publicKeys.Contains(creation.Inputs[0].PublicKey, StringComparer.Ordinal)) return new object[0];
                return submitted.Where(item => item.Transaction.Kind == TransactionKind.TokenTransfer && item.Transaction.Outputs[0].AssetId == tokenId)
                    .OrderByDescending(item => item.Transaction.TimestampUtcTicks).Take(20).Select(item => {
                        var receipt = GetTransferReceipt(item.Transaction.Id);
                        return new { transactionId = receipt.TransactionId, amountAtomic = receipt.Amount.ToString(CultureInfo.InvariantCulture),
                            destinationAddress = receipt.DestinationAddress, status = receipt.Status, confirmations = receipt.Confirmations };
                    }).ToArray();
            }
        }

        private Transaction FindCreation(string tokenId) => blockchain.Blocks.SelectMany(block => block.Transactions)
            .FirstOrDefault(tx => tx.Kind == TransactionKind.TokenCreate && tx.Token.Id == tokenId);
        private static string AddressFor(string publicKey)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(publicKey))).Replace("-", "").ToLowerInvariant();
        }
        private static byte[] AuthorizationPayload(string draftId, UnsignedTokenTransfer transfer) => Encoding.UTF8.GetBytes(
            "povix-dex-token-transfer-authorize-v1|" + Blockchain.NetworkId + "|" + draftId + "|" + transfer.TokenId + "|" + Convert.ToBase64String(transfer.SigningPayload));

        private sealed class PreparedTransfer
        {
            public UnsignedTokenTransfer Transfer { get; set; }
            public string CreatorKey { get; set; }
            public string Owner { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }
    }
}
