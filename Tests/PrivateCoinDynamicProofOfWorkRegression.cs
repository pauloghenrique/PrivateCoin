using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Serialization.Json;
using PrivateCoin.Core;

internal static class PrivateCoinDynamicProofOfWorkRegression
{
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label) { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } catch (ArgumentException) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private static T Copy<T>(T value) { using (var stream = new MemoryStream()) { var json = new DataContractJsonSerializer(typeof(T)); json.WriteObject(stream, value); stream.Position = 0; return (T)json.ReadObject(stream); } }
    // Synthetic header windows test the arithmetic only, not transaction validity.
    private static List<Block> Window(int seconds, BigInteger target)
    {
        var list = new List<Block> { new Block { Height = 0, Hash = new string('0', 64), TimestampUtcTicks = DateTime.UtcNow.AddHours(-2).Ticks } };
        long first = DateTime.UtcNow.AddHours(-1).Ticks;
        for (int i = 1; i <= ProofOfWork.RetargetWindow; i++) list.Add(new Block { Height = i, Hash = i.ToString("x64"), ConsensusVersion = Blockchain.ConsensusVersion,
            TimestampUtcTicks = first + (i - 1L) * seconds * TimeSpan.TicksPerSecond, ProofOfWorkTarget = ProofOfWork.FormatTarget(target) });
        return list;
    }
    private static Block Header(IReadOnlyList<Block> history, long time)
    {
        var block = new Block { Height = history.Count, ConsensusVersion = Blockchain.ConsensusVersion, PreviousHash = history[history.Count - 1].Hash,
            TimestampUtcTicks = time, ProofOfWorkTarget = ProofOfWork.FormatTarget(ProofOfWork.GetRequiredTarget(history)) };
        LegacyConsensusFixture.Mine(block); return block;
    }
    public static void Main()
    {
        var fast = Window(2, ProofOfWork.InitialTarget); var normal = Window(10, ProofOfWork.InitialTarget); var slow = Window(30, ProofOfWork.InitialTarget);
        BigInteger expectedFast = (ProofOfWork.InitialTarget + 1) / 4 - 1;
        Check(ProofOfWork.GetRequiredTarget(fast) == expectedFast, "2-second blocks increase difficulty, bounded to fourfold per window");
        Check(ProofOfWork.GetRequiredTarget(normal) == ProofOfWork.InitialTarget, "10-second blocks leave difficulty unchanged");
        Check(ProofOfWork.GetRequiredTarget(slow) == (ProofOfWork.InitialTarget + 1) * 3 - 1, "30-second blocks reduce difficulty threefold");
        Check(ProofOfWork.GetRequiredTarget(Window(100, ProofOfWork.InitialTarget)) == ProofOfWork.Target, "slow adjustment respects the fourfold cap and 12-bit minimum");
        Check(ProofOfWork.GetRequiredTarget(Window(30, ProofOfWork.Target)) == ProofOfWork.Target, "minimum difficulty cannot be weakened further");
        Check(ProofOfWork.GetRequiredTarget(Window(1, BigInteger.One)) == BigInteger.One, "maximum difficulty never produces a zero target");
        Check(ProofOfWork.GetRequiredTarget(normal.Take(30).ToArray()) == ProofOfWork.InitialTarget, "first 30 mined blocks use initial difficulty");
        long expectedTime = normal.Last().TimestampUtcTicks + TimeSpan.FromSeconds(10).Ticks;
        var block = Header(normal, expectedTime);
        ProofOfWork.ValidateHeader(block, normal, DateTime.UtcNow); Check(true, "header at adjustment boundary verifies against its history");
        normal.Add(block);
        Check(ProofOfWork.GetRequiredTarget(normal) == ProofOfWork.GetBlockTarget(block), "target remains unchanged between adjustment boundaries");
        Check(ProofOfWork.GetBlockWork(block) == new BigInteger(16384), "initial work counts the required 14-bit target");
        var hard = Header(fast, fast.Last().TimestampUtcTicks + TimeSpan.FromSeconds(2).Ticks);
        Check(ProofOfWork.GetBlockWork(hard) == 4 * ProofOfWork.GetBlockWork(block), "harder blocks contribute proportionally more cumulative work");
        Check(ProofOfWork.GetBlockWork(hard) > 3 * ProofOfWork.WorkPerBlock, "fewer hard blocks can outweigh several easy legacy blocks");
        var wrong = Copy(hard); wrong.ProofOfWorkTarget = ProofOfWork.FormatTarget(ProofOfWork.Target); LegacyConsensusFixture.Mine(wrong);
        Check(ProofOfWork.MeetsTarget(wrong.Hash), "forged easier header has valid legacy proof of work");
        Reject(() => ProofOfWork.ValidateHeader(wrong, fast, DateTime.UtcNow), "node rejects a miner-selected easier target despite valid work");
        var missing = Copy(block); missing.ProofOfWorkTarget = null;
        Reject(() => ProofOfWork.GetBlockTarget(missing), "dynamic blocks require their target");
        missing.ProofOfWorkTarget = new string('0', 64); Reject(() => ProofOfWork.GetBlockTarget(missing), "zero target is rejected");
        missing.ProofOfWorkTarget = new string('f', 64); Reject(() => ProofOfWork.GetBlockTarget(missing), "target above protocol maximum is rejected");
        missing.ProofOfWorkTarget = "ABC"; Reject(() => ProofOfWork.GetBlockTarget(missing), "noncanonical target encoding is rejected");
        var history = Window(10, ProofOfWork.InitialTarget); DateTime now = DateTime.UtcNow;
        var backward = Header(history, history.Last().TimestampUtcTicks);
        Reject(() => ProofOfWork.ValidateHeader(backward, history, now), "block timestamps must strictly advance");
        var future = Header(history, now.AddSeconds(ProofOfWork.MaximumFutureSeconds + 1).Ticks);
        Reject(() => ProofOfWork.ValidateHeader(future, history, now), "timestamp beyond future drift is rejected");
        Check(ProofOfWork.GetNextTimestamp(history, now) == now.Ticks, "producer uses UTC when it is ahead of the parent");
        history[history.Count - 1].TimestampUtcTicks = now.AddSeconds(5).Ticks;
        Check(ProofOfWork.GetNextTimestamp(history, now) == history.Last().TimestampUtcTicks + 1, "producer advances parent time within allowed clock drift");
        history[history.Count - 1].TimestampUtcTicks = now.AddSeconds(ProofOfWork.MaximumFutureSeconds + 1).Ticks;
        Reject(() => ProofOfWork.GetNextTimestamp(history, now), "producer refuses a parent beyond its clock tolerance");
        using (var owner = new Wallet()) using (var second = new Wallet())
        {
            var chain = new Blockchain(); var empty = new Transaction[0]; string a = owner.CreateReceiveAddress();
            chain.AddBlock(new[] { chain.CreateWalletCreationTransaction(a, empty) });
            var stake = owner.CreateValidatorStake(a, Blockchain.WalletCreationReward);
            var registration = chain.CreateWalletCreationTransaction(second.CreateReceiveAddress(), empty);
            var signed = chain.AddProofOfStakeBlock(new[] { registration }, new[] { stake });
            Check(chain.IsValid() && new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(), "real bootstrap and signed unpaid registration replay with dynamic headers");
            Check(signed.Transactions[0].Outputs.Count == 0, "dynamic work preserves zero validator reward for registrations");
            Check(chain.ChainWork == ProofOfWork.WorkPerBlock + 2 * new BigInteger(16384), "chain work sums each block's required target including legacy genesis");
            Check(chain.GetConfirmationWork(registration.Id) == ProofOfWork.GetBlockWork(signed), "confirmation work uses the actual required target");
            Check(new BlockchainQueryApi(chain).GetSummary().ChainWork == chain.ChainWork.ToString(), "public explorer reports the same cumulative work");
            var tampered = Copy(chain.Blocks.ToArray()); tampered[2].TimestampUtcTicks++;
            LegacyConsensusFixture.Mine(tampered[2]);
            Reject(() => new Blockchain(tampered), "changing the signed timestamp without a new vote is rejected");
            tampered = Copy(chain.Blocks.ToArray()); tampered[2].ProofOfWorkTarget = ProofOfWork.FormatTarget(ProofOfWork.Target);
            LegacyConsensusFixture.Resign(tampered[2], new[] { stake }); LegacyConsensusFixture.Mine(tampered[2]);
            Reject(() => new Blockchain(tampered), "full chain replay rejects an easier target even with renewed signatures");
            tampered = Copy(chain.Blocks.ToArray()); tampered[0].ProofOfWorkTarget = ProofOfWork.FormatTarget(ProofOfWork.InitialTarget);
            Reject(() => new Blockchain(tampered), "genesis cannot smuggle an unused target field");
            Check(chain.Blocks[0].Rules.TargetBlockSeconds == 10 && chain.Blocks[0].Rules.ProofOfWorkRetargetWindow == 30, "genesis commits adjustment parameters");
        }
        Console.WriteLine(checks + " dynamic proof-of-work checks passed");
    }
}
