using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using PrivateCoin.Core;

internal static class PrivateCoinValidatorStateRegression
{
    public static int Main(string[] args)
    {
        try
        {
            Assembly desktop = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type named = desktop.GetType("PrivateCoin.Desktop.NamedWallet", true);
            Type formType = desktop.GetType("PrivateCoin.Desktop.Form1", true);
            // Test the actual state reconciliation method without creating a WinForms window.
            object form = FormatterServices.GetUninitializedObject(formType);
            GC.SuppressFinalize(form);
            using (var first = new Wallet()) using (var second = new Wallet()) using (var inactive = new Wallet())
            {
                string firstAddress = first.CreateReceiveAddress(), secondAddress = second.CreateReceiveAddress();
                var chain = new Blockchain();
                LegacyConsensusFixture.Fund(chain, firstAddress);
                LegacyConsensusFixture.Fund(chain, secondAddress);
                var common = chain.Blocks.ToArray();
                object a = Activator.CreateInstance(named, new object[] { "A", first, 0L, null, null, false });
                object b = Activator.CreateInstance(named, new object[] { "B", second, 0L, null, null, false });
                object c = Activator.CreateInstance(named, new object[] { "C", inactive, 0L, null, null, false });
                var wallets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(named));
                wallets.Add(a); wallets.Add(b); wallets.Add(c);
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                formType.GetField("wallets", fields).SetValue(form, wallets);
                formType.GetField("blockchain", fields).SetValue(form, chain);
                MethodInfo refresh = formType.GetMethod("RefreshValidatorState", fields);
                refresh.Invoke(form, null);
                Check(!(bool)named.GetProperty("IsValidator").GetValue(c), "inactive wallets remain inactive without throwing");
                chain.AddBlock(new[] { first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, Blockchain.OneCoin, 1) });
                chain.AddBlock(new[] { second.CreateStakeLockTransaction(chain, new Transaction[0], secondAddress, 2 * Blockchain.OneCoin, 1) });
                refresh.Invoke(form, null); refresh.Invoke(form, null);
                Check((long)named.GetProperty("LockedStake").GetValue(a) == Blockchain.OneCoin &&
                    (long)named.GetProperty("LockedStake").GetValue(b) == 2 * Blockchain.OneCoin,
                    "all local wallets restore confirmed guarantees and repeated refresh is safe");
                var fork = new Blockchain(common);
                for (int i = 0; i < 3; i++) LegacyConsensusFixture.Fund(fork, "validator-state-fixture-" + i);
                Check(chain.TryReplaceChain(fork.Blocks), "a valid greater-work fork replaces the old stake history");
                refresh.Invoke(form, null);
                Check(!(bool)named.GetProperty("IsValidator").GetValue(a) && !(bool)named.GetProperty("IsValidator").GetValue(b),
                    "reorganization removes obsolete metadata for every local validator");
                chain.AddBlock(new[] { first.CreateStakeLockTransaction(chain, new Transaction[0], firstAddress, 2 * Blockchain.OneCoin, 1) });
                refresh.Invoke(form, null);
                Check((long)named.GetProperty("LockedStake").GetValue(a) == 2 * Blockchain.OneCoin &&
                    (string)named.GetProperty("ValidatorRewardAddress").GetValue(a) == firstAddress,
                    "wallets adopt guarantees from the replacement history");
            }
            Console.WriteLine("5 validator state checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
