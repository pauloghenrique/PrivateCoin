using System.Web;
using System.Web.Optimization;

namespace Povix.Dex
{
    public class BundleConfig
    {
        // For more information on bundling, visit https://go.microsoft.com/fwlink/?LinkId=301862
        public static void RegisterBundles(BundleCollection bundles)
        {
            // Plain bundles preserve the modern wallet scripts without legacy minification.
            // Scripts.Url versions the combined wallet and page code by their contents,
            // even when ASP.NET compilation debugging is enabled.
            bundles.Add(new Bundle("~/bundles/povix-wallet").Include("~/Scripts/povix-token-wallet.js", "~/Scripts/povix-wallet-menu.js"));
            bundles.Add(new Bundle("~/bundles/povix-token-create").Include(
                "~/Scripts/povix-token-create.js"));
            bundles.Add(new Bundle("~/bundles/povix-token-management").Include("~/Scripts/povix-token-management.js"));
            bundles.Add(new Bundle("~/bundles/povix-token-transfer").Include(
                "~/Scripts/povix-token-transfer.js"));
            bundles.Add(new Bundle("~/bundles/povix-token-profile").Include(
                "~/Scripts/povix-token-profile.js"));
            bundles.Add(new Bundle("~/bundles/povix-liquidity").Include(
                "~/Scripts/povix-liquidity.js"));
            bundles.Add(new Bundle("~/bundles/povix-token-status").Include("~/Scripts/povix-token-status.js"));

            bundles.Add(new ScriptBundle("~/bundles/jquery").Include(
                        "~/Scripts/jquery-{version}.js"));

            bundles.Add(new ScriptBundle("~/bundles/jqueryval").Include(
                        "~/Scripts/jquery.validate*"));

            // Use the development version of Modernizr to develop with and learn from. Then, when you're
            // ready for production, use the build tool at https://modernizr.com to pick only the tests you need.
            bundles.Add(new ScriptBundle("~/bundles/modernizr").Include(
                        "~/Scripts/modernizr-*"));

            bundles.Add(new Bundle("~/bundles/bootstrap").Include(
                      "~/Scripts/bootstrap.js"));

            bundles.Add(new StyleBundle("~/Content/css").Include(
                      "~/Content/bootstrap.css",
                      "~/Content/site.css"));
        }
    }
}
