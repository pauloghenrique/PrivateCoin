using PrivateCoin.Core;
using PrivateCoin.Desktop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Compile with WalletStore.cs and RecoveryPhraseGenerator.cs to exercise the
// internal store against isolated, temporary files, never the user's wallet.
internal static class WalletStartupRegression
{
    private static int passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("PrivateCoin");

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (ArgumentException) { Check(true, name); return; }
        catch (SerializationException) { Check(true, name); return; }
        throw new Exception("Accepted invalid data: " + name);
    }

    private static long Balance(IEnumerable<string> addresses, IReadOnlyDictionary<string, long> balances)
    {
        return addresses.Distinct(StringComparer.Ordinal).Sum(address => balances.ContainsKey(address) ? balances[address] : 0);
    }

    private static Dictionary<string, object> Parse(byte[] data)
    {
        return (Dictionary<string, object>)Json.DeserializeObject(Encoding.UTF8.GetString(data));
    }

    private static byte[] Encode(Dictionary<string, object> data)
    {
        return Encoding.UTF8.GetBytes(Json.Serialize(data));
    }

    private static void RewriteNetwork(string path, Action<Dictionary<string, object>, Dictionary<string, object>> change)
    {
        var envelope = Parse(File.ReadAllBytes(path));
        var payload = Parse(Convert.FromBase64String((string)envelope["Data"]));
        change(envelope, payload);
        byte[] data = Encode(payload);
        envelope["Data"] = Convert.ToBase64String(data);
        using (var hash = SHA256.Create())
            envelope["Sha256"] = string.Concat(hash.ComputeHash(data).Select(value => value.ToString("x2")));
        File.WriteAllBytes(path, Encode(envelope));
    }

    private static void Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PrivateCoin-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var owned = new List<NamedWallet>();
        try
        {
            string phrase = RecoveryPhraseGenerator.Generate();
            byte[] seed = RecoveryPhraseGenerator.ToSeed(phrase);
            using (var original = Wallet.FromSeed(seed, 2))
            using (var legacy = new Wallet())
            {
                string[] keys = original.ExportPrivateKeys().ToArray();
                string[] addresses = original.OwnedOneTimeAddresses.ToArray();
                using (var imported = Wallet.FromPrivateKeys(keys, seed))
                {
                    Check(imported.OwnedOneTimeAddresses.SequenceEqual(addresses), "import preserves addresses and order");
                    Check(imported.ExportPrivateKeys().SequenceEqual(keys), "import preserves private keys");
                    Check(imported.CreateReceiveAddress() == original.CreateReceiveAddress(), "next deterministic address matches phrase recovery");
                }
                Reject(() => Wallet.FromPrivateKeys(keys, new byte[15]), "short seed rejected");
                Reject(() => Wallet.FromPrivateKeys(new[] { keys[0], keys[0] }, seed), "duplicate private keys rejected");

                var chain = new Blockchain();
                Block reward;
                foreach (string address in addresses) chain.TryAddWalletCreationReward(address, out reward);
                string destination = legacy.CreateReceiveAddress();
                chain.TryAddWalletCreationReward(destination, out reward);
                var pending = new[] { original.CreateTransaction(chain, destination, Blockchain.OneCoin) };
                var confirmed = chain.GetBalancesByAddress();
                var projected = chain.GetBalancesByAddress(pending);
                var repeated = addresses.Concat(addresses).Concat(new[] { "unknown" }).ToArray();
                Check(Balance(repeated, confirmed) == chain.GetBalance(repeated), "batch confirmed balances deduplicate addresses and ignore unknown addresses");
                Check(Balance(original.OwnedOneTimeAddresses, projected) == chain.GetBalance(original.OwnedOneTimeAddresses, pending), "batch projected balance includes change and reserved inputs");
                Check(Balance(new[] { destination }, projected) == chain.GetBalance(new[] { destination }, pending), "batch projected destination balance matches existing API");
                Check(Balance(original.OwnedOneTimeAddresses, confirmed) > Balance(original.OwnedOneTimeAddresses, projected), "confirmed snapshot remains unchanged by pending transaction");

                // Repeated entries avoid spending the benchmark on fixture generation;
                // each stored wallet still imports its own two RSA keys on startup.
                const int count = 40;
                for (int i = 0; i < count; i++)
                    owned.Add(new NamedWallet("Wallet " + i, Wallet.FromPrivateKeys(keys, seed), 0, null, phrase, true));
                owned.Add(new NamedWallet("Legacy", Wallet.FromPrivateKeys(legacy.ExportPrivateKeys())));
                var store = new WalletStore(directory);
                store.Save(owned, chain, pending);
                var watch = Stopwatch.StartNew();
                List<NamedWallet> loaded = store.LoadWallets();
                watch.Stop();
                long importMilliseconds = watch.ElapsedMilliseconds;
                try
                {
                    Check(loaded.Count == count + 1 && loaded[0].IsDeterministic && !loaded[count].IsDeterministic, "mixed deterministic and legacy wallets round-trip");
                    Check(loaded[0].Wallet.ExportPrivateKeys().SequenceEqual(keys), "store loads saved keys directly");
                    var payment = loaded[0].Wallet.CreateTransaction(chain, destination, Blockchain.OneCoin);
                    chain.ValidatePendingTransactions(new[] { payment });
                    Check(true, "restored wallet signs a valid transaction");
                    using (var reference = Wallet.FromSeed(seed, 2))
                        Check(loaded[1].Wallet.CreateReceiveAddress() == reference.CreateReceiveAddress(), "store preserves next deterministic index");
                    List<Transaction> restoredPending;
                    var restoredChain = new WalletStore(directory).LoadNetwork(out restoredPending);
                    Check(restoredChain.IsValid() && restoredPending.Count == 1, "network and pending transactions round-trip");
                }
                finally { foreach (var wallet in loaded) wallet.Dispose(); }

                watch.Restart();
                using (var regenerated = Wallet.FromSeed(seed, keys.Length)) { }
                watch.Stop();
                Console.WriteLine("BENCHMARK " + (count + 1) + " stored wallets / " + (count * 2 + 1) + " keys: " + importMilliseconds +
                    " ms loading saved keys; old regeneration estimate: " + (watch.ElapsedMilliseconds * count) + " ms (one-wallet sample).");

                string walletPath = Path.Combine(directory, "wallets.dat");
                byte[] walletFile = File.ReadAllBytes(walletPath);
                var walletData = Parse(ProtectedData.Unprotect(walletFile, Entropy, DataProtectionScope.CurrentUser));
                var first = (Dictionary<string, object>)((object[])walletData["Wallets"])[0];
                first["Addresses"] = new[] { "tampered-address" };
                File.WriteAllBytes(walletPath, ProtectedData.Protect(Encode(walletData), Entropy, DataProtectionScope.CurrentUser));
                Reject(() => new WalletStore(directory).LoadWallets(), "stored address mismatch rejected");
                File.WriteAllBytes(walletPath, walletFile);

                string networkPath = Path.Combine(directory, "Blockchain.json");
                byte[] networkFile = File.ReadAllBytes(networkPath);
                var invalidEnvelope = Parse(networkFile);
                invalidEnvelope["Sha256"] = new string('0', 64);
                File.WriteAllBytes(networkPath, Encode(invalidEnvelope));
                Reject(() => { List<Transaction> ignored; new WalletStore(directory).LoadNetwork(out ignored); }, "network checksum mismatch rejected");
                File.WriteAllBytes(networkPath, networkFile);
                RewriteNetwork(networkPath, (envelope, data) =>
                {
                    var wallet = (Dictionary<string, object>)((object[])data["Wallets"])[0];
                    wallet["TokenBalance"] = Convert.ToInt64(wallet["TokenBalance"]) + 1;
                });
                Reject(() => { List<Transaction> ignored; new WalletStore(directory).LoadNetwork(out ignored); }, "incorrect public balance rejected even with valid checksum");
                File.WriteAllBytes(networkPath, networkFile);
                RewriteNetwork(networkPath, (envelope, data) =>
                {
                    envelope["SchemaVersion"] = 3;
                    foreach (Dictionary<string, object> wallet in (object[])data["Wallets"])
                        wallet["TokenBalance"] = Balance(((object[])wallet["Addresses"]).Cast<string>(), projected);
                });
                List<Transaction> version3Pending;
                var version3Store = new WalletStore(directory);
                version3Store.LoadNetwork(out version3Pending);
                Check(version3Store.NetworkNeedsUpgrade && version3Pending.Count == 1, "schema 3 retains projected public balance compatibility");
                File.WriteAllBytes(networkPath, networkFile);
            }
            Console.WriteLine(passed + " checks passed.");
        }
        finally
        {
            foreach (var wallet in owned) wallet.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
