using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using System.Configuration;
using System.Diagnostics;
using Povix.Dex.Services;

namespace Povix.Dex
{
    public class MvcApplication : System.Web.HttpApplication
    {
        public static TokenNetworkService TokenNetwork { get; private set; }

        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
            try
            {
                int port;
                if (!int.TryParse(ConfigurationManager.AppSettings["DexListenPort"], out port)) port = 4780;
                string seeds = Environment.GetEnvironmentVariable("POVIX_PEERS") ?? ConfigurationManager.AppSettings["PeerSeeds"] ?? string.Empty;
                TokenNetwork = new TokenNetworkService(Server.MapPath("~/App_Data"), port,
                    seeds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()));
            }
            catch (Exception error)
            {
                // Fail closed: no blank replacement of an invalid cache and no fabricated registration.
                Trace.TraceError("DEX: o nó não pôde iniciar ({0}).", error.GetType().Name);
            }
        }

        protected void Application_End() { TokenNetwork?.Dispose(); }
    }
}
