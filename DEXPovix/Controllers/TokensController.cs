using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Mvc;
using DEXPovix.Models;

namespace DEXPovix.Controllers
{
    public class TokensController : Controller
    {
        [HttpGet]
        public ActionResult Index() => View(MvcApplication.TokenNetwork.GetDashboard());

        [HttpGet, OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public ActionResult List() => Json(MvcApplication.TokenNetwork.GetDashboard(), JsonRequestBehavior.AllowGet);

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Prepare(PrepareTokenRequest request)
        {
            try { return Json(MvcApplication.TokenNetwork.Prepare(request)); }
            catch (Exception error) when (IsRequestError(error)) { return BadRequest(error); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<ActionResult> Submit(SubmitTokenRequest request)
        {
            try { return Json(new { TransactionId = await MvcApplication.TokenNetwork.SubmitAsync(request), Status = "Pendente" }); }
            catch (Exception error) when (IsRequestError(error)) { return BadRequest(error); }
        }

        private static bool IsRequestError(Exception error) => error is ArgumentException ||
            error is InvalidOperationException || error is FormatException || error is OverflowException || error is CryptographicException;

        private ActionResult BadRequest(Exception error)
        {
            Response.StatusCode = 400;
            Response.TrySkipIisCustomErrors = true;
            return Json(new { Error = error is CryptographicException ? "Chave ou assinatura inválida." : error.Message });
        }
    }
}
