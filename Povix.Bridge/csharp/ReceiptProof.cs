using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;
using Nethereum.Util;

namespace Povix.Bridge.CSharp {
    /// <summary>
    /// Offline receipt inclusion only. The caller-supplied root is NOT authenticated.
    /// Not connected to minting, custody or native consensus.
    /// </summary>
    public static class ReceiptProof {
        const int MaxReceipt = 262144, MaxNode = 65536, MaxProof = 524288;
        sealed class Item {
            public byte[] Data, Encoded;
            public List<Item> Children;
        }
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        static byte[] Hash(byte[] bytes) { return new Sha3Keccack().CalculateHash(bytes); }
        static bool Same(byte[] a, byte[] b) { return a.SequenceEqual(b); }
        static byte[] Hex(JToken value, int max, int exact = -1) {
            Require(value != null && value.Type == JTokenType.String, "Expected hex string.");
            string text = (string)value;
            Require(text.StartsWith("0x", StringComparison.Ordinal) && text.Length % 2 == 0 && text.Length <= 2 + max * 2 &&
                text.Skip(2).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'), "Use bounded lowercase 0x hex.");
            var bytes = Enumerable.Range(0, (text.Length - 2) / 2).Select(i => byte.Parse(text.Substring(2 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            Require(exact < 0 || bytes.Length == exact, "Incorrect byte length."); return bytes;
        }
        static int Length(byte[] bytes, ref int position, int count, int end) {
            Require(count > 0 && count <= 4 && position <= end - count && bytes[position] != 0, "Invalid RLP length.");
            long size = 0; for (int i = 0; i < count; i++) size = size * 256 + bytes[position++];
            Require(size >= 56 && size <= end - position, "Noncanonical or truncated RLP length."); return (int)size;
        }
        static Item Parse(byte[] bytes, ref int position, int end, int depth, ref int budget) {
            Require(depth <= 16 && --budget >= 0 && position < end, "RLP resource limit or truncation.");
            int start = position, prefix = bytes[position++], size; bool list = prefix >= 192;
            if (prefix < 128) return new Item { Data = new[] { (byte)prefix }, Encoded = new[] { (byte)prefix } };
            if (prefix <= 183) size = prefix - 128;
            else if (prefix <= 191) size = Length(bytes, ref position, prefix - 183, end);
            else if (prefix <= 247) size = prefix - 192;
            else size = Length(bytes, ref position, prefix - 247, end);
            Require(size <= end - position, "Truncated RLP.");
            int finish = position + size;
            var result = new Item();
            if (list) {
                result.Children = new List<Item>();
                while (position < finish) result.Children.Add(Parse(bytes, ref position, finish, depth + 1, ref budget));
            } else {
                Require(size != 1 || bytes[position] >= 128, "Noncanonical RLP byte.");
                result.Data = bytes.Skip(position).Take(size).ToArray(); position = finish;
            }
            result.Encoded = bytes.Skip(start).Take(finish - start).ToArray(); return result;
        }
        static Item Decode(byte[] bytes) {
            int position = 0, budget = 16384; var result = Parse(bytes, ref position, bytes.Length, 0, ref budget);
            Require(position == bytes.Length, "Trailing RLP data."); return result;
        }
        static byte[] Scalar(Item item, int exact = -1) {
            Require(item.Data != null && (exact < 0 || item.Data.Length == exact), "Invalid RLP scalar."); return item.Data;
        }
        static List<Item> List(Item item, int exact = -1) {
            Require(item.Children != null && (exact < 0 || item.Children.Count == exact), "Invalid RLP list."); return item.Children;
        }
        static byte[] IndexKey(int value) {
            if (value == 0) return new byte[] { 128 };
            var bytes = new List<byte>(); while (value > 0) { bytes.Insert(0, (byte)(value & 255)); value >>= 8; }
            if (bytes.Count > 1 || bytes[0] >= 128) bytes.Insert(0, (byte)(128 + bytes.Count)); return bytes.ToArray();
        }
        static int[] Nibbles(byte[] bytes) { return bytes.SelectMany(b => new[] { b >> 4, b & 15 }).ToArray(); }
        static Item Follow(Item reference, byte[][] proof, ref int next) {
            if (reference.Children != null) {
                Require(reference.Encoded.Length < 32, "Oversize inline trie node."); return reference;
            }
            byte[] hash = Scalar(reference, 32);
            Require(next < proof.Length && proof[next].Length >= 32 && Same(Hash(proof[next]), hash), "Missing or mismatched trie node.");
            return Decode(proof[next++]);
        }
        static byte[] Inclusion(byte[] root, int index, byte[][] proof) {
            Require(proof.Length > 0 && Same(Hash(proof[0]), root), "Root does not match proof.");
            int next = 1, offset = 0; int[] key = Nibbles(IndexKey(index)); Item node = Decode(proof[0]);
            for (int steps = 0; steps < 64; steps++) {
                var fields = List(node);
                if (fields.Count == 17) {
                    // Validate child references even on branches not selected by this proof.
                    for (int i = 0; i < 16; i++) Require(fields[i].Children != null ? fields[i].Encoded.Length < 32 : fields[i].Data.Length == 0 || fields[i].Data.Length == 32, "Invalid branch reference.");
                    Scalar(fields[16]);
                    if (offset == key.Length) { Require(next == proof.Length, "Unused proof nodes."); return Scalar(fields[16]); }
                    node = Follow(fields[key[offset++]], proof, ref next);
                } else {
                    Require(fields.Count == 2, "Invalid trie node arity.");
                    int[] compact = Nibbles(Scalar(fields[0]));
                    Require(compact.Length >= 2 && compact[0] <= 3 && ((compact[0] & 1) != 0 || compact[1] == 0), "Invalid hex-prefix path.");
                    bool leaf = (compact[0] & 2) != 0;
                    int[] path = compact.Skip((compact[0] & 1) != 0 ? 1 : 2).ToArray();
                    Require((leaf || path.Length > 0) && offset + path.Length <= key.Length && path.SequenceEqual(key.Skip(offset).Take(path.Length)), "Trie path mismatch.");
                    offset += path.Length;
                    if (leaf) { Require(offset == key.Length && next == proof.Length, "Incomplete key or unused proof nodes."); return Scalar(fields[1]); }
                    Require(offset < key.Length, "Extension cannot terminate the key."); node = Follow(fields[1], proof, ref next);
                }
            }
            throw new InvalidOperationException("Trie traversal limit.");
        }
        static int Integer(JObject obj, string name) {
            var token = obj[name]; Require(token != null && token.Type == JTokenType.Integer, "Expected integer " + name + ".");
            long value; Require(long.TryParse(token.ToString(), out value) && value >= 0 && value <= int.MaxValue, "Invalid " + name + "."); return (int)value;
        }
        static string HexText(byte[] bytes) { return "0x" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        static BigInteger Unsigned(byte[] bytes) { return new BigInteger(bytes.Reverse().Concat(new byte[] { 0 }).ToArray()); }
        public static JObject InspectFile(string file) {
            const int limit = 2 * 1024 * 1024;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                Require(stream.Length <= limit, "Proof JSON exceeds 2 MiB.");
                using (var buffer = new MemoryStream()) {
                    byte[] chunk = new byte[8192]; int count;
                    while ((count = stream.Read(chunk, 0, chunk.Length)) != 0) {
                        Require(buffer.Length + count <= limit, "Proof JSON exceeds 2 MiB."); buffer.Write(chunk, 0, count);
                    }
                    string json = new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray());
                    return Inspect(JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }));
                }
            }
        }
        public static JObject Inspect(JObject input) {
            Require((string)input["format"] == "povix-receipt-inclusion-v1", "Unsupported proof format.");
            var root = Hex(input["receiptsRoot"], 32, 32); var receipt = Hex(input["receipt"], MaxReceipt);
            var vault = Hex(input["vault"], 20, 20);
            Require(vault.Any(b => b != 0), "Zero vault.");
            int index = Integer(input, "transactionIndex"), logIndex = Integer(input, "receiptLogIndex");
            var nodes = input["proof"] as JArray; Require(nodes != null && nodes.Count > 0 && nodes.Count <= 64, "Invalid proof node count.");
            int total = 0;
            var proof = nodes.Select(n => { var bytes = Hex(n, MaxNode); total += bytes.Length; Require(total <= MaxProof, "Proof byte limit."); return bytes; }).ToArray();
            Require(Same(Inclusion(root, index, proof), receipt), "Receipt differs from proven trie value.");
            Require(receipt.Length > 0, "Empty receipt.");
            int type = receipt[0] < 128 ? receipt[0] : 0;
            Require(type == 0 || type == 1 || type == 2, "Unsupported typed receipt.");
            var fields = List(Decode(type == 0 ? receipt : receipt.Skip(1).ToArray()), 4);
            Require(Same(Scalar(fields[0]), new byte[] { 1 }), "Receipt must have successful status.");
            var gas = Scalar(fields[1]); Require(gas.Length <= 8 && (gas.Length == 0 || gas[0] != 0), "Noncanonical cumulative gas.");
            Scalar(fields[2], 256);
            var logs = List(fields[3]); Require(logs.Count <= 1024 && logIndex < logs.Count, "Invalid receipt-local log index.");
            foreach (var entry in logs) {
                var log = List(entry, 3); Scalar(log[0], 20); var topics = List(log[1]);
                Require(topics.Count <= 4, "Too many topics."); foreach (var topic in topics) Scalar(topic, 32); Scalar(log[2]);
            }
            var selected = logs[logIndex].Children; Require(Same(Scalar(selected[0]), vault), "Wrong event emitter.");
            var selectedTopics = List(selected[1], 4);
            Require(Same(Scalar(selectedTopics[0]), Hash(System.Text.Encoding.UTF8.GetBytes("Deposited(bytes32,address,bytes32,uint256,uint256)"))), "Wrong event signature.");
            var depositId = Scalar(selectedTopics[1]); var sender = Scalar(selectedTopics[2]); var recipient = Scalar(selectedTopics[3]);
            Require(depositId.Any(b => b != 0) && recipient.Any(b => b != 0) && sender.Take(12).All(b => b == 0) && sender.Skip(12).Any(b => b != 0), "Invalid event identities.");
            byte[] data = Scalar(selected[2], 64);
            var nativeAmount = Unsigned(data.Take(32).ToArray()); var assetAmount = Unsigned(data.Skip(32).ToArray());
            int decimals = Integer(input, "assetDecimals"); Require(decimals >= 8 && decimals <= 18, "Protocol pUSDT requires 8 through 18 asset decimals.");
            Require(nativeAmount > 0 && nativeAmount <= long.MaxValue && assetAmount == nativeAmount * BigInteger.Pow(10, decimals - 8), "Invalid event amount or scale.");
            return new JObject {
                ["inclusionVerified"] = true, ["consensusVerified"] = false, ["usableForMint"] = false,
                ["warning"] = "Caller-supplied receiptsRoot is unauthenticated. Not a consensus proof or mint authorization.",
                ["receiptsRoot"] = HexText(root), ["transactionIndex"] = index, ["receiptLogIndex"] = logIndex,
                ["vault"] = HexText(vault), ["depositId"] = HexText(depositId), ["sender"] = HexText(sender.Skip(12).ToArray()),
                ["nativeRecipient"] = HexText(recipient), ["nativeAmount"] = nativeAmount.ToString(CultureInfo.InvariantCulture),
                ["assetAmount"] = assetAmount.ToString(CultureInfo.InvariantCulture), ["receiptType"] = type
            };
        }
    }
}
