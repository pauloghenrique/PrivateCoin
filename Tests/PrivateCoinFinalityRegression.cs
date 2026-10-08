using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PrivateCoin.Core;

internal static class PrivateCoinFinalityRegression
{
    private static int checks;
    public static int Main(string[] args)
    {
        string directory = args.Length == 0 ? "work/finality-regression" : args[0];
        Directory.CreateDirectory(directory);
        var wallets = Enumerable.Range(0, 4).Select(i => new Wallet()).ToArray();
        try
        {
            var bootstrap = new Blockchain((FinalityPolicy)null);
            var signers = new List<ValidatorStake>();
            foreach (Wallet wallet in wallets)
            {
                string address = wallet.CreateReceiveAddress();
                Block reward;
                bootstrap.TryAddWalletCreationReward(address, out reward);
                bootstrap.AddBlock(new[] { wallet.CreateStakeLockTransaction(bootstrap, new Transaction[0], address, Blockchain.OneCoin, 1) });
                signers.Add(wallet.CreateValidatorStake(address, Blockchain.OneCoin));
            }
            using (var fixture = File.Create(Path.Combine(directory, "bootstrap-public.json")))
                new DataContractJsonSerializer(typeof(Block[])).WriteObject(fixture, bootstrap.Blocks.ToArray());
            var policy = new FinalityPolicy(bootstrap.Blocks.Count - 1, bootstrap.Blocks.Last().Hash);
            var offline = new Blockchain(bootstrap.Blocks, null);
            for (int i = 0; i < 5; i++) offline.AddBlock(new Transaction[0]);
            var returning = new Blockchain(offline.Blocks, policy);
            var online = new Blockchain(bootstrap.Blocks, policy);
            var proposalChain = new Blockchain(bootstrap.Blocks, policy);
            Block proposal = proposalChain.AddBlock(new Transaction[0]);
            using (var journal = new FinalityVoteJournal(Path.Combine(directory, "votes.journal")))
            {
                var coordinator = new FinalityCoordinator(policy, journal);
                coordinator.Observe(online, proposalChain.Blocks, signers.Take(2));
                Check(!coordinator.TryFinalize(online), "half of the registered stake cannot finalize");
                coordinator.Observe(online, proposalChain.Blocks, signers.Take(3));
                Check(coordinator.TryFinalize(online), "three of four distinct equal-stake validators finalize");
                Check(returning.TryReplaceChain(online.Blocks) && returning.Blocks.Count == online.Blocks.Count &&
                    returning.Blocks.Last().Hash == proposal.Hash, "a shorter certified online chain overrides a longer uncertified local chain");
                Check(!returning.TryReplaceChain(offline.Blocks), "an uncertified longer chain cannot replace finality");
                Check(new Blockchain(online.Blocks, policy).FinalizedHeight == proposal.Height, "certificates survive JSON serialization and restart");
                Reject(() => journal.Sign(policy, new Block { Height = proposal.Height, Hash = new string('a', 64) }, signers[0]),
                    "a signer cannot vote twice for conflicting blocks at one height");

                Block[] duplicated = Clone(online.Blocks);
                duplicated.Last().FinalityVotes.Add(duplicated.Last().FinalityVotes[0]);
                Reject(() => new Blockchain(duplicated, policy), "duplicate vote cannot inflate quorum");
                Block[] tampered = Clone(online.Blocks);
                tampered.Last().FinalityVotes[0].Signature = "AAAA";
                Reject(() => new Blockchain(tampered, policy), "forged signatures are rejected");
                Block[] wrongTarget = Clone(online.Blocks);
                wrongTarget.Last().FinalityVotes[0].BlockHash = new string('b', 64);
                Reject(() => new Blockchain(wrongTarget, policy), "vote target is bound to the exact block hash");
                var otherPolicy = new FinalityPolicy(bootstrap.Blocks.Count - 2, bootstrap.Blocks[bootstrap.Blocks.Count - 2].Hash);
                Reject(() => new Blockchain(online.Blocks, otherPolicy), "votes cannot replay into another activation policy");

                var competing = new Blockchain(bootstrap.Blocks, policy);
                Block competitor;
                competing.TryAddWalletCreationReward(wallets[0].CreateReceiveAddress(), out competitor);
                using (var malicious = new FinalityVoteJournal(Path.Combine(directory, "malicious-separate-keys.journal")))
                {
                    competitor.FinalityVotes = signers.Take(3).Select(v => malicious.Sign(policy, competitor, v)).ToList();
                    // Deliberately simulate Byzantine double-signing with a separate
                    // journal. A node must fail closed, never switch finality.
                    Reject(() => returning.TryReplaceChain(competing.Blocks), "conflicting quorum certificates stop chain replacement");
                }

                var pos = new Blockchain(online.Blocks, policy);
                ValidatorStake creator = ProofOfStake.SelectCreator(signers, pos.Blocks.Last().Hash, pos.Blocks.Count);
                pos.AddProofOfStakeBlock(new Transaction[0], new[] { signers.Single(v => v.PublicKey == creator.PublicKey) });
                Check(new Blockchain(pos.Blocks, policy).IsValid(), "a single selected creator can propose; distributed finality is collected separately");
                Reject(() => pos.AddBlock(new Transaction[0]), "a second proposal cannot extend an uncertified tip");
            }
            var three = new Blockchain((FinalityPolicy)null);
            for (int i = 0; i < 3; i++)
            {
                Block reward;
                three.TryAddWalletCreationReward(signers[i].RewardAddress, out reward);
                three.AddBlock(new[] { wallets[i].CreateStakeLockTransaction(three, new Transaction[0], signers[i].RewardAddress, Blockchain.OneCoin, 1) });
            }
            var threePolicy = new FinalityPolicy(three.Blocks.Count - 1, three.Blocks.Last().Hash);
            var threeLocal = new Blockchain(three.Blocks, threePolicy);
            var threeProposal = new Blockchain(three.Blocks, threePolicy);
            threeProposal.AddBlock(new Transaction[0]);
            using (var journal = new FinalityVoteJournal(Path.Combine(directory, "strict-threshold.journal")))
            {
                var coordinator = new FinalityCoordinator(threePolicy, journal);
                coordinator.Observe(threeLocal, threeProposal.Blocks, signers.Take(2));
                Check(!coordinator.TryFinalize(threeLocal), "exactly two thirds of stake is insufficient");
                coordinator.Observe(threeLocal, threeProposal.Blocks, signers.Take(3));
                Check(coordinator.TryFinalize(threeLocal), "stake strictly above two thirds finalizes");
            }
            var weighted = new Blockchain((FinalityPolicy)null);
            var weightedSigners = new List<ValidatorStake>();
            for (int i = 0; i < 3; i++)
            {
                long stake = (i == 0 ? 5 : 1) * Blockchain.OneCoin;
                Block reward;
                weighted.TryAddWalletCreationReward(signers[i].RewardAddress, out reward);
                weighted.AddBlock(new[] { wallets[i].CreateStakeLockTransaction(weighted, new Transaction[0], signers[i].RewardAddress, stake, 1) });
                weightedSigners.Add(wallets[i].CreateValidatorStake(signers[i].RewardAddress, stake));
            }
            var weightedPolicy = new FinalityPolicy(weighted.Blocks.Count - 1, weighted.Blocks.Last().Hash);
            var weightedLocal = new Blockchain(weighted.Blocks, weightedPolicy);
            var weightedProposal = new Blockchain(weighted.Blocks, weightedPolicy);
            weightedProposal.AddBlock(new Transaction[0]);
            using (var journal = new FinalityVoteJournal(Path.Combine(directory, "weighted.journal")))
            {
                var alone = new FinalityCoordinator(weightedPolicy, journal);
                alone.Observe(weightedLocal, weightedProposal.Blocks, weightedSigners.Take(1));
                Check(!alone.TryFinalize(weightedLocal), "one key alone cannot finalize even with over two thirds of stake");
                var coordinator = new FinalityCoordinator(weightedPolicy, journal);
                coordinator.Observe(weightedLocal, weightedProposal.Blocks, weightedSigners.Skip(1));
                Check(!coordinator.TryFinalize(weightedLocal), "a majority of validator identities without stake quorum cannot finalize");
                coordinator.Observe(weightedLocal, weightedProposal.Blocks, weightedSigners.Take(1));
                Check(coordinator.TryFinalize(weightedLocal), "quorum uses registered stake weights");
            }
            using (var reopened = new FinalityVoteJournal(Path.Combine(directory, "votes.journal")))
                Reject(() => reopened.Sign(policy, new Block { Height = proposal.Height, Hash = new string('c', 64) }, signers[0]),
                    "restart retains the double-vote lock");
            File.AppendAllText(Path.Combine(directory, "votes.journal"), "torn-record");
            RejectAny(() => new FinalityVoteJournal(Path.Combine(directory, "votes.journal")), "corrupt journal fails closed");
            if (args.Length > 1 && args[1] == "--p2p") RunNetwork(directory, bootstrap, policy, signers.ToArray());
            Console.WriteLine(checks + " finality checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { foreach (Wallet wallet in wallets) wallet.Dispose(); }
    }
    private static void RunNetwork(string directory, Blockchain bootstrap, FinalityPolicy policy, ValidatorStake[] signers)
    {
        object gate = new object();
        var errors = new List<Exception>();
        var left = new Blockchain(bootstrap.Blocks, policy);
        var right = new Blockchain(bootstrap.Blocks, policy);
        var proposal = new Blockchain(bootstrap.Blocks, policy);
        proposal.AddBlock(new Transaction[0]);
        int leftPort = Port(), rightPort = Port(), returningPort = Port();
        using (var leftJournal = new FinalityVoteJournal(Path.Combine(directory, "left-votes.journal")))
        using (var rightJournal = new FinalityVoteJournal(Path.Combine(directory, "right-votes.journal")))
        using (var leftNode = new PeerNode(leftPort, false, null, policy))
        using (var rightNode = new PeerNode(rightPort, false, null, policy))
        using (var returningNode = new PeerNode(returningPort, false, null, policy))
        {
            var leftCoordinator = new FinalityCoordinator(policy, leftJournal);
            var rightCoordinator = new FinalityCoordinator(policy, rightJournal);
            Action<PeerNode, Blockchain, FinalityCoordinator, ValidatorStake[], Block[], bool> observe = (node, chain, coordinator, keys, blocks, retry) =>
            {
                chain.TryReplaceChain(blocks);
                var outbound = coordinator.Observe(chain, blocks, keys, retry);
                if (outbound.Count > 0)
                {
                    _ = node.BroadcastChainAsync(blocks);
                    foreach (var vote in outbound) _ = node.BroadcastFinalityVoteAsync(vote);
                }
                if (coordinator.TryFinalize(chain)) _ = node.BroadcastChainAsync(chain.Blocks);
            };
            leftNode.ChainReceived += (sender, e) => { lock (gate) try { observe(leftNode, left, leftCoordinator, signers.Take(2).ToArray(), e.Blocks, false); } catch (Exception ex) { errors.Add(ex); } };
            rightNode.ChainReceived += (sender, e) => { lock (gate) try { observe(rightNode, right, rightCoordinator, signers.Skip(2).ToArray(), e.Blocks, false); } catch (Exception ex) { errors.Add(ex); } };
            leftNode.FinalityVoteReceived += (sender, e) => { lock (gate) { if (leftCoordinator.Receive(left, e.Vote) && leftCoordinator.TryFinalize(left)) _ = leftNode.BroadcastChainAsync(left.Blocks); } };
            rightNode.FinalityVoteReceived += (sender, e) => { lock (gate) { if (rightCoordinator.Receive(right, e.Vote) && rightCoordinator.TryFinalize(right)) _ = rightNode.BroadcastChainAsync(right.Blocks); } };
            leftNode.SynchronizationRequested += (sender, e) => { lock (gate) _ = leftNode.BroadcastChainAsync(left.Blocks); };
            rightNode.SynchronizationRequested += (sender, e) => { lock (gate) _ = rightNode.BroadcastChainAsync(right.Blocks); };
            leftNode.Start(); rightNode.Start(); returningNode.Start();
            rightNode.ConnectAsync("127.0.0.1", leftPort).GetAwaiter().GetResult();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate)
                {
                    observe(leftNode, left, leftCoordinator, signers.Take(2).ToArray(), proposal.Blocks.ToArray(), true);
                    observe(rightNode, right, rightCoordinator, signers.Skip(2).ToArray(), proposal.Blocks.ToArray(), true);
                    if (left.FinalizedHeight == proposal.Blocks.Last().Height && right.FinalizedHeight == left.FinalizedHeight) break;
                }
                Thread.Sleep(100);
            }
            lock (gate) Check(errors.Count == 0 && left.FinalizedHeight == proposal.Blocks.Last().Height && right.Blocks.Last().Hash == left.Blocks.Last().Hash,
                "separate TCP nodes exchange validator votes and converge on one quorum certificate");
            var incompatiblePolicy = new FinalityPolicy(policy.AnchorHeight, new string('f', 64));
            using (var incompatible = new PeerNode(Port(), false, null, incompatiblePolicy))
            {
                incompatible.Start();
                incompatible.ConnectAsync("127.0.0.1", leftPort).GetAwaiter().GetResult();
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (incompatible.ConnectedPeerCount > 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
                Check(incompatible.ConnectedPeerCount == 0, "peers with different finality policy IDs are disconnected");
            }
            var isolated = new Blockchain(bootstrap.Blocks, null);
            for (int i = 0; i < 5; i++) isolated.AddBlock(new Transaction[0]);
            var returning = new Blockchain(isolated.Blocks, policy);
            returningNode.ChainReceived += (sender, e) => { lock (gate) try { returning.TryReplaceChain(e.Blocks); } catch (Exception ex) { errors.Add(ex); } };
            returningNode.ConnectAsync("127.0.0.1", rightPort).GetAwaiter().GetResult();
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate) { if (returning.FinalizedHeight == right.FinalizedHeight) break; }
                Thread.Sleep(100);
            }
            lock (gate) Check(errors.Count == 0 && returning.Blocks.Last().Hash == right.Blocks.Last().Hash && returning.Blocks.Count == right.Blocks.Count,
                "a reconnecting TCP node discards its longer isolated suffix for the certified online chain");
        }
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static Block[] Clone(IEnumerable<Block> chain)
    {
        using (var memory = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(Block[]));
            serializer.WriteObject(memory, chain.ToArray()); memory.Position = 0;
            return (Block[])serializer.ReadObject(memory);
        }
    }
    private static void Reject(Action action, string label)
    { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception(label); }
    private static void RejectAny(Action action, string label)
    { try { action(); } catch (InvalidDataException) { Check(true, label); return; } throw new Exception(label); }
    private static void Check(bool result, string label)
    { if (!result) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
}
