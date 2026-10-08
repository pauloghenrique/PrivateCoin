using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Povix.Dex.Models;

namespace Povix.Dex.Controllers
{
    public sealed class TokensController : Controller
    {
        [HttpGet]
        public ActionResult Create()
        {
            NoCache();
            return View(new CreateTokenViewModel { Network = Network() });
        }

        [HttpGet]
        public ActionResult NetworkStatus()
        {
            NoCache();
            return Json(Network(), JsonRequestBehavior.AllowGet);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Balance(string[] addresses)
        {
            NoCache();
            if (!ValidAddresses(addresses)) return Error("Endereços da carteira inválidos.", 400);
            if (MvcApplication.TokenNetwork == null) return Error("A conexão com a rede está indisponível.", 503);
            return Json(new { balanceAtomic = MvcApplication.TokenNetwork.GetBalance(addresses).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public ActionResult Prepare(CreateTokenViewModel model, string[] publicKeys, string changeAddress)
        {
            NoCache();
            if (!ModelState.IsValid) return Error(string.Join(" ", ModelState.Values.SelectMany(value => value.Errors)
                .Select(error => string.IsNullOrEmpty(error.ErrorMessage) ? "Confira os campos informados." : error.ErrorMessage)), 400);
            if (MvcApplication.TokenNetwork == null) return Error("A conexão com a rede está indisponível.", 503);
            try
            {
                string[] keys = DecodePublicKeys(publicKeys);
                return Json(MvcApplication.TokenNetwork.Prepare(model, keys, changeAddress, Session.SessionID));
            }
            catch (Exception error) when (Services.TokenNetworkService.IsInvalidData(error))
            { return Error(error.Message == "Insufficient POVIX for the transaction fee." ? "A carteira não tem POVIX confirmado disponível para pagar a taxa." : "Não foi possível preparar a criação. Confira a carteira, os campos e a conexão com a rede.", 400); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<ActionResult> Submit(string draftId, string[] signatures)
        {
            NoCache();
            if (draftId == null || !Regex.IsMatch(draftId, @"\A[0-9a-f]{32}\z") || signatures == null ||
                signatures.Length == 0 || signatures.Length > 1000 || signatures.Any(value => value == null ||
                    value.Length != 344 || !Regex.IsMatch(value, @"\A[A-Za-z0-9+/]+={0,2}\z")))
                return Error("Assinaturas inválidas.", 400);
            if (MvcApplication.TokenNetwork == null) return Error("A conexão com a rede está indisponível.", 503);
            try
            {
                string id = await MvcApplication.TokenNetwork.SubmitAsync(draftId, signatures, Session.SessionID);
                return Json(new { transactionId = id, receiptUrl = Url.Action("Details", new { id }) });
            }
            catch (Exception error) when (Services.TokenNetworkService.IsInvalidData(error))
            { return Error("A transação não foi aceita. A preparação pode ter expirado ou o saldo foi usado. Prepare novamente; nenhuma confirmação foi registrada.", 400); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { return Error("Não foi possível salvar a transação. Tente novamente.", 503); }
        }

        [HttpGet]
        public ActionResult Details(string id)
        {
            NoCache();
            var registration = ValidId(id) ? MvcApplication.TokenNetwork?.GetRegistration(id) : null;
            return registration == null ? (ActionResult)HttpNotFound() : View(registration);
        }

        [HttpGet]
        public ActionResult RegistrationStatus(string id)
        {
            NoCache();
            var registration = ValidId(id) ? MvcApplication.TokenNetwork?.GetRegistration(id) : null;
            if (registration == null) return HttpNotFound();
            return Json(new { status = registration.Status, blockHeight = registration.BlockHeight,
                blockHash = registration.BlockHash, confirmations = registration.Confirmations,
                peerCount = registration.PeerCount }, JsonRequestBehavior.AllowGet);
        }

        private TokenNetworkViewModel Network() => MvcApplication.TokenNetwork?.GetNetwork() ??
            new TokenNetworkViewModel { Error = "A conexão com a blockchain está indisponível. Verifique a configuração do nó." };

        private ActionResult Error(string message, int status)
        {
            Response.StatusCode = status;
            Response.TrySkipIisCustomErrors = true;
            return Json(new { error = message });
        }

        private void NoCache() { Response.Cache.SetCacheability(HttpCacheability.NoCache); Response.Cache.SetNoStore(); }
        private static bool ValidId(string value) => value != null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
        private static bool ValidAddresses(string[] values) => values != null && values.Length > 0 && values.Length <= 1000 && values.All(ValidId);

        // Public XML is transported as Base64 to avoid ASP.NET's HTML request validation.
        // Exact canonical matching excludes private parameters before RSA import.
        private static string[] DecodePublicKeys(string[] values)
        {
            if (values == null || values.Length == 0 || values.Length > 1000) throw new ArgumentException("Invalid public keys.");
            return values.Select(value =>
            {
                if (value == null || value.Length > 1024) throw new ArgumentException("Invalid public key.");
                string xml = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                if (!Regex.IsMatch(xml, @"\A<RSAKeyValue><Modulus>[A-Za-z0-9+/=]+</Modulus><Exponent>[A-Za-z0-9+/=]+</Exponent></RSAKeyValue>\z"))
                    throw new ArgumentException("Only public RSA parameters are allowed.");
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(xml);
                    if (rsa.KeySize != 2048 || rsa.ToXmlString(false) != xml) throw new ArgumentException("Invalid public key.");
                }
                return xml;
            }).ToArray();
        }
    }
}
