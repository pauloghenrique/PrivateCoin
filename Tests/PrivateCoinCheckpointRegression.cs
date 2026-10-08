using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using PrivateCoin.Core;

internal static class PrivateCoinCheckpointRegression
{
    private static int checks;
    public static int Main(string[] args)
    {
        try
        {
            Block[] blocks;
            using (var stream = File.OpenRead(args[0]))
                blocks = (Block[])new DataContractJsonSerializer(typeof(Block[])).ReadObject(stream);
            var local = new Blockchain(blocks, null);
            int tip = local.Blocks.Count - 1;
            FinalityCheckpoint candidate = FinalityCheckpoint.Create(local);
            Check(candidate.Height == tip && candidate.BlockHash == local.Blocks.Last().Hash && candidate.Committee.Count == 4,
                "candidate contains the existing block and its registered validators");
            byte[] json = candidate.ToJson();
            FinalityCheckpoint imported = FinalityCheckpoint.FromJson(json);
            imported.ValidateAgainst(local);
            Check(imported.PolicyId == candidate.PolicyId && imported.ToJson().SequenceEqual(json), "public JSON export is deterministic and round trips");
            Check(!Encoding.UTF8.GetString(json).Contains("<D>") && !Encoding.UTF8.GetString(json).Contains("PrivateKey") &&
                candidate.ConfigurationSnippet().Contains(candidate.BlockHash), "export contains public keys and exact configuration, no private keys");
            local.AddBlock(new Transaction[0]);
            imported.ValidateAgainst(local);
            Check(true, "the same checkpoint remains valid after later blocks are created");
            FinalityCheckpoint older = FinalityCheckpoint.Create(local, tip - 2);
            Check(older.Height == tip - 2 && older.Committee.Count == 3, "an operator can select an earlier existing block with its historical stake set");
            var active = new Blockchain(blocks, candidate.ToPolicy());
            active.AddBlock(new Transaction[0]);
            Check(FinalityCheckpoint.Create(active).PolicyId == candidate.PolicyId,
                "an active policy exports its fixed checkpoint rather than the changing tip");
            Reject(() => FinalityCheckpoint.Create(active, tip + 1), "an active checkpoint cannot silently change");
            Reject(() => FinalityCheckpoint.Create(local, 0), "genesis cannot be used as the migration checkpoint");
            Reject(() => FinalityCheckpoint.Create(local, 999999), "future or missing block height is rejected");
            Reject(() => FinalityCheckpoint.Create(local, 2), "a block with only one registered validator is rejected");
            string text = Encoding.UTF8.GetString(json);
            string alteredStake = text.Replace("\"LockedAmountAtomic\":\"100000000\"", "\"LockedAmountAtomic\":\"100000001\"");
            Check(alteredStake != text, "the tampering fixture changes a real stake field");
            Reject(() => FinalityCheckpoint.FromJson(Encoding.UTF8.GetBytes(alteredStake)).ValidateAgainst(local),
                "claimed stake amounts are checked against the local chain");
            string otherNetwork = text.Replace(Blockchain.NetworkId, "unrelated-network");
            Reject(() => FinalityCheckpoint.FromJson(Encoding.UTF8.GetBytes(otherNetwork)), "another network's candidate is rejected");
            string alteredHash = text.Replace(candidate.BlockHash, new string('f', 64));
            Reject(() => FinalityCheckpoint.FromJson(Encoding.UTF8.GetBytes(alteredHash)), "hash and policy identity must agree");
            var different = new Blockchain(blocks.Take(tip), null);
            different.AddBlock(new Transaction[0]);
            Reject(() => imported.ValidateAgainst(different), "a competing block at the same height is rejected");
            local.Blocks.Last().Hash = "invalid";
            Reject(() => FinalityCheckpoint.Create(local, tip - 2), "invalid source history is rejected even when selecting an older block");
            try { FinalityCheckpoint.FromJson(new byte[1024 * 1024 + 1]); throw new Exception("Size limit missing"); }
            catch (InvalidDataException) { Check(true, "oversized imports are rejected"); }
            Console.WriteLine(checks + " checkpoint checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label)
    { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception(label); }
}
