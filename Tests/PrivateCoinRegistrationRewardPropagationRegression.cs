using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using PrivateCoin.Core;

internal static class PrivateCoinRegistrationRewardPropagationRegression
{
    private static int checks;
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception(text);
        checks++; Console.WriteLine("PASS " + text);
    }
    private static void Reject(Action action, string text, string reason = null)
    {
        try { action(); }
        catch (InvalidOperationException error)
        {
            if (reason != null && !error.Message.Contains(reason)) throw;
            Check(true, text); return;
        }
        throw new Exception("Expected rejection: " + text);
    }
    private static T Copy<T>(T value)
    {
        using (var stream = new MemoryStream())
        {
            var json = new DataContractJsonSerializer(typeof(T));
            json.WriteObject(stream, value); stream.Position = 0;
            return (T)json.ReadObject(stream);
        }
    }
    public static void Main()
    {
        var empty = new Transaction[0];
        using (var founder = new Wallet())
        {
            string address = founder.CreateReceiveAddress();
            var validatorNode = new Blockchain();
            var bootstrap = validatorNode.CreateWalletCreationTransaction(address, empty);
            Check(validatorNode.CanCreateUnsignedBlock(new[] { bootstrap }), "initial bootstrap remains available without a preexisting validator");
            var earlyWallet = new Wallet();
            var earlyRegistration = validatorNode.CreateWalletCreationTransaction(earlyWallet.CreateReceiveAddress(), new[] { bootstrap });
            Check(!validatorNode.CanCreateUnsignedBlock(new[] { bootstrap, earlyRegistration }), "second wallet queued before bootstrap cannot share its unpaid block");
            Reject(() => validatorNode.AddBlock(new[] { bootstrap, earlyRegistration }), "bootstrap cannot absorb a later registration without reward");
            TimeSpan bootstrapTime = TimeSpan.Zero;
            var bootstrapScheduler = new BlockProductionScheduler(() => bootstrapTime);
            Func<IReadOnlyList<Transaction>, bool> bootstrapPolicy = candidate => Blockchain.HasCompatibleValidatorRules(candidate) && validatorNode.CanCreateUnsignedBlock(candidate);
            bootstrapScheduler.SelectBatch(new[] { bootstrap, earlyRegistration }, bootstrapPolicy);
            bootstrapTime = TimeSpan.FromSeconds(20);
            var bootstrapBatch = bootstrapScheduler.SelectBatch(new[] { bootstrap, earlyRegistration }, bootstrapPolicy);
            Check(bootstrapBatch.Count == 1 && bootstrapBatch[0].Kind == TransactionKind.InitialLaunchCreate,
                "Desktop scheduler confirms only the automatic pool before processing the queued second wallet");
            validatorNode.AddBlock(bootstrapBatch);
            // A second node knows the same confirmed collateral but has none of its private keys.
            var observerNode = new Blockchain(Copy(validatorNode.Blocks.ToArray()));
            var signer = founder.CreateValidatorStake(address, Blockchain.WalletCreationReward);
            long paid = 0;
            for (int walletNumber = 2; walletNumber <= 4; walletNumber++)
            using (var wallet = new Wallet())
            {
                var registration = walletNumber == 2 ? earlyRegistration : validatorNode.CreateWalletCreationTransaction(wallet.CreateReceiveAddress(), empty);
                var batch = new[] { registration };
                Check(!validatorNode.CanCreateUnsignedBlock(batch) && !observerNode.CanCreateUnsignedBlock(batch),
                    "wallet " + walletNumber + ": both nodes require the signed reward path regardless of local keys");
                Reject(() => observerNode.AddBlock(batch), "wallet " + walletNumber + ": keyless peer cannot confirm without paying a validator", "signed block");
                Check(!observerNode.CanCreateUnsignedBlock(new[] { registration, new Transaction { Kind = TransactionKind.StakeLock } }),
                    "wallet " + walletNumber + ": mixing a self-validated operation cannot enable unsigned fallback");
                TimeSpan elapsed = TimeSpan.Zero;
                var observerScheduler = new BlockProductionScheduler(() => elapsed);
                Func<IReadOnlyList<Transaction>, bool> observerPolicy = candidate => observerNode.CanCreateUnsignedBlock(candidate);
                observerScheduler.SelectBatch(batch, observerPolicy); elapsed = TimeSpan.FromSeconds(20);
                Check(observerScheduler.SelectBatch(batch, observerPolicy).Count == 0 && observerScheduler.CandidateCount == 0,
                    "wallet " + walletNumber + ": keyless Desktop production waits instead of losing the reward");
                var fork = new Block { ConsensusVersion = Blockchain.ConsensusVersion, Height = observerNode.Blocks.Count,
                    PreviousHash = observerNode.Blocks.Last().Hash, TimestampUtcTicks = DateTime.UtcNow.Ticks,
                    Transactions = new List<Transaction> { registration } };
                LegacyConsensusFixture.Mine(fork);
                Check(ProofOfWork.MeetsTarget(fork.Hash), "wallet " + walletNumber + ": competing unsigned block has real valid proof of work");
                var unsignedChain = Copy(observerNode.Blocks.ToArray()).Concat(new[] { fork }).ToArray();
                Reject(() => new Blockchain(unsignedChain), "wallet " + walletNumber + ": replay rejects even a valid-work unsigned competing block");
                long before = validatorNode.GetBalance(new[] { address });
                int height = validatorNode.Blocks.Count;
                var block = validatorNode.AddProofOfStakeBlock(batch, new[] { signer });
                long reward = ProofOfStake.GetBlockReward(height); paid = checked(paid + reward);
                Check(block.Validators.Count == 1 && validatorNode.GetBalance(new[] { address }) - before == reward,
                    "wallet " + walletNumber + ": the sole founder receives exactly the scheduled reward");
                bool changed;
                Check(observerNode.TrySynchronizeChain(Copy(validatorNode.Blocks.ToArray()), out changed) && changed,
                    "wallet " + walletNumber + ": peer accepts and synchronizes the signed rewarded block");
                Check(observerNode.GetBalance(new[] { address }) == Blockchain.WalletCreationReward + paid,
                    "wallet " + walletNumber + ": synchronized rewards do not alternate or disappear");
            }
            earlyWallet.Dispose();
            Check(new Blockchain(Copy(validatorNode.Blocks.ToArray())).IsValid(), "all three consecutive rewards survive full replay");
            Check(validatorNode.GetInitialLaunchLockedBalance(new[] { address }) == Blockchain.WalletCreationReward &&
                validatorNode.GetInitialLaunchStatus().Remaining == InitialLaunch.ReserveSupply,
                "consecutive registration rewards preserve the locked 600 and the principal reserve");
            Check(Blockchain.MaximumSupply + ProofOfStake.MaximumSupply == 18180000L * Blockchain.OneCoin,
                "consecutive registration rewards keep the existing supply cap");
        }
        Console.WriteLine(checks + " registration reward propagation checks passed");
    }
}
