using PrivateCoin.Core;
using PrivateCoin.Desktop;
using System;
using System.Collections.Generic;
using System.IO;

// Run export with the original Core, then recover with a Core built with
// different configuration constants. All paths must be disposable test data.
internal static class PrivateCoinDesktopForkRecoveryRegression
{
    public static int Main(string[] args)
    {
        try
        {
            string directory = args[1];
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Blockchain.json");
            var store = new WalletStore(directory);
            List<Transaction> pending;
            if (args[0] == "export")
            {
                File.WriteAllText(path, "{}");
                store.LoadNetworkOrRecreate(new List<NamedWallet>(), out pending);
                File.WriteAllLines(Path.Combine(directory, "original-identity.txt"),
                    new[] { Blockchain.GenesisHash, Blockchain.RulesHash, Blockchain.NetworkId });
            }
            else
            {
                string[] original = File.ReadAllLines(Path.Combine(directory, "original-identity.txt"));
                Check(original[0] != Blockchain.GenesisHash && original[1] != Blockchain.RulesHash &&
                    original[2] != Blockchain.NetworkId, "changed rules create a separate network identity");
                bool rejected = false;
                try { store.LoadNetwork(out pending); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "previous network chain is rejected");
                Blockchain fork = store.LoadNetworkOrRecreate(new List<NamedWallet>(), out pending);
                Check(fork.IsValid() && fork.Blocks.Count == 1 && pending.Count == 0 &&
                    fork.Blocks[0].Hash == Blockchain.GenesisHash &&
                    fork.Blocks[0].Rules.CoreConfigurationManifest == CoreConfiguration.Manifest,
                    "replacement chain uses the altered executable configuration");
                Check(store.LoadNetwork(out pending).Blocks[0].Hash == Blockchain.GenesisHash,
                    "separate chain survives reload");
                Console.WriteLine("4 fork recovery checks passed");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
