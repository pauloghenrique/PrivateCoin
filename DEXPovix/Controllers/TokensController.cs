using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Mvc;
using DEXPovix.Models;
using DEXPovix.Services;

namespace DEXPovix.Controllers
{
    public sealed class TokensController : Controller
    {
        [HttpGet]
        public ActionResult Create() { return View(new CreateTokenViewModel { Decimals = 8 }); }
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<ActionResult> Create(CreateTokenViewModel model)
        {
            Response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);
            if (!ModelState.IsValid) return View(model);
            try
            {
                var tx = TokenSubmission.Parse(model);
                await MvcApplication.TokenNetwork.SubmitAsync(tx);
                return RedirectToAction("Status", new { id = tx.Id });
            }
            catch (InvalidOperationException)
            { ModelState.AddModelError("", "Não foi possível enviar: verifique a assinatura, os dados, o saldo POVIX para a taxa e a sincronização da rede."); }
            catch (System.Security.Cryptography.CryptographicException)
            { ModelState.AddModelError("", "A assinatura da transação é inválida."); }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException || ex is System.Xml.XmlException)
            { ModelState.AddModelError("", "A transação contém dados inválidos."); }
            return View(model);
        }
        [HttpGet]
        public ActionResult Status(string id)
        {
            if (id == null || !Regex.IsMatch(id, "\\A[0-9a-f]{64}\\z")) return new HttpStatusCodeResult(400);
            Response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);
            ViewBag.TransactionId = id;
            return View();
        }
        [HttpGet]
        public ActionResult TransactionStatus(string id)
        {
            if (id == null || !Regex.IsMatch(id, "\\A[0-9a-f]{64}\\z")) return new HttpStatusCodeResult(400);
            Response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);
            return Json(MvcApplication.TokenNetwork.GetStatus(id), JsonRequestBehavior.AllowGet);
        }
    }
}
