using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PrivateCoin.Core;

namespace Povix.Dex.Services
{
    public sealed partial class TokenNetworkService
    {
        private sealed class ManagementDraft
        {
            public UnsignedTokenManagement Operation;
            public string Owner;
            public DateTime Expires;
        }
        private readonly Dictionary<string, ManagementDraft> managementDrafts = new Dictionary<string, ManagementDraft>();
        public object GetManagementTokens()
        {
            lock (sync) return blockchain.GetTokens().Where(token => token.Policy != null).Select(token => {
                var state = blockchain.GetTokenRuntime(token.Id); var policy = token.Policy;
                return new { tokenId = token.Id, name = token.Name, symbol = token.Symbol, decimals = token.Decimals,
                    supplyAtomic = state.Supply.ToString(CultureInfo.InvariantCulture), issuedAtomic = state.Issued.ToString(CultureInfo.InvariantCulture),
                    maximumSupplyAtomic = policy.MaximumSupply.ToString(CultureInfo.InvariantCulture), revision = state.Revision.ToString(CultureInfo.InvariantCulture),
                    automaticBurnBasisPoints = policy.AutoBurnBasisPoints, automaticBurnFixedAtomic = policy.AutoBurnFixedAtomic.ToString(CultureInfo.InvariantCulture),
                    burnedAtomic = (state.Issued - state.Supply).ToString(CultureInfo.InvariantCulture), paused = state.Paused, mintable = policy.Mintable, burnable = policy.Burnable, pausable = policy.Pausable,
                    administrators = policy.Administrators, requiredSignatures = policy.RequiredSignatures };
            }).ToArray();
        }
        public object PrepareManagement(string tokenId, string operation, string amount, string destination,
            int feePriority, string[] publicKeys, string changeAddress, string owner)
        {
            lock (sync)
            {
                RequireReady();
                foreach (var expired in managementDrafts.Where(item => item.Value.Expires <= DateTime.UtcNow).Select(item => item.Key).ToArray()) managementDrafts.Remove(expired);
                if (managementDrafts.Count + drafts.Count + transferDrafts.Count + exchangeDrafts.Count >= 200 ||
                    managementDrafts.Values.Count(item => item.Owner == owner) >= 5) throw new TokenOperationException("draft_limit", "Há preparações demais. Aguarde alguns minutos.");
                var state = blockchain.GetTokenRuntime(tokenId);
                long atomic = 0;
                if (state == null || (operation == "mint" || operation == "burn") && !Models.CreateTokenViewModel.TryParseSupply(amount, state.Token.Decimals, out atomic))
                    throw new TokenOperationException("fields_invalid", "Confira o token e a quantidade.");
                var preparation = TokenManagement.Prepare(blockchain, pending, publicKeys, tokenId, operation, atomic, destination,
                    changeAddress, GetSelectedFee(feePriority, null), PoolInputKeys());
                var tx = preparation.Preview; string draftId = Guid.NewGuid().ToString("N");
                var draft = new ManagementDraft { Operation = preparation, Owner = owner, Expires = DateTime.UtcNow.AddMinutes(15) };
                managementDrafts.Add(draftId, draft);
                return new { draftId, networkId = Blockchain.NetworkId, consensusVersion = Blockchain.ConsensusVersion,
                    tokenId, operation, amountAtomic = atomic.ToString(CultureInfo.InvariantCulture), revision = state.Revision.ToString(CultureInfo.InvariantCulture),
                    timestampUtcTicks = tx.TimestampUtcTicks.ToString(CultureInfo.InvariantCulture), feeAtomic = tx.Fee.ToString(CultureInfo.InvariantCulture),
                    signingPayload = Convert.ToBase64String(preparation.SigningPayload), inputAddresses = preparation.InputAddresses,
                    administratorAddresses = preparation.AdministratorAddresses, administratorPublicKeys = tx.TokenAuthorizations.Select(proof => proof.PublicKey).ToArray(),
                    inputs = tx.Inputs.Select((input, index) => new { transactionId = input.TransactionId, outputIndex = input.OutputIndex, address = preparation.InputAddresses[index] }).ToArray(),
                    outputs = tx.Outputs.Select(output => new { amountAtomic = output.Amount.ToString(CultureInfo.InvariantCulture), address = output.OneTimeAddress, assetId = output.AssetId }).ToArray(),
                    expiresUtc = draft.Expires.ToString("o", CultureInfo.InvariantCulture) };
            }
        }
        public async Task<string> SubmitManagementAsync(string draftId, string[] signatures, string[] administratorSignatures, string owner)
        {
            Transaction tx;
            lock (sync)
            {
                // Idempotency remains bound to the session even after durable acceptance.
                ManagementDraft draft;
                if (draftId == null || !managementDrafts.TryGetValue(draftId, out draft) || draft.Owner != owner)
                    throw new TokenOperationException("draft_missing", "Preparação indisponível nesta sessão. Prepare novamente.");
                var prior = submitted.FirstOrDefault(item => item.DraftId == draftId && item.Transaction.Kind == TransactionKind.TokenManage);
                if (prior != null) return prior.Transaction.Id;
                RequireReady();
                if (draft.Expires <= DateTime.UtcNow) throw new TokenOperationException("draft_expired", "A preparação expirou. Prepare novamente.");
                if (pending.Count >= MaximumPending) throw new TokenOperationException("pending_limit", "A fila está cheia. Tente novamente.", true);
                tx = draft.Operation.Complete(signatures, administratorSignatures);
                blockchain.ValidatePendingTransactions(pending.Concat(new[] { tx }));
                var nextPending = pending.Concat(new[] { tx }).ToList();
                var nextSubmitted = submitted.Concat(new[] { new SubmittedToken { DraftId = draftId, Transaction = tx } }).ToList();
                Save(blockchain, nextPending, nextSubmitted); pending = nextPending; submitted = nextSubmitted;
            }
            await Relay(tx).ConfigureAwait(false); return tx.Id;
        }
        public object GetManagementStatus(string id)
        {
            lock (sync)
            {
                var block = blockchain.Blocks.FirstOrDefault(item => item.Transactions.Any(candidate => candidate.Id == id && candidate.Kind == TransactionKind.TokenManage));
                var tx = block?.Transactions.First(item => item.Id == id) ?? pending.FirstOrDefault(item => item.Id == id && item.Kind == TransactionKind.TokenManage) ??
                    submitted.FirstOrDefault(item => item.Transaction.Id == id && item.Transaction.Kind == TransactionKind.TokenManage)?.Transaction;
                if (tx == null) return null;
                bool queued = pending.Any(item => item.Id == id);
                return new { transactionId = id, tokenId = tx.TokenCommand.TokenId, operation = tx.TokenCommand.Operation,
                    status = block != null ? "confirmed" : !queued ? "removed" : blockchain.HasValidTransactionApproval(tx, pending) ? "validated" : "pending",
                    confirmations = block == null ? 0 : blockchain.Blocks.Count - block.Height, blockHeight = block?.Height, blockHash = block?.Hash };
            }
        }
    }
}
