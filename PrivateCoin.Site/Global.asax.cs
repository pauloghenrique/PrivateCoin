using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using PrivateCoin.Site.Services;

namespace PrivateCoin.Site
{
    public class MvcApplication : System.Web.HttpApplication
    {
        internal static PublicNetworkService PublicNetwork { get; private set; }

        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
            PublicNetwork = new PublicNetworkService();
        }

        protected void Application_End()
        {
            if (PublicNetwork != null) PublicNetwork.Dispose();
        }
    }
}
