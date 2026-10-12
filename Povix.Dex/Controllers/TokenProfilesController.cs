using System;
using System.IO;
using System.Web;
using System.Web.Mvc;
using Povix.Dex.Models;
using Povix.Dex.Services;

namespace Povix.Dex.Controllers
{
    public sealed class TokenProfilesController : Controller
    {
        [HttpGet] public ActionResult Index(string tokenId)
        { NoCache(); ViewBag.TokenId = tokenId; return View(); }

        [HttpPost, ValidateAntiForgeryToken] public ActionResult WalletTokens(string[] publicKeys)
        { return Execute(() => MvcApplication.TokenNetwork.GetTokenProfiles(TokensController.DecodePublicKeys(publicKeys))); }

        [HttpPost, ValidateAntiForgeryToken] public ActionResult Prepare(EditTokenViewModel model, string[] publicKeys)
        {
            if (!ModelState.IsValid) { NoCache(); return Error("Confira os campos do perfil.", 400, "fields_invalid"); }
            return Execute(() => {
                Session["Povix.Dex.TokenDraftSession"] = true;
                return MvcApplication.TokenNetwork.PrepareProfile(model, TokensController.DecodePublicKeys(publicKeys), Session.SessionID);
            });
        }

        [HttpPost, ValidateAntiForgeryToken] public ActionResult Submit(string draftId, string signature)
        {
            return Execute(() => {
                if (signature == null || signature.Length != 344) throw new TokenOperationException("signatures_invalid", "Assinatura inválida.");
                MvcApplication.TokenNetwork.SubmitProfile(draftId, signature, Session.SessionID);
                return new { saved = true };
            });
        }
        private ActionResult Execute(Func<object> action)
        {
            NoCache();
            if (MvcApplication.TokenNetwork == null) return Error("A rede está indisponível.", 503, "network_unavailable");
            try { return Json(action()); }
            catch (TokenOperationException error) { return Error(error.Message, error.Retryable ? 503 : 400, error.Code); }
            catch (Exception error) when (TokenNetworkService.IsInvalidData(error)) { return Error("Dados inválidos. Atualize e prepare novamente.", 400, "profile_invalid"); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return Error("Não foi possível salvar o perfil. Tente o mesmo envio novamente.", 503, "persistence_unavailable"); }
        }
        private ActionResult Error(string message, int status, string code)
        { Response.StatusCode = status; Response.TrySkipIisCustomErrors = true; return Json(new { error = message, code }); }
        private void NoCache() { Response.Cache.SetCacheability(HttpCacheability.NoCache); Response.Cache.SetNoStore(); }
    }
}
