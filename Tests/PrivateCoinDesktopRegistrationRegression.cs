using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using PrivateCoin.Core;
using PrivateCoin.Desktop;

class PrivateCoinDesktopRegistrationRegression
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidOperationException) { Check(true, message); return; } throw new Exception(message); }
    public static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        using (var first = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain(); var empty = new Transaction[0];
            Check(!WalletRegistration.HasRegistration(chain, first, empty) && chain.Blocks.Count == 1 && first.OwnedOneTimeAddresses.Count == 0, "opening a local wallet does not register or issue coins");
            var registration = WalletRegistration.Prepare(chain, first, empty);
            Check(registration.Kind == TransactionKind.InitialLaunchCreate && first.OwnedOneTimeAddresses.Count == 1, "registering the selected first wallet prepares the automatic launch");
            Check(WalletRegistration.Describe(registration).Contains("600 POVIX") && WalletRegistration.Describe(registration).Contains("359.400 POVIX"), "Desktop distinguishes the 600 reward from the pool reserve");
            Check(WalletRegistration.HasRegistration(chain, first, new[] { registration }) && !WalletRegistration.HasRegistration(chain, first, empty), "pending registration is distinct from confirmed registration");
            Check(WalletRegistration.LaunchStatus(chain, new[] { registration }).Contains("aguardando confirmação"), "pending launch is shown without claiming confirmed funds");
            Reject(() => WalletRegistration.Prepare(chain, first, new[] { registration }), "Desktop rejects a second queued registration for the same wallet");
            Check(first.OwnedOneTimeAddresses.Count == 1 && chain.GetBalance(first.OwnedOneTimeAddresses) == 0, "retry creates no extra identity or premature reward");
            chain.AddBlock(new[] { registration });
            Check(WalletRegistration.HasRegistration(chain, first, empty), "confirmation marks the selected wallet registered");
            Reject(() => WalletRegistration.Prepare(chain, first, empty), "Desktop prevents duplicate confirmed rewards");
            var balance = WalletBalanceSummary.Read(chain, first, empty);
            Check(balance.Available == 0 && balance.LaunchLocked == 600 * Blockchain.OneCoin && balance.Total == 600 * Blockchain.OneCoin && balance.LaunchLockText.Contains("Bloqueado no lançamento"), "Desktop displays the first 600 POVIX as locked guarantee, without adding pool reserves");
            Check(WalletRegistration.LaunchStatus(chain, empty).Contains("Pool criada") && WalletRegistration.LaunchStatus(chain, empty).Contains("359.400"), "Desktop shows the automatically confirmed pool in collection");
            Check(chain.GetTokenBalances(first.OwnedOneTimeAddresses).Count == 2 && chain.GetTokenBalances(first.OwnedOneTimeAddresses).All(token => token.Amount == (token.Id == InitialLaunch.UsdcTokenId ? InitialLaunch.WalletUsdcReward : InitialLaunch.FundingTokenSupply)), "Desktop token catalogue receives 100 USDC and one million USDT");
            string original = second.CreateReceiveAddress();
            var later = WalletRegistration.Prepare(chain, second, empty);
            Check(later.Kind == TransactionKind.WalletCreate && later.Outputs[0].Amount == 0 && later.Outputs[0].OneTimeAddress == original, "later selected wallet reuses its receiving identity and gets zero reward");
            Check(WalletRegistration.Describe(later).StartsWith("Registro com 100 USDC"), "Desktop explains the 100-USDC registration reward");
            chain.AddBlock(new[] { later });
            Check(WalletBalanceSummary.Read(chain, second, empty).Available == 0, "second wallet remains at zero after registration");
            using (var stream = new MemoryStream())
            {
                var json = new DataContractJsonSerializer(typeof(Block[])); json.WriteObject(stream, chain.Blocks.ToArray()); stream.Position = 0;
                var restored = new Blockchain((Block[])json.ReadObject(stream));
                Check(WalletRegistration.HasRegistration(restored, first, empty) && WalletRegistration.HasRegistration(restored, second, empty) && restored.GetInitialLaunchStatus().Created, "chain restoration retains zero-reward registrations and pool state");
                Reject(() => WalletRegistration.Prepare(restored, second, empty), "restored zero-reward registration cannot be repeated under another address");
            }
        }
        Console.WriteLine(checks + " Desktop registration checks passed");
    }
}
