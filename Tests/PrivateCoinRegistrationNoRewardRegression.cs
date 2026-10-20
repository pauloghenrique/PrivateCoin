using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using PrivateCoin.Core;

internal static class PrivateCoinRegistrationNoRewardRegression
{
    private static int checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; Console.WriteLine("PASS " + text); }
    private static void Reject(Action action, string text) { try { action(); } catch (InvalidOperationException) { Check(true, text); return; } throw new Exception("Expected rejection: " + text); }
    private static T Copy<T>(T value) { using (var stream = new MemoryStream()) { var json = new DataContractJsonSerializer(typeof(T)); json.WriteObject(stream, value); stream.Position = 0; return (T)json.ReadObject(stream); } }
    public static void Main()
    {
        var empty = new Transaction[0];
        using (var founder = new Wallet()) using (var buyer = new Wallet()) using (var third = new Wallet())
        {
            string a = founder.CreateReceiveAddress(), b = buyer.CreateReceiveAddress(), c = third.CreateReceiveAddress();
            var chain = new Blockchain();
            chain.AddBlock(new[] { chain.CreateWalletCreationTransaction(a, empty) });
            var founderStake = founder.CreateValidatorStake(a, Blockchain.WalletCreationReward);
            var second = chain.CreateWalletCreationTransaction(b, empty);
            var registrationBlock = chain.AddProofOfStakeBlock(new[] { second }, new[] { founderStake });
            Check(registrationBlock.Validators.Count == 1 && registrationBlock.Transactions[0].Outputs.Count == 0, "one validator signs registration without reward outputs");
            Check(chain.GetSpendableBalance(new[] { a }, empty) == 0 && new BlockchainQueryApi(chain).GetSummary().IssuedTokenAmount == Blockchain.MaximumSupply, "registration credits no founder reward and increases no public native issuance");
            Check(chain.GetTokenBalance(new[] { b }, InitialLaunch.PusdtTokenId) == 0, "new wallet receives no free stablecoin");
            PusdtBridgeFixture.Seed(chain, founder, buyer, third);
            var purchase = buyer.CreateInitialLaunchPurchase(chain, empty, b, b, 1);
            purchase.TransactionApproval = chain.CreateTransactionApproval(purchase, new[] { purchase }, new[] { founderStake });
            int purchaseHeight = chain.Blocks.Count;
            chain.AddProofOfStakeBlock(new[] { purchase }, new[] { founderStake });
            Check(chain.GetSpendableBalance(new[] { a }, empty) == ProofOfStake.GetBlockReward(purchaseHeight) + purchase.Fee, "launch swap still pays its scheduled reward plus approving validator fee");
            var buyerStake = buyer.CreateValidatorStake(b, purchase.Outputs[2].Amount);
            var validators = new[] { founderStake, buyerStake };
            long founderBefore = chain.GetBalance(new[] { a }), buyerBefore = chain.GetBalance(new[] { b });
            long issuedBefore = new BlockchainQueryApi(chain).GetSummary().IssuedTokenAmount;
            var nextRegistration = chain.CreateWalletCreationTransaction(c, empty);
            var twoValidatorBlock = chain.AddProofOfStakeBlock(new[] { nextRegistration }, validators);
            Check(twoValidatorBlock.Validators.Count == 2 && twoValidatorBlock.Validators.All(item => !string.IsNullOrEmpty(item.VoteSignature)), "multiple validators still create and confirm the registration block");
            Check(twoValidatorBlock.Transactions[0].Outputs.Count == 0 && chain.GetBalance(new[] { a }) == founderBefore && chain.GetBalance(new[] { b }) == buyerBefore, "neither creator nor confirmer earns POVIX from registration");
            Check(new BlockchainQueryApi(chain).GetSummary().IssuedTokenAmount == issuedBefore, "summary excludes unpaid signed registration blocks from validator emission");
            Check(new BlockchainQueryApi(chain).GetLedger(0, 1000).Where(item => item.BlockHeight == twoValidatorBlock.Height).Sum(item => item.IssuedAmount) == 0, "ledger records zero native issuance for the entire registration block");
            var forged = Copy(twoValidatorBlock);
            forged.Transactions[0].Outputs.Add(new TransactionOutput { Amount = ProofOfStake.GetBlockReward(forged.Height), OneTimeAddress = a });
            forged.Transactions[0].Id = LegacyConsensusFixture.Id(forged.Transactions[0]);
            LegacyConsensusFixture.Resign(forged, validators); LegacyConsensusFixture.Mine(forged);
            Check(ProofOfWork.MeetsTarget(forged.Hash), "forged paid registration has actual proof of work and renewed validator signatures");
            Reject(() => new Blockchain(Copy(chain.Blocks.Take(forged.Height).ToArray()).Concat(new[] { forged })), "consensus rejects a registration that tries to mint the usual scheduled reward");
            using (var fourth = new Wallet()) using (var fifth = new Wallet())
            {
                var firstQueued = chain.CreateWalletCreationTransaction(fourth.CreateReceiveAddress(), empty);
                var secondQueued = chain.CreateWalletCreationTransaction(fifth.CreateReceiveAddress(), new[] { firstQueued });
                var batch = chain.AddProofOfStakeBlock(new[] { firstQueued, secondQueued }, validators);
                Check(batch.Transactions.Count == 3 && batch.Transactions[0].Outputs.Count == 0, "multiple registrations in one signed block also pay zero");
                Check(new BlockchainQueryApi(chain).GetSummary().IssuedTokenAmount == issuedBefore, "batching registrations cannot consume the reward budget");
            }
            var thirdPurchase = third.CreateInitialLaunchPurchase(chain, empty, c, c, 1);
            thirdPurchase.TransactionApproval = chain.CreateTransactionApproval(thirdPurchase, new[] { thirdPurchase }, new[] { buyerStake });
            using (var extra = new Wallet())
            {
                var extraRegistration = chain.CreateWalletCreationTransaction(extra.CreateReceiveAddress(), empty);
                Reject(() => chain.AddProofOfStakeBlock(new[] { thirdPurchase, extraRegistration }, validators), "registration cannot mix with a remunerated swap");
            }
            long beforeSwap = chain.GetBalance(new[] { b }); int height = chain.Blocks.Count;
            chain.AddProofOfStakeBlock(new[] { thirdPurchase }, new[] { buyerStake });
            Check(chain.GetBalance(new[] { b }) - beforeSwap == ProofOfStake.GetBlockReward(height) + thirdPurchase.Fee, "later swaps continue to pay normal reward and fee");
            Check(new BlockchainQueryApi(chain).GetSummary().IssuedTokenAmount == issuedBefore + ProofOfStake.GetBlockReward(height), "only the remunerated swap advances public validator issuance");
            Check(new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(), "unpaid registrations and paid swaps survive complete chain replay");
            Check(chain.GetInitialLaunchLockedBalance(new[] { a }) == Blockchain.WalletCreationReward && Blockchain.MaximumSupply + ProofOfStake.MaximumSupply == 18180000L * Blockchain.OneCoin, "initial lock and existing total supply cap are preserved");
        }
        Console.WriteLine(checks + " no-registration-reward checks passed");
    }
}
