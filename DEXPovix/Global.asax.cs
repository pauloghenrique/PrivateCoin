using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using System.Configuration;
using System.Web.Hosting;
using DEXPovix.Services;

namespace DEXPovix
{
    public class MvcApplication : System.Web.HttpApplication
    {
        internal static TokenNetworkService TokenNetwork { get; private set; }

        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
            int port;
            if (!int.TryParse(ConfigurationManager.AppSettings["ListenPort"], out port)) port = 4781;
            string[] seeds = (ConfigurationManager.AppSettings["PeerSeeds"] ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(seed => seed.Trim()).ToArray();
            TokenNetwork = new TokenNetworkService(port, seeds, HostingEnvironment.MapPath("~/App_Data/DEXBlockchain.json"));
        }

        protected void Application_End() { if (TokenNetwork != null) TokenNetwork.Dispose(); }
    }
}
