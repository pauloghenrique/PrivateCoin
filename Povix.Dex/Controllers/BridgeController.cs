using System;
using System.Configuration;
using System.Text.RegularExpressions;
using System.Web.Mvc;
using PrivateCoin.Core;

namespace Povix.Dex.Controllers
{
    public sealed class BridgeController : Controller
    {
        [HttpGet]
        public ActionResult Index() { return View(); }

        [HttpGet]
        public ActionResult Configuration()
        {
            Response.Cache.SetNoStore();
            var asset = ConfigurationManager.AppSettings["BridgeTestAsset"] ?? "";
            var vault = ConfigurationManager.AppSettings["BridgeTestVault"] ?? "";
            var address = new Regex("^0x[0-9a-fA-F]{40}$");
            bool enabled = ConfigurationManager.AppSettings["BridgeTestEnabled"] == "true"
                && address.IsMatch(asset) && address.IsMatch(vault)
                && asset != "0x0000000000000000000000000000000000000000"
                && vault != "0x0000000000000000000000000000000000000000"
                && !String.Equals(asset, vault, StringComparison.OrdinalIgnoreCase);
            return Json(new { enabled, chainId = 97, confirmations = 15,
                asset = enabled ? asset : null, vault = enabled ? vault : null,
                network = Blockchain.NetworkId, token = InitialLaunch.PusdtTokenId }, JsonRequestBehavior.AllowGet);
        }
    }
}
