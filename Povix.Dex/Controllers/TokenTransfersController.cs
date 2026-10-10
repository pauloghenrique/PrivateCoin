using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Povix.Dex.Models;
using Povix.Dex.Services;

namespace Povix.Dex.Controllers
{
    public sealed class TokenTransfersController : Controller
    {
        [HttpGet]
        public ActionResult Index(string tokenId)
        {
            NoCache();
            return View(new TokenTransferViewModel { TokenId = ValidId(tokenId) ? tokenId : null,
                Network = MvcApplication.TokenNetwork?.GetNetwork() ?? new TokenNetworkViewModel() });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult WalletTokens(string[] publicKeys)
        {
            NoCache();
            if (MvcApplication.TokenNetwork == null) return Error("A conexão com a rede está indisponível.", 503, "network_unavailable");
            try { return Json(MvcApplication.TokenNetwork.GetCreatedTokens(TokensController.DecodePublicKeys(publicKeys))); }
            catch (Exception error) when (TokenNetworkService.IsInvalidData(error)) { return Error("As chaves públicas da carteira são inválidas.", 400, "wallet_invalid"); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult History(string[] publicKeys, string tokenId)
        {
            NoCache();
            if (!ValidId(tokenId)) return Error("Identificador do token inválido.", 400, "token_invalid");
            if (MvcApplication.TokenNetwork == null) return Error("A conexão com a rede está indisponível.", 503, "network_unavailable");
            try { return Json(MvcApplication.TokenNetwork.GetTransferHistory(TokensController.DecodePublicKeys(publicKeys), tokenId)); }
            catch (Exception error) when (TokenNetworkService.IsInvalidData(error)) { return Error("Não foi possível consultar as movimentações.", 400, "wallet_invalid"); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Prepare(TokenTransferViewModel model, string[] publicKeys, string changeAddress)
        {
            NoCache();
            if (!ModelState.IsValid) return Error("Confira o token, a quantidade, o destino e a prioridade da taxa.", 400, "fields_invalid");
            if (MvcApplication.TokenNetwork == null) return Error("A conexão com a rede está indisponível.", 503, "network_unavailable");
            try
            {
                var keys = TokensController.DecodePublicKeys(publicKeys);
                Session["Povix.Dex.TokenDraftSession"] = true;
                return Json(MvcApplication.TokenNetwork.PrepareTransfer(model, keys, changeAddress, Session.SessionID));
            }
            catch (TokenOperationException error) { return Error(error.Message, error.Retryable ? 503 : 400, error.Code); }
            catch (Exception error) when (TokenNetworkService.IsInvalidData(error))
            {
                string message = error.Message == "Insufficient token balance." ? "A carteira não tem a quantidade confirmada disponível deste token." :
                    error.Message == "Insufficient POVIX for the transaction fee." ? "A carteira não tem POVIX confirmado disponível para a taxa." : "Não foi possível preparar a movimentação. Confira a carteira e os campos.";
                return Error(message, 400, "preparation_invalid");
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<ActionResult> Submit(string draftId, string[] signatures, string creatorSignature)
        {
            NoCache();
            if (draftId == null || !Regex.IsMatch(draftId, @"\A[0-9a-f]{32}\z") || signatures == null || signatures.Length == 0 ||
                signatures.Length > 1000 || signatures.Any(value => !ValidSignature(value)) || !ValidSignature(creatorSignature))
                return Error("São necessárias as assinaturas das entradas e a autorização do criador.", 400, "signatures_invalid");
            if (MvcApplication.TokenNetwork == null) return Error("A conexão com a rede está indisponível.", 503, "network_unavailable");
            try
            {
                string id = await MvcApplication.TokenNetwork.SubmitTransferAsync(draftId, signatures, creatorSignature, Session.SessionID);
                return Json(new { transactionId = id, receiptUrl = Url.Action("Details", new { id }) });
            }
            catch (TokenOperationException error) { return Error(error.Message, error.Retryable ? 503 : 400, error.Code); }
            catch (Exception error) when (TokenNetworkService.IsInvalidData(error)) { return Error("Não foi possível validar a movimentação. Prepare novamente.", 400, "submission_invalid"); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return Error("Não foi possível salvar a transação. Tente novamente.", 503, "persistence_unavailable"); }
        }

        [HttpGet]
        public ActionResult Details(string id)
        {
            NoCache();
            var receipt = ValidId(id) ? MvcApplication.TokenNetwork?.GetTransferReceipt(id) : null;
            return receipt == null ? (ActionResult)HttpNotFound() : View(receipt);
        }

        [HttpGet]
        public ActionResult Status(string id)
        {
            NoCache();
            var receipt = ValidId(id) ? MvcApplication.TokenNetwork?.GetTransferReceipt(id) : null;
            if (receipt == null) return HttpNotFound();
            return Json(new { status = receipt.Status, blockHeight = receipt.BlockHeight, blockHash = receipt.BlockHash,
                confirmations = receipt.Confirmations, validations = receipt.ValidationCount, peerCount = receipt.PeerCount }, JsonRequestBehavior.AllowGet);
        }

        private ActionResult Error(string message, int status, string code)
        { Response.StatusCode = status; Response.TrySkipIisCustomErrors = true; return Json(new { error = message, code }); }
        private void NoCache() { Response.Cache.SetCacheability(HttpCacheability.NoCache); Response.Cache.SetNoStore(); }
        private static bool ValidId(string value) => value != null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
        private static bool ValidSignature(string value) => value != null && value.Length == 344 && Regex.IsMatch(value, @"\A[A-Za-z0-9+/]+={0,2}\z");
    }
}
