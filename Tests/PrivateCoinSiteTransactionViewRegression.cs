using System;
using System.IO;
using System.Web.Mvc.Razor;
using System.Web.Razor;

internal static class PrivateCoinSiteTransactionViewRegression
{
    public static void Main()
    {
        const string path = "PrivateCoin.Site/Views/Home/Transaction.cshtml";
        var host = new MvcWebPageRazorHost("~/Views/Home/Transaction.cshtml", Path.GetFullPath(path));
        var engine = new RazorTemplateEngine(host);
        using (var input = File.OpenText(path))
        {
            var result = engine.GenerateCode(input);
            if (!result.Success)
            {
                foreach (var error in result.ParserErrors) Console.Error.WriteLine(error);
                throw new InvalidOperationException("A página de transação contém erros Razor.");
            }
        }
        Console.WriteLine("PASS: sintaxe Razor da página de detalhes da transação");
    }
}
