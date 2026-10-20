using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using PrivateCoin.Core;

internal static class PrivateCoinTwentyYearEmissionRegression
{
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidOperationException) { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
    private static T Copy<T>(T value)
    { using (var stream = new MemoryStream()) { var json = new DataContractJsonSerializer(typeof(T)); json.WriteObject(stream, value); stream.Position = 0; return (T)json.ReadObject(stream); } }

    private static void Schedule()
    {
        Check(ProofOfStake.BlocksPerPhase == 15778800 && ProofOfStake.RewardedBlockCount == 63115200, "four five-year phases at ten seconds per remunerated block");
        Check((long)ProofOfStake.RewardedBlockCount * ProofOfWork.TargetBlockSeconds == 20L * 36525 * 864, "complete window represents twenty Julian years of remunerated blocks");
        Check(ProofOfStake.GetBlockReward(0) == 0 && ProofOfStake.GetBlockReward(-1) == 0, "no reward before first remunerated ordinal");
        long[] budgets = { 7220000L, 5600000L, 3000000L, 2000000L };
        long[] bases = { 45757598L, 35490658L, 19012852L, 12675235L };
        long[] extras = { 12677600L, 5549600L, 10862400L, 1982000L };
        long total = 0;
        foreach (RewardPhase phase in ProofOfStake.RewardPhases)
        {
            int i = phase.Number - 1;
            long budget = budgets[i] * Blockchain.OneCoin;
            Check(phase.FirstHeight == i * 15778800 + 1 && phase.LastHeight == (i + 1) * 15778800, "phase " + phase.Number + " ordinal boundaries");
            Check(phase.RewardPerBlock == bases[i] && phase.RemainderAtomicUnits == extras[i], "phase " + phase.Number + " exact base and atomic remainder");
            int lastBase = phase.LastHeight - (int)extras[i];
            Check(ProofOfStake.GetBlockReward(phase.FirstHeight) == bases[i] && ProofOfStake.GetBlockReward(lastBase) == bases[i], "phase " + phase.Number + " base segment includes its endpoints");
            Check(ProofOfStake.GetBlockReward(lastBase + 1) == bases[i] + 1 && ProofOfStake.GetBlockReward(phase.LastHeight) == bases[i] + 1, "phase " + phase.Number + " final segment pays exactly one extra atomic unit");
            long independentlySummed = (lastBase - phase.FirstHeight + 1L) * ProofOfStake.GetBlockReward(lastBase) +
                extras[i] * ProofOfStake.GetBlockReward(phase.LastHeight);
            Check(independentlySummed == budget && phase.TotalReward == budget, "phase " + phase.Number + " all block rewards sum to original budget exactly");
            Check(ProofOfStake.GetScheduledIssuance(lastBase) == total + (lastBase - phase.FirstHeight + 1L) * bases[i] &&
                ProofOfStake.GetScheduledIssuance(lastBase + 1) - ProofOfStake.GetScheduledIssuance(lastBase) == bases[i] + 1, "phase " + phase.Number + " partial issuance includes rounding boundary exactly");
            total += budget;
            Check(ProofOfStake.GetScheduledIssuance(phase.LastHeight) == total, "phase " + phase.Number + " cumulative budget is exact");
            var creator = new ValidatorStake("creator", "creator", 600L);
            var confirmer = new ValidatorStake("confirmer", "confirmer", 6L);
            foreach (int index in new[] { phase.FirstHeight, lastBase, lastBase + 1, phase.LastHeight })
            {
                var shares = ProofOfStake.DistributeReward(index, creator, new[] { confirmer });
                Check(shares.Sum(x => x.Amount) == ProofOfStake.GetBlockReward(index) && shares.Single(x => x.IsCreator).Amount == ProofOfStake.GetBlockReward(index) * 30 / 100,
                    "phase " + phase.Number + " creator/confirmer split conserves subsidy at ordinal " + index);
            }
        }
        Check(total == 17820000L * Blockchain.OneCoin && ProofOfStake.ScheduledIssuance == total && total == ProofOfStake.MaximumSupply, "all stages preserve exactly 17820000 POVIX");
        Check(ProofOfStake.GetBlockReward(63115201) == 0 && ProofOfStake.GetBlockReward(int.MaxValue) == 0 &&
            ProofOfStake.GetScheduledIssuance(int.MaxValue) == total, "exhausted schedule never creates further subsidy");
        foreach (var kind in new[] { TransactionKind.WalletCreate, TransactionKind.WalletDistribution, TransactionKind.InitialLaunchCreate })
            Check(Blockchain.GetValidatorBlockReward(1, new[] { new Transaction { Kind = kind } }) == 0, kind + " remains unpaid independently of reward ordinal");
        Check(ProofOfStake.GetBlockReward(1, 21) == 361L * Blockchain.OneCoin / 100 && ProofOfStake.GetBlockReward(2000001, 21) == 280L * Blockchain.OneCoin / 100 &&
            ProofOfStake.GetBlockReward(8000001, 21) == 0, "legacy calculation API retains its original height schedule");
    }

    private static Block Buy(Blockchain chain, Wallet buyer, string address, ValidatorStake signer)
    {
        var tx = buyer.CreateInitialLaunchPurchase(chain, new Transaction[0], address, address, 1);
        tx.TransactionApproval = chain.CreateTransactionApproval(tx, new[] { tx }, new[] { signer });
        return chain.AddProofOfStakeBlock(new[] { tx }, new[] { signer });
    }

    private static void Chain()
    {
        var empty = new Transaction[0];
        using (var founder = new Wallet()) using (var buyer = new Wallet()) using (var next = new Wallet())
        {
            string a = founder.CreateReceiveAddress(), b = buyer.CreateReceiveAddress(), c = next.CreateReceiveAddress();
            var chain = new Blockchain();
            chain.AddBlock(new[] { chain.CreateWalletCreationTransaction(a, empty) });
            var downgraded = Copy(chain.Blocks[1]);
            downgraded.ConsensusVersion = 21; downgraded.ProofOfWorkTarget = null;
            LegacyConsensusFixture.Mine(downgraded);
            Check(ProofOfWork.MeetsTarget(downgraded.Hash), "downgraded first child has valid legacy proof of work");
            Reject(() => new Blockchain(new[] { Copy(chain.Blocks[0]), downgraded }),
                "new genesis cannot start under an older emission consensus");
            var signer = founder.CreateValidatorStake(a, Blockchain.WalletCreationReward);
            chain.AddProofOfStakeBlock(new[] { chain.CreateWalletCreationTransaction(b, empty) }, new[] { signer });
            var initial = new BlockchainQueryApi(chain).GetSummary();
            Check(initial.RewardedBlockCount == 0 && initial.ValidatorIssuedTokenAmount == 0 && initial.CurrentReward == 45757598L,
                "bootstrap and signed registration leave first subsidy and reward index untouched");
            Check(initial.RemainingValidatorRewardAmount == ProofOfStake.MaximumSupply && initial.RewardedBlocksRemaining == 63115200, "initial emission budget and block count are intact");
            PusdtBridgeFixture.Seed(chain, founder, buyer, next);
            Check(Blockchain.GetRewardedBlockCount(chain.Blocks) == 0, "quorum test issuance leaves validator subsidy untouched");
            var beforePurchase = Copy(chain.Blocks.ToArray());
            Block first = Buy(chain, buyer, b, signer);
            Check(first.Height > 1 && first.Transactions[0].Outputs.Sum(x => x.Amount) == 45757598L && Blockchain.GetRewardedBlockCount(chain.Blocks) == 1,
                "first signed purchase pays reward ordinal one despite earlier unpaid heights");
            Check(chain.GetSpendableBalance(new[] { a }, empty) == 45757598L + first.Transactions[1].Fee,
                "subsidy and transaction fee are credited separately with no extra issuance");
            foreach (long wrong in new[] { 45757597L, 45757599L, 361L * Blockchain.OneCoin / 100, 35490658L })
            {
                Block forged = Copy(first); forged.Transactions[0].Outputs[0].Amount = wrong;
                forged.Transactions[0].Id = LegacyConsensusFixture.Id(forged.Transactions[0]);
                LegacyConsensusFixture.Resign(forged, new[] { signer }); LegacyConsensusFixture.Mine(forged);
                Reject(() => new Blockchain(Copy(beforePurchase).Concat(new[] { forged })), "replay rejects renewed-signature and valid-work reward amount " + wrong);
            }
            var fork = new Blockchain(Copy(chain.Blocks.ToArray()));
            chain.AddProofOfStakeBlock(new[] { chain.CreateWalletCreationTransaction(c, empty) }, new[] { signer });
            Block second = Buy(chain, next, c, signer);
            var summary = new BlockchainQueryApi(chain).GetSummary();
            Check(summary.RewardedBlockCount == 2 && summary.ValidatorIssuedTokenAmount == 91515196L &&
                summary.IssuedTokenAmount == Blockchain.MaximumSupply + 91515196L, "query counts only the two paid blocks, excluding registration and fees");
            Check(new BlockchainQueryApi(chain).GetLedger(0, 1000).Sum(x => x.IssuedAmount) == summary.IssuedTokenAmount,
                "ledger issuance and public summary agree across paid and unpaid blocks");
            Check(summary.CurrentReward == ProofOfStake.GetBlockReward(3) && summary.RemainingValidatorRewardAmount + summary.ValidatorIssuedTokenAmount == ProofOfStake.MaximumSupply,
                "query reports next remunerated subsidy and exact remaining budget");
            Check(new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(), "paid and unpaid blocks replay with identical emission");
            // More-work branch with only one paid block: confirmed reward count must be rebuilt, not cached by height.
            for (int i = 0; i < 4; i++)
                fork.AddProofOfStakeBlock(new[] { fork.CreateWalletCreationTransaction(i.ToString("x64"), empty) }, new[] { signer });
            Check(fork.Blocks.Count > chain.Blocks.Count && Blockchain.GetRewardedBlockCount(fork.Blocks) == 1,
                "longer alternate chain contains more unpaid blocks but only one paid block");
            Check(chain.TryReplaceChain(Copy(fork.Blocks.ToArray())), "node adopts valid greater-work alternative");
            summary = new BlockchainQueryApi(chain).GetSummary();
            Check(summary.RewardedBlockCount == 1 && summary.ValidatorIssuedTokenAmount == 45757598L &&
                summary.RemainingValidatorRewardAmount == ProofOfStake.MaximumSupply - 45757598L,
                "reorganization restores exact confirmed reward count and budget");
            var rules = chain.Blocks[0].Rules;
            Check(rules.SchemaVersion == 9 && rules.ConsensusVersion == 36 && rules.EmissionRule.Contains("not chain heights") &&
                rules.RewardPhases.Sum(x => x.TotalRewardAtomicUnits) == ProofOfStake.MaximumSupply && rules.RewardPhases[0].RemainderAtomicUnits == 12677600L &&
                CoreConfiguration.Manifest.Contains("RewardPhase.1.RemainderAtomicUnits") && CoreConfiguration.Manifest.Contains("RewardPhase.4.TotalReward"),
                "genesis binds reward ordinals, exact phase budgets and remainder rule");
            foreach (string property in new[] { "RemainderAtomicUnits", "TotalRewardAtomicUnits" })
            {
                Block genesis = Copy(chain.Blocks[0]);
                object phase = genesis.Rules.RewardPhases[0];
                var field = phase.GetType().GetProperty(property);
                field.GetSetMethod(true).Invoke(phase, new object[] { (long)field.GetValue(phase, null) + 1 });
                LegacyConsensusFixture.Mine(genesis);
                Reject(() => new Blockchain(new[] { genesis }), "remining genesis cannot alter exact " + property);
            }
            Check(chain.GetInitialLaunchLockedBalance(new[] { a }) == Blockchain.WalletCreationReward &&
                summary.MaximumTokenAmount == 18180000L * Blockchain.OneCoin, "initial collateral and overall maximum supply remain unchanged");
        }
    }

    public static void Main() { Schedule(); Chain(); Console.WriteLine(checks + " twenty-year emission checks passed"); }
}
