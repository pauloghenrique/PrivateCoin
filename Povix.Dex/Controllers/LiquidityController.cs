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
    public sealed class LiquidityController : Controller
    {
        [HttpGet]
        public ActionResult Index(string poolId)
        { NoCache(); ViewBag.SelectedPoolId = ValidId(poolId) ? poolId : "";
            return View(MvcApplication.TokenNetwork?.GetNetwork() ?? new TokenNetworkViewModel()); }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Catalogue(string[] publicKeys) => Read(() => MvcApplication.TokenNetwork.GetExchangeCatalogue(
            publicKeys == null || publicKeys.Length == 0 ? new string[0] : TokensController.DecodePublicKeys(publicKeys)));

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult PreparePool(CreatePoolViewModel model, string[] publicKeys, string changeAddress)
        {
            if (!ModelState.IsValid) return Error("Confira os dois ativos, as quantidades e a taxa.", 400, "fields_invalid");
            return Read(() => { Session["Povix.Dex.TokenDraftSession"] = true;
                return MvcApplication.TokenNetwork.PreparePool(model, TokensController.DecodePublicKeys(publicKeys), changeAddress, Session.SessionID); });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult PrepareSwap(SwapViewModel model, string[] publicKeys, string changeAddress)
        {
            if (!ModelState.IsValid) return Error("Confira a pool, a quantidade, o mínimo a receber e a taxa.", 400, "fields_invalid");
            return Read(() => { Session["Povix.Dex.TokenDraftSession"] = true;
                return MvcApplication.TokenNetwork.PrepareSwap(model, TokensController.DecodePublicKeys(publicKeys), changeAddress, Session.SessionID); });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult ProposeSwap(string draftId, string[] signatures)
        {
            if (!ValidDraft(draftId) || !ValidSignatures(signatures)) return Error("Assinaturas inválidas.", 400, "signatures_invalid");
            return Read(() => MvcApplication.TokenNetwork.ProposeSwap(draftId, signatures, Session.SessionID));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Requests(string[] publicKeys) => Read(() => MvcApplication.TokenNetwork.GetSwapRequests(TokensController.DecodePublicKeys(publicKeys)));

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult RequestStatus(string draftId)
        {
            if (!ValidDraft(draftId)) return Error("Solicitação inválida.", 400, "fields_invalid");
            return Read(() => MvcApplication.TokenNetwork.GetSwapRequestStatus(draftId, Session.SessionID));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public Task<ActionResult> SubmitPool(string draftId, string[] signatures) => Submit(draftId, signatures, false);

        [HttpPost, ValidateAntiForgeryToken]
        public Task<ActionResult> SubmitSwap(string draftId, string[] signatures) => Submit(draftId, signatures, true, true);

        [HttpPost, ValidateAntiForgeryToken]
        public Task<ActionResult> AcceptSwap(string draftId, string[] signatures) => Submit(draftId, signatures, true);

        private async Task<ActionResult> Submit(string draftId, string[] signatures, bool swap, bool completeSwap = false)
        {
            NoCache();
            if (!ValidDraft(draftId) || !ValidSignatures(signatures)) return Error("Assinaturas inválidas.", 400, "signatures_invalid");
            if (MvcApplication.TokenNetwork == null) return Error("A rede está indisponível.", 503, "network_unavailable");
            try {
                string id = completeSwap ? await MvcApplication.TokenNetwork.SubmitSwapAsync(draftId, signatures, Session.SessionID) :
                    swap ? await MvcApplication.TokenNetwork.AcceptSwapAsync(draftId, signatures) :
                    await MvcApplication.TokenNetwork.SubmitPoolAsync(draftId, signatures, Session.SessionID);
                return Json(new { transactionId = id, receiptUrl = Url.Action("Details", new { id }) });
            }
            catch (TokenOperationException error) { return Error(error.Message, error.Retryable ? 503 : 400, error.Code); }
            catch (Exception error) when (TokenNetworkService.IsInvalidData(error)) { return Error("A operação foi rejeitada. Prepare novamente.", 400, "submission_invalid"); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return Error("Não foi possível salvar. Repita o mesmo envio.", 503, "persistence_unavailable"); }
        }

        [HttpGet]
        public ActionResult Details(string id)
        {
            NoCache();
            var receipt = ValidId(id) ? MvcApplication.TokenNetwork?.GetExchangeReceipt(id) : null;
            return receipt == null ? (ActionResult)HttpNotFound() : View(receipt);
        }

        [HttpGet]
        public ActionResult Status(string id)
        {
            NoCache();
            var receipt = ValidId(id) ? MvcApplication.TokenNetwork?.GetExchangeReceipt(id) : null;
            return receipt == null ? (ActionResult)HttpNotFound() : Json(new { status = receipt.Status, confirmations = receipt.Confirmations,
                poolAvailable = receipt.PoolAvailable, poolStatusMessage = receipt.PoolStatusMessage,
                validations = receipt.ValidationCount, blockHeight = receipt.BlockHeight, blockHash = receipt.BlockHash, peerCount = receipt.PeerCount }, JsonRequestBehavior.AllowGet);
        }

        private ActionResult Read(Func<object> action)
        {
            NoCache();
            if (MvcApplication.TokenNetwork == null) return Error("A rede está indisponível.", 503, "network_unavailable");
            try { return Json(action()); }
            catch (TokenOperationException error) { return Error(error.Message, error.Retryable ? 503 : 400, error.Code); }
            catch (Exception error) when (TokenNetworkService.IsInvalidData(error)) {
                string message = error.Message == "Insufficient POVIX for the transaction fee." ? "POVIX insuficiente para a operação e a taxa." :
                    error.Message == "Insufficient token balance." ? "Saldo disponível do token insuficiente." :
                    error.Message == "Minimum receive is not met." ? "A cotação não atende ao mínimo a receber. Atualize e revise outra troca." :
                    "Não foi possível preparar a operação. Confira os ativos, os saldos e a carteira.";
                return Error(message, 400, "preparation_invalid");
            }
        }
        private ActionResult Error(string message, int status, string code)
        { Response.StatusCode = status; Response.TrySkipIisCustomErrors = true; return Json(new { error = message, code }); }
        private void NoCache() { Response.Cache.SetCacheability(HttpCacheability.NoCache); Response.Cache.SetNoStore(); }
        private static bool ValidId(string id) => id != null && Regex.IsMatch(id, @"\A[0-9a-f]{64}\z");
        private static bool ValidDraft(string id) => id != null && Regex.IsMatch(id, @"\A[0-9a-f]{32}\z");
        private static bool ValidSignatures(string[] signatures) => signatures != null && signatures.Length > 0 && signatures.Length <= 1000 &&
            signatures.All(value => value != null && value.Length == 344 && Regex.IsMatch(value, @"\A[A-Za-z0-9+/]+={0,2}\z"));
    }
}
