using System;
using System.Collections.Generic;
using PrivateCoin.Core;
using PrivateCoin.Site.Models;

internal static class PrivateCoinSiteSwapLedgerRegression
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS " + label);
    }

    private static LedgerEntry Swap(string assetA, string assetB, int inputSide, long sent, long received)
    {
        var transaction = new Transaction {
            Kind = TransactionKind.LiquiditySwap,
            LiquiditySwap = new LiquiditySwapTerms { InputSide = inputSide, Amount = sent },
            Outputs = new List<TransactionOutput> {
                new TransactionOutput { AssetId = assetA, Amount = 999999999 },
                new TransactionOutput { AssetId = assetB, Amount = 888888888 },
                new TransactionOutput { AssetId = inputSide == 0 ? assetB : assetA, Amount = received },
                new TransactionOutput { Amount = 777777777 }
            }
        };
        return new LedgerEntry(1, "block", transaction, LedgerEntryType.LiquiditySwap, 0, 0, 0, 1);
    }

    public static int Main()
    {
        try
        {
            var model = new HomeViewModel { Tokens = new Dictionary<string, TokenDefinition> {
                { "a", new TokenDefinition { Name = "Alpha", Decimals = 2 } },
                { "b", new TokenDefinition { Name = "Beta", Decimals = 0 } }
            } };
            var buy = Swap(null, "a", 0, Blockchain.OneCoin, 1234);
            Check(model.FormatLedgerAmount(buy) == "1,00000000 POVIX → 12,34 Alpha", "POVIX to token uses trade quantities, excluding reserves and change");
            Check(HomeViewModel.LedgerAmount(buy) == Blockchain.OneCoin, "native amount is sent POVIX, not reserve");
            var sell = Swap(null, "a", 1, 2000, Blockchain.OneCoin / 2);
            Check(model.FormatLedgerAmount(sell) == "20,00 Alpha → 0,50000000 POVIX", "token to POVIX uses payout");
            Check(HomeViewModel.LedgerAmount(sell) == Blockchain.OneCoin / 2, "native amount is received POVIX");
            Check(model.FormatLedgerAmount(Swap("a", null, 1, Blockchain.OneCoin, 1234)) == "1,00000000 POVIX → 12,34 Alpha", "native asset can be the second reserve");
            var pair = Swap("a", "b", 0, 1234, 56);
            Check(model.FormatLedgerAmount(pair) == "12,34 Alpha → 56 Beta", "token pair is never labeled POVIX");
            Check(model.LedgerTokenName(pair) == "Alpha → Beta", "both traded assets are identified");
            Check(HomeViewModel.LedgerAmount(pair) == 0, "token pair has no native trade amount");
            Check(HomeViewModel.EntryTypeName(pair.Type) == "Troca", "swap has distinct operation label");
            model.Tokens = null;
            Check(model.FormatLedgerAmount(pair) == "1.234 unidades atômicas · a → 56 unidades atômicas · b", "unknown token metadata keeps atomic quantities");
            Console.WriteLine(checks + " site swap ledger checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
