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
            Check(chain.GetMinimumValidatorsForTransactions(new[] { later }) == 1, "registration during launch needs one eligible signer");
            string validatorAddress = first.OwnedOneTimeAddresses.Single();
            var stake = first.CreateValidatorStake(validatorAddress, Blockchain.WalletCreationReward);
            int rewardHeight = chain.Blocks.Count;
            Check(WalletBalanceSummary.Read(chain, first, new[] { later }).Available == 0, "queued registration cannot credit a premature validator reward");
            var paidBlock = chain.AddProofOfStakeBlock(new[] { later }, new[] { stake });
            Check(paidBlock.Validators.Count == 1 && paidBlock.Validators.Single().IsCreator, "Desktop registration produces a signed block with one confirmed validator");
            var paidBalance = WalletBalanceSummary.Read(chain, first, empty);
            Check(paidBalance.Available == ProofOfStake.GetBlockReward(rewardHeight) && paidBalance.LaunchLocked == Blockchain.WalletCreationReward,
                "block reward is spendable in Desktop while the first 600 POVIX remain locked");
            Check(paidBlock.Transactions[0].Outputs.Sum(item => item.Amount) == ProofOfStake.GetBlockReward(rewardHeight), "registration pays the scheduled reward once");
            long totalCap = checked(Blockchain.MaximumSupply + ProofOfStake.MaximumSupply);
            long issuedNative = chain.GetBalance(first.OwnedOneTimeAddresses) + chain.GetBalance(second.OwnedOneTimeAddresses) + chain.GetInitialLaunchStatus().Remaining;
            Check(issuedNative == Blockchain.MaximumSupply + ProofOfStake.GetBlockReward(rewardHeight), "validator reward is counted in issued native supply without changing pool reserves");
            Check(totalCap == 18180000L * Blockchain.OneCoin && totalCap - issuedNative == ProofOfStake.MaximumSupply - paidBalance.Available,
                "paid block reward reduces the unissued budget within the unchanged 18,180,000-POVIX cap");
            Check(ProofOfStake.RewardPhases.Sum(phase => phase.TotalReward) == ProofOfStake.MaximumSupply,
                "registration rewards use the existing capped reward schedule");
            Reject(() => chain.AddProofOfStakeBlock(new[] { later }, new[] { stake }), "replaying the same registration cannot issue a second block reward");
            Check(chain.GetMinimumValidatorsForTransactions(new[] { new Transaction { Kind = TransactionKind.Transfer } }) == 2,
                "ordinary transfers retain two validators during launch");
            Check(chain.GetMinimumValidatorsForTransactions(new[] { later, new Transaction { Kind = TransactionKind.StakeLock } }) == 2,
                "mixed registration batches do not acquire single-validator rights");
            Check(WalletBalanceSummary.Read(chain, second, empty).Available == 0, "second wallet remains at zero after registration");
            // Reproduce two Desktop wallets: founder validates the registration, then its buyer's first swap.
            var purchase = second.CreateInitialLaunchPurchase(chain, empty, original, original, 1);
            Check(chain.GetValidatorsForTransactions(new[] { purchase }).Single().ValidatorId == validatorAddress,
                "initial 600-POVIX wallet is eligible for the second wallet's very first launch swap");
            Check(chain.GetMinimumValidatorsForTransactions(new[] { purchase }) == 1,
                "first launch swap needs only the existing founder validator");
            purchase.TransactionApproval = chain.CreateTransactionApproval(purchase, new[] { purchase }, new[] { stake });
            Check(purchase.TransactionApproval != null && purchase.TransactionApproval.CollateralTransactionId == registration.Id &&
                purchase.TransactionApproval.CollateralOutputIndex == 4 && chain.HasValidTransactionApproval(purchase, new[] { purchase }),
                "first launch swap approval uses the confirmed original 600-POVIX collateral");
            Check(!Blockchain.IsSelfValidatedOperation(purchase) && Blockchain.RequiresLockedTokenApproval(purchase),
                "signed first swap follows rewarded validation instead of unsigned bootstrap");
            long founderBeforeSwap = WalletBalanceSummary.Read(chain, first, empty).Available;
            int swapHeight = chain.Blocks.Count;
            var swapBlock = chain.AddProofOfStakeBlock(new[] { purchase }, new[] { stake });
            var founderAfterSwap = WalletBalanceSummary.Read(chain, first, empty);
            Check(swapBlock.Validators.Count == 1 && founderAfterSwap.Available - founderBeforeSwap == ProofOfStake.GetBlockReward(swapHeight) + purchase.Fee,
                "sole founder validator receives the first swap's scheduled block reward and network fee");
            Check(founderAfterSwap.LaunchLocked == Blockchain.WalletCreationReward && WalletBalanceSummary.Read(chain, second, empty).LaunchLocked == InitialLaunch.PurchaseAmount - purchase.Fee,
                "first swap keeps both initial collateral and buyer receipt locked");
            using (var third = new Wallet())
            {
                var nextRegistration = WalletRegistration.Prepare(chain, third, empty);
                chain.AddProofOfStakeBlock(new[] { nextRegistration }, new[] { stake });
                string thirdAddress = third.OwnedOneTimeAddresses.Single();
                var nextSwap = third.CreateInitialLaunchPurchase(chain, empty, thirdAddress, thirdAddress, 1);
                nextSwap.TransactionApproval = chain.CreateTransactionApproval(nextSwap, new[] { nextSwap }, new[] { stake });
                var nextBlock = chain.AddProofOfStakeBlock(new[] { nextSwap }, new[] { stake });
                Check(nextBlock.Validators.Count == 1 && nextBlock.Validators[0].ValidatorId == validatorAddress,
                    "founder remains eligible for later swaps even when another locked buyer exists");
                var ownPurchase = first.CreateInitialLaunchPurchase(chain, empty, validatorAddress, validatorAddress, 1);
                Check(chain.CreateTransactionApproval(ownPurchase, new[] { ownPurchase }, new[] { stake }) == null,
                    "600-POVIX founder cannot approve its own purchase");
                Reject(() => chain.AddProofOfStakeBlock(new[] { ownPurchase }, new[] { stake }),
                    "600-POVIX founder cannot sign its own purchase block");
            }
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
