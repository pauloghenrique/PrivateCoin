using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PrivateCoin.Core;
using PrivateCoin.Desktop;

internal static class PrivateCoinFinalityRegression
{
    private static int checks;
    public static int Main(string[] args)
    {
        try
        {
            string directory = args.Length > 0 ? args[0] : Path.Combine("work", "finality-regression");
            Directory.CreateDirectory(directory);
            using (var fixture = new Fixture())
            {
                CheckQuorum(fixture, directory);
                CheckForks(fixture);
                CheckPersistence(fixture, directory);
                CheckPeerVotes(fixture, directory).GetAwaiter().GetResult();
            }
            using (var weighted = new Fixture(5, 1, 1))
            {
                var chain = weighted.NewTarget();
                Check(chain.AddFinalityVote(chain.CreateFinalityVote(chain.NextFinalityHeight, weighted.Signers[0])),
                    "one signer with 5/7 of preceding stake finalizes despite representing only 1/3 of validators");
            }
            using (var boundary = new Fixture(4, 1, 1))
            {
                var chain = boundary.NewTarget();
                int height = chain.NextFinalityHeight;
                Check(!chain.AddFinalityVote(chain.CreateFinalityVote(height, boundary.Signers[0])),
                    "a signer with exactly 4/6 of weighted stake still cannot finalize");
                Check(chain.AddFinalityVote(chain.CreateFinalityVote(height, boundary.Signers[1])),
                    "5/6 of weighted stake advances the checkpoint");
            }
            var noStake = new Blockchain();
            noStake.AddBlock(new Transaction[0]);
            Check(noStake.NextFinalityHeight == -1 && noStake.FinalizedHeight == 0,
                "a state with no active stake cannot finalize new blocks");
            CheckPriorStake();
            Console.WriteLine(checks + " finality checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Wallet[] Wallets = { new Wallet(), new Wallet(), new Wallet() };
        public readonly Blockchain Base = new Blockchain();
        public readonly ValidatorStake[] Signers;
        public Fixture(params int[] amounts)
        {
            if (amounts.Length == 0) amounts = new[] { 1, 1, 1 };
            var addresses = Wallets.Select(w => w.CreateReceiveAddress()).ToArray();
            Block reward;
            foreach (string address in addresses) Base.TryAddWalletCreationReward(address, out reward);
            for (int i = 0; i < Wallets.Length; i++)
                Base.AddBlock(new[] { Wallets[i].CreateStakeLockTransaction(Base, new Transaction[0], addresses[i], amounts[i] * Blockchain.OneCoin, 1) });
            Signers = Wallets.Select((w, i) => w.CreateValidatorStake(addresses[i], amounts[i] * Blockchain.OneCoin)).ToArray();
            FinalityTestSupport.FinalizeAvailable(Base, Signers);
        }
        public Blockchain NewTarget()
        {
            var chain = new Blockchain(Base.Blocks, Base.LocalNodeId, Base.GetFinalityState());
            chain.AddBlock(new Transaction[0]);
            return chain;
        }
        public void Dispose() { foreach (Wallet wallet in Wallets) wallet.Dispose(); }
    }

    private static void CheckQuorum(Fixture fixture, string directory)
    {
        var chain = fixture.NewTarget();
        int parent = chain.FinalizedHeight, height = chain.NextFinalityHeight;
        var votes = fixture.Signers.Select(v => chain.CreateFinalityVote(height, v)).ToArray();
        var inflated = fixture.Wallets[0].CreateValidatorStake(fixture.Signers[0].RewardAddress, long.MaxValue);
        var authoritative = fixture.NewTarget();
        Check(!authoritative.AddFinalityVote(authoritative.CreateFinalityVote(authoritative.NextFinalityHeight, inflated)),
            "a signer cannot inflate its voting weight through a locally declared locked amount");
        Check(!chain.AddFinalityVote(votes[0]) && chain.FinalizedHeight == parent, "1/3 of stake does not finalize");
        Check(!chain.AddFinalityVote(votes[0]), "a repeated validator vote contributes no additional stake");
        Check(!chain.AddFinalityVote(votes[1]) && chain.FinalizedHeight == parent, "exactly 2/3 of stake does not finalize");
        var pendingRestored = new Blockchain(chain.Blocks, chain.LocalNodeId, chain.GetFinalityState());
        Check(pendingRestored.FinalizedHeight == parent && pendingRestored.GetFinalityState().PendingVotes.Count == 2,
            "sub-quorum votes survive snapshot restoration without prematurely finalizing");
        Check(pendingRestored.AddFinalityVote(votes[2]) && pendingRestored.FinalizedHeight == height &&
            pendingRestored.FinalizedHash == chain.Blocks[height].Hash, "strictly more than 2/3 finalizes the exact block hash");
        var synchronized = new Blockchain(chain.Blocks, chain.LocalNodeId, fixture.Base.GetFinalityState());
        foreach (FinalityVote vote in votes)
        {
            var independent = new Blockchain(chain.Blocks, chain.LocalNodeId, fixture.Base.GetFinalityState());
            independent.AddFinalityVote(vote);
            bool changed;
            Check(synchronized.TrySynchronizeChain(independent.Blocks, independent.GetFinalityState(), out changed) && changed,
                "synchronization retains a new independent partial vote on an identical block chain");
        }
        Check(synchronized.FinalizedHeight == height,
            "partial votes received from separate snapshots combine into a valid finality certificate");
        var forged = new FinalityVote { Height = votes[2].Height, BlockHash = votes[2].BlockHash,
            PreviousHash = votes[2].PreviousHash, PublicKey = votes[2].PublicKey, Signature = Convert.ToBase64String(new byte[256]) };
        Reject(() => chain.AddFinalityVote(forged), "an invalid RSA signature is rejected");
        forged.Signature = votes[2].Signature;
        forged.BlockHash = new string('a', 64);
        Reject(() => chain.AddFinalityVote(forged), "a vote cannot be reused for another block hash");
        forged.BlockHash = votes[2].BlockHash;
        forged.PreviousHash = new string('b', 64);
        Reject(() => chain.AddFinalityVote(forged), "a vote cannot be reused with another parent hash");
        forged.PreviousHash = votes[2].PreviousHash;
        forged.PublicKey = "<RSAKeyValue>";
        Reject(() => chain.AddFinalityVote(forged), "an unknown malformed public key is rejected before signature parsing");
        forged.PublicKey = votes[2].PublicKey;
        var reminted = fixture.NewTarget();
        forged.BlockHash = reminted.Blocks.Last().Hash;
        forged.PreviousHash = reminted.Blocks[height - 1].Hash;
        Reject(() => reminted.AddFinalityVote(forged), "a valid alternate block cannot reuse a signature over the original block hash");
        FinalityState invalid = pendingRestored.GetFinalityState();
        invalid.Certificates.Last().Votes.Add(invalid.Certificates.Last().Votes[0]);
        Reject(() => new Blockchain(pendingRestored.Blocks, pendingRestored.LocalNodeId, invalid), "duplicate certificate signers are rejected");
        invalid = chain.GetFinalityState();
        invalid.FinalizedHeight = height;
        invalid.FinalizedHash = chain.Blocks[height].Hash;
        Reject(() => new Blockchain(chain.Blocks, chain.LocalNodeId, invalid), "height/hash announcements without quorum evidence are rejected");

        string journalPath = Path.Combine(directory, "local-votes.dat");
        var journal = new FinalityVoteJournal(journalPath);
        FinalityVote own = journal.GetOrCreate(chain, height, fixture.Signers[0]);
        Check(new FinalityVoteJournal(journalPath).GetOrCreate(chain, height, fixture.Signers[0]).Signature == own.Signature,
            "the local voting journal persists and reuses the original signed vote after restart");
        var fork = fixture.NewTarget();
        Reject(() => new FinalityVoteJournal(journalPath).GetOrCreate(fork, height, fixture.Signers[0]),
            "the journal refuses to sign a conflicting block at the same height after restart");
        var later = new Blockchain(chain.Blocks, chain.LocalNodeId, pendingRestored.GetFinalityState());
        later.AddBlock(new Transaction[0]);
        journal.GetOrCreate(later, later.NextFinalityHeight, fixture.Signers[0]);
        Reject(() => journal.GetOrCreate(chain, height, fixture.Signers[0]), "a signer cannot return to an older voting height");
        File.WriteAllText(journalPath, "[{}]");
        Reject(() => new FinalityVoteJournal(journalPath).GetOrCreate(chain, height, fixture.Signers[0]),
            "a corrupted local journal blocks signing instead of recreating a fresh identity");
        Check(File.ReadAllText(journalPath) == "[{}]", "a failed journal validation leaves the voting record unchanged");
    }

    private static void CheckForks(Fixture fixture)
    {
        var finalized = fixture.NewTarget();
        FinalityTestSupport.FinalizeAvailable(finalized, fixture.Signers);
        int checkpoint = finalized.FinalizedHeight;
        finalized.AddBlock(new Transaction[0]);
        finalized.AddBlock(new Transaction[0]);
        string tip = finalized.Blocks.Last().Hash;
        foreach (int length in new[] { finalized.Blocks.Count - 2, finalized.Blocks.Count, finalized.Blocks.Count + 1 })
        {
            var fork = fixture.NewTarget();
            while (fork.Blocks.Count < length) fork.AddBlock(new Transaction[0]);
            FinalityTestSupport.FinalizeAvailable(fork, fixture.Signers);
            Reject(() => finalized.TryReplaceChain(fork.Blocks, fork.GetFinalityState()),
                "a conflicting chain of length " + length + " is rejected even with valid competing certificates");
            Check(finalized.FinalizedHeight == checkpoint && finalized.Blocks.Last().Hash == tip,
                "rejecting the fork preserves the checkpoint and complete local chain");
        }
        var tentative = fixture.NewTarget();
        var longerTentative = fixture.NewTarget();
        longerTentative.AddBlock(new Transaction[0]);
        Check(tentative.TryReplaceChain(longerTentative.Blocks, longerTentative.GetFinalityState()) &&
            tentative.FinalizedHeight == fixture.Base.FinalizedHeight, "a longer unfinalized suffix may change above the checkpoint");
        var certifiedShorter = fixture.NewTarget();
        FinalityTestSupport.FinalizeAvailable(certifiedShorter, fixture.Signers);
        Check(tentative.TryReplaceChain(certifiedShorter.Blocks, certifiedShorter.GetFinalityState()) &&
            tentative.FinalizedHash == certifiedShorter.FinalizedHash,
            "a newer verified checkpoint wins over a longer unfinalized suffix");
        var same = new Blockchain(certifiedShorter.Blocks, certifiedShorter.LocalNodeId);
        Check(same.TryReplaceChain(certifiedShorter.Blocks, certifiedShorter.GetFinalityState()) &&
            same.FinalizedHeight == certifiedShorter.FinalizedHeight, "an identical block chain can import new finality evidence");
        Check(!same.TryReplaceChain(fixture.Base.Blocks, fixture.Base.GetFinalityState()) &&
            same.FinalizedHeight == certifiedShorter.FinalizedHeight, "an older matching prefix never rolls back a checkpoint");
        var gap = fixture.NewTarget();
        FinalityTestSupport.FinalizeAvailable(gap, fixture.Signers);
        int skipped = gap.FinalizedHeight;
        gap.AddBlock(new Transaction[0]);
        FinalityTestSupport.FinalizeAvailable(gap, fixture.Signers);
        FinalityState missingParent = gap.GetFinalityState();
        missingParent.Certificates.RemoveAll(c => c.Height == skipped);
        Reject(() => new Blockchain(gap.Blocks, gap.LocalNodeId, missingParent),
            "even a valid supermajority certificate cannot skip finalization of its preceding parent");
    }

    private static void CheckPersistence(Fixture fixture, string directory)
    {
        var chain = fixture.NewTarget();
        FinalityTestSupport.FinalizeAvailable(chain, fixture.Signers);
        string path = Path.Combine(directory, "core-network.json");
        BlockchainStateStore.Save(path, chain);
        var restored = BlockchainStateStore.Load(path, chain.LocalNodeId);
        Check(restored.FinalizedHeight == chain.FinalizedHeight && restored.FinalizedHash == chain.FinalizedHash,
            "atomic Core storage restores and revalidates the finalized height, hash and signatures");
        byte[] bytes = File.ReadAllBytes(path);
        Reject(() => BlockchainStateStore.Save(path, fixture.Base), "atomic Core storage refuses to overwrite a newer saved checkpoint");
        Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "rejecting a stale Core save preserves the exact existing file");
        var store = new WalletStore(Path.Combine(directory, "desktop"));
        store.SaveNetwork(chain, new Transaction[0]);
        System.Collections.Generic.List<Transaction> pending;
        var desktop = new WalletStore(Path.Combine(directory, "desktop")).LoadNetwork(out pending);
        Check(desktop.FinalizedHeight == chain.FinalizedHeight && desktop.FinalizedHash == chain.FinalizedHash,
            "the actual Desktop public store restores the finalized checkpoint after restart");
        string desktopPath = Path.Combine(directory, "desktop", "Blockchain.json");
        bytes = File.ReadAllBytes(desktopPath);
        Reject(() => store.SaveNetwork(fixture.Base, new Transaction[0]), "the Desktop refuses to overwrite a newer saved checkpoint");
        Check(bytes.SequenceEqual(File.ReadAllBytes(desktopPath)), "rejecting a stale Desktop save preserves the exact existing file");
        var fork = fixture.NewTarget();
        fork.AddBlock(new Transaction[0]);
        Reject(() => desktop.TryReplaceChain(fork.Blocks, fork.GetFinalityState()), "stored Desktop finality continues to reject conflicting forks");
    }

    private static void CheckPriorStake()
    {
        using (var fixture = new Fixture()) using (var newcomer = new Wallet())
        {
            var chain = fixture.NewTarget();
            Block reward;
            string address = newcomer.CreateReceiveAddress();
            chain.TryAddWalletCreationReward(address, out reward);
            FinalityTestSupport.FinalizeAvailable(chain, fixture.Signers);
            chain.AddBlock(new[] { newcomer.CreateStakeLockTransaction(chain, new Transaction[0], address, 5 * Blockchain.OneCoin, 1) });
            int height = chain.NextFinalityHeight;
            Check(chain.GetActiveValidators().Count == 4 && chain.GetFinalityValidators(height).Count == 3,
                "stake created in the target block is excluded from its voting denominator");
            Reject(() => chain.CreateFinalityVote(height, newcomer.CreateValidatorStake(address, 5 * Blockchain.OneCoin)),
                "a new stake holder cannot vote on the block activating its own stake");
            FinalityTestSupport.FinalizeAvailable(chain, fixture.Signers);
            chain.AddBlock(new Transaction[0]);
            Check(chain.GetFinalityValidators(chain.NextFinalityHeight).Count == 4,
                "new stake becomes eligible for the following block after activation is finalized");
        }
        using (var fixture = new Fixture())
        {
            var chain = fixture.NewTarget();
            FinalityTestSupport.FinalizeAvailable(chain, fixture.Signers);
            chain.AddBlock(new[] { fixture.Wallets[2].CreateStakeUnlockTransaction(chain, fixture.Signers[2].RewardAddress, 1) });
            int height = chain.NextFinalityHeight;
            Check(chain.GetActiveValidators().Count == 2 && chain.GetFinalityValidators(height).Count == 3,
                "stake removed in the target block remains in that block's preceding voting denominator");
            chain.AddFinalityVote(chain.CreateFinalityVote(height, fixture.Signers[0]));
            Check(!chain.AddFinalityVote(chain.CreateFinalityVote(height, fixture.Signers[1])),
                "withdrawing a validator cannot turn exactly 2/3 of preceding stake into a quorum");
            Check(chain.AddFinalityVote(chain.CreateFinalityVote(height, fixture.Signers[2])),
                "a withdrawing validator can still sign finality for its withdrawal block");
            chain.AddBlock(new Transaction[0]);
            Check(chain.GetFinalityValidators(chain.NextFinalityHeight).Count == 2,
                "withdrawn stake stops contributing starting with the following block");
        }
    }

    private static async Task CheckPeerVotes(Fixture fixture, string directory)
    {
        var original = fixture.NewTarget();
        var receiverChain = new Blockchain(original.Blocks, original.LocalNodeId, original.GetFinalityState());
        int height = original.NextFinalityHeight;
        int portA = Port(), portB = Port();
        while (portB == portA) portB = Port();
        using (var sender = new PeerNode(portA, false)) using (var receiver = new PeerNode(portB, false))
        {
            var complete = new TaskCompletionSource<bool>();
            receiver.FinalityVoteReceived += (source, args) => {
                try { if (receiverChain.AddFinalityVote(args.Vote)) complete.TrySetResult(true); }
                catch (Exception error) { complete.TrySetException(error); }
            };
            sender.Start(); receiver.Start();
            await sender.ConnectAsync("127.0.0.1", portB);
            foreach (ValidatorStake signer in fixture.Signers)
                await sender.BroadcastFinalityVoteAsync(original.CreateFinalityVote(height, signer));
            if (await Task.WhenAny(complete.Task, Task.Delay(10000)) != complete.Task) throw new Exception("Vote transport timed out.");
            await complete.Task;
            Check(receiverChain.FinalizedHeight == height, "real TCP gossip collects signed votes and advances finality at the receiver");
            var fresh = new Blockchain();
            var synced = new TaskCompletionSource<bool>();
            sender.ChainReceived += (source, args) => {
                try { synced.TrySetResult(fresh.TryReplaceChain(args.Blocks, args.Finality)); }
                catch (Exception error) { synced.TrySetException(error); }
            };
            await receiver.BroadcastChainAsync(receiverChain);
            if (await Task.WhenAny(synced.Task, Task.Delay(10000)) != synced.Task) throw new Exception("Certificate transport timed out.");
            Check(await synced.Task && fresh.FinalizedHash == receiverChain.FinalizedHash,
                "chain synchronization transmits and validates finalized certificates over real TCP");
        }
    }

    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
}
