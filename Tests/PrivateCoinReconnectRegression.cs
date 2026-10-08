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
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            connected = true;
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            long staleEpoch = readiness.Epoch;
            Check(readiness.Accept(staleEpoch, () => connected, () => local.TryReplaceChain(remote.Blocks)),
                "an identical validated chain releases the node");
            readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0]));
            connected = false;
            readiness.Disconnect();
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            connected = true;
            Check(!readiness.Accept(staleEpoch, () => connected, () => { throw new Exception("stale callback ran"); }),
                "a queued response from before disconnection cannot release the node");
            Reject(() => readiness.Execute(() => connected, () => local.AddBlock(new Transaction[0])));
            remote.AddBlock(new Transaction[0]);
            remote.AddBlock(new Transaction[0]);
            Check(readiness.Accept(readiness.Epoch, () => connected, () => local.TryReplaceChain(remote.Blocks)) &&
                local.Blocks[local.Blocks.Count - 1].Hash == remote.Blocks[remote.Blocks.Count - 1].Hash,
                "reconnection adopts the valid longer network chain");
            readiness.Disconnect();
            var invalid = new Blockchain().Blocks;
            invalid[invalid.Count - 1].Hash = "invalid";
            Reject(() => readiness.Accept(readiness.Epoch, () => connected, () => local.TryReplaceChain(invalid)));
            Check(!readiness.IsReady, "invalid chain cannot release synchronization");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
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
