using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using PrivateCoin.Core;
using PrivateCoin.Desktop;

internal static class PrivateCoinGenesisRulesRegression
{
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS " + label);
    }
    private static T Copy<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, value); stream.Position = 0;
            return (T)serializer.ReadObject(stream);
        }
    }
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException || error is SerializationException)
        { Check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void Set(object value, string property, object replacement)
    {
        value.GetType().GetProperty(property).GetSetMethod(true).Invoke(value, new[] { replacement });
    }
    private static void Mine(Block block)
    {
        typeof(Blockchain).GetMethod("Mine", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { block });
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static void PeerMessage(int port, string network, string version, string rules, Block[] blocks, bool accepted)
    {
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, port);
            var serializer = new DataContractJsonSerializer(typeof(Block[]));
            string chain;
            using (var memory = new MemoryStream()) { serializer.WriteObject(memory, blocks); chain = Encoding.UTF8.GetString(memory.ToArray()); }
            string body = "{\"MessageId\":\"" + Guid.NewGuid() + "\",\"Type\":\"chain\",\"NetworkId\":\"" + network + "\",\"ConsensusVersion\":" + version +
                (rules == null ? "" : ",\"RulesHash\":\"" + rules + "\"") + ",\"Blocks\":" + chain + "}";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            var stream = client.GetStream();
            byte[] header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
            stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
            // Read the initial hello and then EOF on incompatibility. A timeout
            // fails the test rather than claiming that a silent peer was rejected.
            if (!accepted)
            {
                client.ReceiveTimeout = 5000;
                var buffer = new byte[4096];
                while (stream.Read(buffer, 0, buffer.Length) != 0) { }
            }
        }
    }
    public static void Main(string[] args)
    {
        string directory = args.Length == 0 ? Path.Combine(Path.GetTempPath(), "povix-genesis-" + Guid.NewGuid()) : args[0];
        Directory.CreateDirectory(directory);
        var chain = new Blockchain();
        GenesisRules rules = chain.Blocks[0].Rules;
        Check(chain.IsValid() && ProofOfWork.MeetsTarget(Blockchain.GenesisHash), "canonical genesis has valid proof of work");
        Check(new Blockchain().Blocks[0].Hash == chain.Blocks[0].Hash, "genesis construction is deterministic");
        Check(rules.TotalSupplyAtomicUnits == 18180000L * Blockchain.OneCoin && rules.InitialDistributionAtomicUnits == 600L * Blockchain.OneCoin &&
            rules.ValidatorSupplyAtomicUnits == 17820000L * Blockchain.OneCoin && rules.GenesisIssuanceAtomicUnits == 0, "genesis exposes total, distribution, rewards and zero premine");
        Check(rules.WalletRewardAtomicUnits == 600L * Blockchain.OneCoin && rules.RewardedWalletLimit == 1 && rules.RewardPhases.Count == 4 &&
            rules.RewardPhases.Sum(phase => (phase.LastHeight - phase.FirstHeight + 1) * phase.RewardAtomicUnits) == rules.ValidatorSupplyAtomicUnits,
            "distribution and all emission phases agree with executable policy");
        Check(rules.MaxOperationsPerBlock == 20 && rules.BlockWaitSeconds == 10 && rules.ProofOfWorkDifficultyBits == 12 &&
            rules.CreatorPercentage + rules.ConfirmerPercentage == 100 && rules.ImplementationHash.Length == 64, "production, validation and implementation rules are public");
        Check(Copy<Block[]>(chain.Blocks.ToArray())[0].Rules.CalculateHash() == Blockchain.RulesHash, "JSON roundtrip preserves the canonical rule commitment");
        foreach (PropertyInfo property in typeof(GenesisRules).GetProperties())
        {
            var bad = Copy<Block[]>(chain.Blocks.ToArray());
            object old = property.GetValue(bad[0].Rules, null);
            if (property.PropertyType == typeof(int)) Set(bad[0].Rules, property.Name, (int)old + 1);
            else if (property.PropertyType == typeof(long)) Set(bad[0].Rules, property.Name, (long)old + 1);
            else if (property.PropertyType == typeof(string)) Set(bad[0].Rules, property.Name, (string)old + "changed");
            else Set(bad[0].Rules.RewardPhases[0], "RewardAtomicUnits", bad[0].Rules.RewardPhases[0].RewardAtomicUnits + 1);
            Mine(bad[0]);
            Reject(() => new Blockchain(bad), "remining cannot authorize modified " + property.Name);
        }
        var missing = Copy<Block[]>(chain.Blocks.ToArray()); missing[0].Rules = null; Mine(missing[0]);
        Reject(() => new Blockchain(missing), "genesis without rules is rejected");
        Check(new Blockchain().IsValid(), "mutating a returned manifest cannot change the canonical cache");
        chain.AddBlock(new[] { chain.CreateWalletCreationTransaction(new string('a', 64), new Transaction[0]) });
        var extra = Copy<Block[]>(chain.Blocks.ToArray()); extra[1].Rules = Copy(rules); Mine(extra[1]);
        Reject(() => new Blockchain(extra), "rules cannot be redefined in a later block");
        Check(new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(), "valid operations and persistence still work");

        var store = new WalletStore(directory);
        typeof(WalletStore).GetMethod("SaveNetwork", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(store, new object[] { new NamedWallet[0], chain, new Transaction[0] });
        string file = Path.Combine(directory, "Blockchain.json"), original = File.ReadAllText(file);
        Check(original.Contains("\"Genesis\"") && original.Contains("\"TotalSupplyAtomicUnits\":1818000000000000"), "Blockchain.json contains readable genesis rules");
        List<Transaction> pending;
        Check(store.LoadNetwork(out pending).IsValid() && pending.Count == 0, "real Desktop storage reloads the manifest");
        File.WriteAllText(file, original.Replace("\"BlockWaitSeconds\":10", "\"BlockWaitSeconds\":11"));
        Reject(() => store.LoadNetwork(out pending), "readable genesis tampering is rejected even with intact payload checksum");
        File.WriteAllText(file, original);

        int port = FreePort(), delivered = 0;
        using (var node = new PeerNode(port, false))
        {
            node.ChainReceived += (sender, eventArgs) => Interlocked.Increment(ref delivered);
            node.Start();
            string version = Blockchain.ConsensusVersion.ToString();
            PeerMessage(port, Blockchain.NetworkId, version, new string('0', 64), chain.Blocks.ToArray(), false);
            Check(delivered == 0, "peer with changed rules is disconnected before dispatch");
            PeerMessage(port, Blockchain.NetworkId, version, null, chain.Blocks.ToArray(), false);
            Check(delivered == 0, "peer without a rule commitment is disconnected");
            PeerMessage(port, "old-network", version, Blockchain.RulesHash, chain.Blocks.ToArray(), false);
            Check(delivered == 0, "old genesis network is disconnected");
            PeerMessage(port, Blockchain.NetworkId, "0", Blockchain.RulesHash, chain.Blocks.ToArray(), false);
            Check(delivered == 0, "different consensus version is disconnected");
            PeerMessage(port, Blockchain.NetworkId, version, Blockchain.RulesHash, extra, false);
            Check(delivered == 0, "claiming the correct rule hash cannot authorize an invalid chain");
            PeerMessage(port, Blockchain.NetworkId, version, Blockchain.RulesHash, chain.Blocks.ToArray(), true);
            Check(SpinWait.SpinUntil(() => delivered == 1, 5000), "compatible peer delivers a validated chain");
        }
        Console.WriteLine("PASS " + checks + " genesis rules checks");
    }
}
