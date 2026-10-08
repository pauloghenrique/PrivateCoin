using System;
using PrivateCoin.Core;

internal static class PrivateCoinMandatoryFinalityRegression
{
    public static int Main(string[] args)
    {
        try
        {
            var inspection = PublicBlockchainSnapshot.Read(args[0]);
            bool configured = args.Length > 1 && args[1] == "configured";
            if (!configured)
            {
                Reject(() => FinalityPolicy.FromConfiguration(), "missing or disabled finality prevents network configuration");
                Reject(() => new Blockchain(), "default chain creation cannot silently select legacy consensus");
                Reject(() => new PeerNode(4778), "a peer cannot start without the required configured policy");
            }
            else
            {
                FinalityPolicy policy = FinalityPolicy.FromConfiguration();
                var chain = new Blockchain();
                Check(chain.Finality.Id == policy.Id, "default chain uses the configured voting policy");
                Reject(() => chain.AddBlock(new Transaction[0]), "an unsynchronized node cannot create blocks before the agreed checkpoint");
                var candidate = FinalityCheckpoint.Create(inspection);
                candidate.ValidateAgainst(new Blockchain(inspection.Blocks, policy));
                Check(policy.AnchorHash == candidate.BlockHash && policy.AnchorHeight == candidate.Height,
                    "the real configured checkpoint must match validated source data");
            }
            Reject(() => inspection.TryReplaceChain(inspection.Blocks), "inspection-only chains cannot adopt peer data or use longest-chain fallback");
            Check(FinalityCheckpoint.Create(inspection).Committee.Count >= 2, "read-only checkpoint preparation remains available before activation");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool result, string label)
    { if (!result) throw new Exception(label); Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label)
    { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception(label); }
}
