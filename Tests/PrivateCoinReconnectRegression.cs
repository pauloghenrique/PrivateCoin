using System;
using PrivateCoin.Core;
using PrivateCoin.Desktop;

internal static class PrivateCoinReconnectRegression
{
    public static int Main()
    {
        try
        {
            var readiness = new NetworkReadiness();
            bool connected = false;
            var local = new Blockchain();
            var remote = new Blockchain();
            Check(!readiness.IsReady, "startup requires synchronization");
            Reject(() => readiness.Execute(() => connected, () => Reward(local)));
            connected = true;
            Reject(() => readiness.Execute(() => connected, () => Reward(local)));
            long staleEpoch = readiness.Epoch;
            Check(readiness.Accept(staleEpoch, () => connected, () => Synchronize(local, remote.Blocks)),
                "an identical validated chain releases the node");
            readiness.Execute(() => connected, () => Reward(local));
            connected = false;
            readiness.Disconnect();
            Reject(() => readiness.Execute(() => connected, () => Reward(local)));
            connected = true;
            Check(!readiness.Accept(staleEpoch, () => connected, () => { throw new Exception("stale callback ran"); }),
                "a queued response from before disconnection cannot release the node");
            Reject(() => readiness.Execute(() => connected, () => Reward(local)));
            Check(!readiness.Accept(readiness.Epoch, () => connected, () => Synchronize(local, remote.Blocks)) && !readiness.IsReady,
                "a valid chain with less work cannot release synchronization");
            Reward(remote);
            Reward(remote);
            Check(readiness.Accept(readiness.Epoch, () => connected, () => Synchronize(local, remote.Blocks)) &&
                local.Blocks[local.Blocks.Count - 1].Hash == remote.Blocks[remote.Blocks.Count - 1].Hash,
                "reconnection adopts the valid longer network chain");
            readiness.Disconnect();
            var invalid = new Blockchain().Blocks;
            invalid[invalid.Count - 1].Hash = "invalid";
            Reject(() => readiness.Accept(readiness.Epoch, () => connected, () => Synchronize(local, invalid)));
            Check(!readiness.IsReady, "invalid chain cannot release synchronization");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static bool Synchronize(Blockchain chain, System.Collections.Generic.IEnumerable<Block> blocks)
    { bool changed; return chain.TrySynchronizeChain(blocks, out changed); }
    private static Block Reward(Blockchain chain)
    {
        using (var wallet = new Wallet())
        {
            Block reward;
            reward = LegacyConsensusFixture.Fund(chain, wallet.CreateReceiveAddress());
            return reward;
        }
    }
    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS operation blocked"); return; }
        throw new Exception("Expected operation rejection");
    }
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
}
