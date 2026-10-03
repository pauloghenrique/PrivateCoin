using PrivateCoin.Core;
using PrivateCoin.Site.Models;
using System.Web.Mvc;

namespace PrivateCoin.Site.Controllers
{
    public class HomeController : Controller
    {
        private const int LedgerPageSize = 100;
        private static readonly BlockchainQueryApi DefaultQueryApi =
            new BlockchainQueryApi(new Blockchain());
        private readonly BlockchainQueryApi queryApi;

        public HomeController() : this(DefaultQueryApi)
        {
        }

        public HomeController(BlockchainQueryApi queryApi)
        {
            this.queryApi = queryApi ?? throw new System.ArgumentNullException(nameof(queryApi));
        }

        public ActionResult Index()
        {
            var model = new HomeViewModel(queryApi.GetSummary(), queryApi.GetLedger(0, LedgerPageSize));
            return View(model);
        }

        public ActionResult About()
        {
            ViewBag.Message = "Your application description page.";

            return View();
        }

        public ActionResult Contact()
        {
            ViewBag.Message = "Your contact page.";

            return View();
        }
    }
}
