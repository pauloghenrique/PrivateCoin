using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Mvc;

namespace PrivateCoin.Site.Controllers
{
    // Internal read-only API. Never receives private keys or signs transactions.
    public sealed class SwapNetworkController : Controller
    {
        private bool Allowed()
        {
            string token = Environment.GetEnvironmentVariable("POVIX_SWAP_NETWORK_TOKEN");
            string authorization = Request.Headers["Authorization"] ?? string.Empty;
            if (string.IsNullOrEmpty(token) || token.Length < 32)
            {
                Response.StatusCode = 503;
                return false;
            }
            using (var sha = SHA256.Create())
            {
                byte[] expected = sha.ComputeHash(Encoding.UTF8.GetBytes("Bearer " + token));
                byte[] supplied = sha.ComputeHash(Encoding.UTF8.GetBytes(authorization));
                int difference = 0;
                for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ supplied[i];
                if (difference == 0) return true;
            }
            Response.StatusCode = 401;
            return false;
        }

        [HttpPost]
        public ActionResult Balance(string[] addresses, int confirmations = 12)
        {
            Response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);
            if (!Allowed()) return Json(new { error = "Consulta restrita ao serviço de swap." });
            if (addresses == null || addresses.Length == 0 || addresses.Length > 1000 ||
                addresses.Any(address => address == null || !Regex.IsMatch(address, "^[0-9a-f]{64}$")) ||
                confirmations < 1 || confirmations > 10000)
            {
                Response.StatusCode = 400;
                return Json(new { error = "Consulta inválida." });
            }
            return Json(MvcApplication.PublicNetwork.GetSwapBalance(addresses.Distinct().ToArray(), confirmations));
        }

        [HttpPost]
        public ActionResult Transaction(string tx)
        {
            Response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);
            if (!Allowed()) return Json(new { error = "Consulta restrita ao serviço de swap." });
            if (tx == null || !Regex.IsMatch(tx, "^[0-9a-f]{64}$"))
            {
                Response.StatusCode = 400;
                return Json(new { error = "Identificador inválido." });
            }
            return Json(MvcApplication.PublicNetwork.GetSwapTransaction(tx));
        }
    }
}
