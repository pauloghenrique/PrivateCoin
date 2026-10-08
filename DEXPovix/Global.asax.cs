using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;

namespace DEXPovix
{
    public class MvcApplication : System.Web.HttpApplication
    {
        public static DEXPovix.Services.TokenNetworkService TokenNetwork { get; private set; }

        protected void Application_End() { if (TokenNetwork != null) TokenNetwork.Dispose(); }

        protected void Application_Start()
        {
            TokenNetwork = new DEXPovix.Services.TokenNetworkService();
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
        }
    }
}
