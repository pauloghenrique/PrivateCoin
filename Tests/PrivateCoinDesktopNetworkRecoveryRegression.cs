using PrivateCoin.Core;
using PrivateCoin.Desktop;
using System;
using System.Collections.Generic;
using System.IO;

internal static class PrivateCoinDesktopNetworkRecoveryRegression
{
    public static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "privatecoin-network-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string networkPath = Path.Combine(directory, "Blockchain.json");
            string walletPath = Path.Combine(directory, "wallets.dat");
            string recoveryPath = Path.Combine(directory, "recovery.dat");
            File.WriteAllText(walletPath, "private-wallet-sentinel");
            File.WriteAllText(recoveryPath, "private-recovery-sentinel");
            var store = new WalletStore(directory);
            var wallets = new List<NamedWallet>();
            List<Transaction> pending;
            foreach (string invalid in new[] { "{invalid-json", "{}",
                "{\"SchemaVersion\":5,\"Data\":\"e30=\",\"Sha256\":\"altered\"}",
                "{\"SchemaVersion\":5,\"Data\":\"not base64!\",\"Sha256\":\"altered\"}" })
            {
                File.WriteAllText(networkPath, invalid);
                Blockchain restored = store.LoadNetworkOrRecreate(wallets, out pending);
                Check(restored.IsValid() && restored.Blocks.Count == 1 && pending.Count == 0, "canonical empty chain restored");
                Check(store.LoadNetwork(out pending).IsValid() && pending.Count == 0, "replacement file loads normally");
                Check(File.ReadAllText(walletPath) == "private-wallet-sentinel" &&
                    File.ReadAllText(recoveryPath) == "private-recovery-sentinel", "private files preserved");
            }
            string valid = File.ReadAllText(networkPath);
            store.LoadNetworkOrRecreate(wallets, out pending);
            Check(File.ReadAllText(networkPath) == valid, "valid file preserved");
            File.Delete(networkPath);
            Directory.CreateDirectory(networkPath);
            try
            {
                store.LoadNetworkOrRecreate(wallets, out pending);
                throw new Exception("disk access failure was suppressed");
            }
            catch (UnauthorizedAccessException) { }
            Console.WriteLine("14 network recovery checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
