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

            routes.MapRoute("CreateToken", "tokens/criar", new { controller = "Tokens", action = "Create" });
            routes.MapRoute("TransferToken", "tokens/movimentar/{tokenId}", new { controller = "TokenTransfers", action = "Index", tokenId = UrlParameter.Optional });
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
