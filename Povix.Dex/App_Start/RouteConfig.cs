using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;

namespace Povix.Dex
{
    public class RouteConfig
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            routes.IgnoreRoute("{resource}.axd/{*pathInfo}");

            routes.MapRoute("BridgeMultiSwap", "swap-bnb", new { controller = "Bridge", action = "Index" });
            routes.MapRoute("BridgeSwap", "swap-usdt", new { controller = "Bridge", action = "Index" });

            routes.MapRoute("TokenList", "tokens", new { controller = "Tokens", action = "Index" });

            routes.MapRoute("EditToken", "tokens/editar/{tokenId}", new { controller = "TokenProfiles", action = "Index", tokenId = UrlParameter.Optional });
            routes.MapRoute("CreateToken", "tokens/criar", new { controller = "Tokens", action = "Create" });
            routes.MapRoute("CreateLaunchPool", "liquidez/lancamento/criar", new { controller = "Liquidity", action = "CreateLaunch" });
            routes.MapRoute("CreatePool", "liquidez/criar", new { controller = "Liquidity", action = "Create" });
            routes.MapRoute("Liquidity", "liquidez", new { controller = "Liquidity", action = "Index" });
            routes.MapRoute("LiquidityReceipt", "liquidez/operacao/{id}", new { controller = "Liquidity", action = "Details" });
            routes.MapRoute("TransferReceipt", "tokens/movimentacao/{id}", new { controller = "TokenTransfers", action = "Details" });
            routes.MapRoute("TokenRegistration", "tokens/registro/{id}", new { controller = "Tokens", action = "Details" });

            routes.MapRoute(
                name: "Default",
                url: "{controller}/{action}/{id}",
                defaults: new { controller = "Home", action = "Index", id = UrlParameter.Optional }
            );
        }
    }
}
