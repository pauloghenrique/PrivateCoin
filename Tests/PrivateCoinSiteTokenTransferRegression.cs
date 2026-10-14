using System;
using System.Collections.Generic;
using PrivateCoin.Core;
using PrivateCoin.Site.Models;

internal static class PrivateCoinSiteTokenTransferRegression
{
    private static int checks;
    private static void Equal(object actual, object expected, string label)
    {
        if (!actual.Equals(expected)) throw new Exception(label + ": expected " + expected + "; got " + actual);
        checks++;
        Console.WriteLine("PASS " + label);
    }

    private static LedgerEntry Transfer(LedgerEntryType type, string assetId, bool nativeFirst)
    {
        var outputs = new List<TransactionOutput> {
            new TransactionOutput { AssetId = assetId, Amount = 1234 },
            new TransactionOutput { AssetId = assetId, Amount = 98766 }
        };
        var nativeChange = new TransactionOutput { Amount = 5 * Blockchain.OneCoin };
        if (nativeFirst) outputs.Insert(0, nativeChange); else outputs.Add(nativeChange);
        var tx = new Transaction { Kind = TransactionKind.TokenTransfer,
            Fee = Blockchain.TransferFeeStep, Outputs = outputs };
        return new LedgerEntry(1, "block", tx, type, 0, nativeChange.Amount, 0, 1);
    }

    public static int Main()
    {
        try
        {
            var model = new HomeViewModel { Tokens = new Dictionary<string, TokenDefinition> {
                { "token-a", new TokenDefinition { Name = "Alpha", Symbol = "ALP", Decimals = 2 } },
                { "token-b", new TokenDefinition { Name = "Beta", Symbol = "BET", Decimals = 0 } },
                { "token-c", new TokenDefinition { Name = "Gamma", Symbol = "GAM", Decimals = 8 } }
            } };
            foreach (var type in new[] { LedgerEntryType.TokenTransfer, LedgerEntryType.Transfer })
            foreach (bool nativeFirst in new[] { false, true })
            {
                var entry = Transfer(type, "token-a", nativeFirst);
                Equal(model.FormatLedgerAmount(entry), "12,34 Alpha", "recipient amount excludes token and native change: " + type + "/" + nativeFirst);
                Equal(model.LedgerTokenName(entry), "Alpha", "transferred token identity: " + type + "/" + nativeFirst);
                Equal(HomeViewModel.EntryTypeName(entry), "Transferência de token", "transfer label: " + type + "/" + nativeFirst);
                Equal(HomeViewModel.LedgerAmount(entry), 0L, "token transfer does not become a native payment: " + type + "/" + nativeFirst);
                Equal(HomeViewModel.FormatTokens(entry.Fee), "0,00000001 POVIX", "fee stays native: " + type + "/" + nativeFirst);
            }
            Equal(model.FormatLedgerAmount(Transfer(LedgerEntryType.Transfer, "token-b", false)), "1.234 Beta", "zero decimal token");
            Equal(model.FormatLedgerAmount(Transfer(LedgerEntryType.Transfer, "token-c", false)), "0,00001234 Gamma", "eight decimal token");
            model.Tokens = null;
            var unknown = Transfer(LedgerEntryType.Transfer, "token-a", true);
            Equal(model.FormatLedgerAmount(unknown), "1.234 unidades atômicas · token-a", "unknown metadata preserves asset ID and atomic amount");
            Equal(model.LedgerTokenName(unknown), "token-a", "unknown metadata never defaults to POVIX");
            var tx = new Transaction { Outputs = new List<TransactionOutput> {
                new TransactionOutput { Amount = Blockchain.OneCoin },
                new TransactionOutput { Amount = 4 * Blockchain.OneCoin }
            } };
            var native = new LedgerEntry(1, "block", tx, LedgerEntryType.Transfer, 0, 5 * Blockchain.OneCoin, 0, 1);
            Equal(model.FormatLedgerAmount(native), "1,00000000 POVIX", "native transfer excludes native change");
            Equal(HomeViewModel.EntryTypeName(native), "Transferência", "native transfer label");
            Console.WriteLine(checks + " site token transfer checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
