using System;
using System.Configuration;
using System.Text.RegularExpressions;
using System.Web.Mvc;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using PrivateCoin.Core;

namespace Povix.Dex.Controllers
{
    public sealed class ApprovedAsset {
        public string asset { get; set; }
        public string vault { get; set; }
        public string assetCodeHash { get; set; }
        public string vaultCodeHash { get; set; }
    }
    public sealed class BridgeController : Controller
    {
        [HttpGet]
        public ActionResult Index() { return View(); }

        [HttpGet]
        public ActionResult Configuration()
        {
            Response.Cache.SetNoStore();
            bool production = ConfigurationManager.AppSettings["BridgeMode"] == "production";
            Func<string, string> setting = name => ConfigurationManager.AppSettings[(production ? "Bridge" : "BridgeTest") + name] ?? "";
            var asset = setting("Asset").ToLowerInvariant(); var vault = setting("Vault").ToLowerInvariant(); var factory = setting("Factory").ToLowerInvariant();
            var assetHash = setting("AssetCodeHash"); var vaultHash = setting("VaultCodeHash"); var factoryHash = setting("FactoryCodeHash");
            var address = new Regex("^0x[0-9a-f]{40}$"); var hash = new Regex("^0x[0-9a-f]{64}$");
            Func<string, bool> validAddress = value => address.IsMatch(value) && value != "0x0000000000000000000000000000000000000000";
            bool enabled = setting("Enabled") == "true" && validAddress(asset) && validAddress(vault) && asset != vault
                && (!production || hash.IsMatch(assetHash) && hash.IsMatch(vaultHash));
            var approved = new ApprovedAsset[0];
            if (production) {
                var path = Server.MapPath("~/App_Data/bridge-assets.json");
                if (System.IO.File.Exists(path)) {
                    try { approved = new JavaScriptSerializer().Deserialize<ApprovedAsset[]>(System.IO.File.ReadAllText(path)) ?? new ApprovedAsset[0]; }
                    catch (ArgumentException) { enabled = false; }
                    if (approved.Any(item => item == null || !validAddress(item.asset ?? "") || !validAddress(item.vault ?? "") ||
                        !hash.IsMatch(item.assetCodeHash ?? "") || !hash.IsMatch(item.vaultCodeHash ?? ""))) { enabled = false; approved = new ApprovedAsset[0]; }
                }
            }
            return Json(new { enabled, chainId = production ? 56 : 97, confirmations = production ? 64 : 15, production,
                asset = enabled ? asset : null, vault = enabled ? vault : null,
                assetCodeHash = production ? assetHash : null, vaultCodeHash = production ? vaultHash : null, factoryCodeHash = production ? factoryHash : null,
                factory = enabled && validAddress(factory) && (!production || hash.IsMatch(factoryHash)) ? factory : null,
                approvedAssets = enabled ? approved : new ApprovedAsset[0],
                network = Blockchain.NetworkId, token = InitialLaunch.PusdtTokenId }, JsonRequestBehavior.AllowGet);
        }
    }
}
