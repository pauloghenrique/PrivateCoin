using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Povix.Bridge.CSharp;

public static class CSharpRegression {
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Reject(Action action, string message) { try { action(); } catch { Console.WriteLine("PASS " + message); return; } throw new Exception("Accepted " + message); }
    public static void Main(string[] args) {
        if (args.Length == 3 && args[0] == "automation-peer") {
            using (var peer = new PrivateCoin.Core.PeerNode(int.Parse(args[1]))) {
                peer.TransactionReceived += (s, e) => { using (var stream = File.Create(args[2])) new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(PrivateCoin.Core.Transaction)).WriteObject(stream, e.Transaction); Console.WriteLine("RECEIVED " + e.Transaction.Id); };
                Console.WriteLine("NETWORK " + PrivateCoin.Core.Blockchain.NetworkId); peer.Start(); Console.WriteLine("READY"); System.Threading.Thread.Sleep(TimeSpan.FromMinutes(10));
            } return;
        }
        if (args.Length == 3 && args[0] == "native-sign") {
            byte[] payload = Convert.FromBase64String(args[2]);
            using (var rsa = OperatorKeys.LoadNative(args[1])) {
                string key = rsa.ToXmlString(false);
                Console.WriteLine(new JObject { ["publicKey"] = key, ["address"] = Povix.Bridge.BridgeNative.Hash(key), ["signature"] = Convert.ToBase64String(rsa.SignData(payload, "SHA256")) });
            } return;
        }
        if (args.Length == 0 || args[0] == "self-test") {
            string temp = Path.Combine(Path.GetTempPath(), "povix-csharp-unit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try {
                var publicKeys = new JArray();
                for (int i = 0; i < 2; i++) using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider(3072)) { rsa.PersistKeyInCsp = false; publicKeys.Add(rsa.ToXmlString(false)); }
                string first = Path.Combine(temp, "node1.json"), second = Path.Combine(temp, "node2.json");
                var fixture = new JObject {
                    ["mode"] = "production", ["chainId"] = 31337, ["rpcUrl"] = "http://127.0.0.1:10001", ["rpcUrls"] = new JArray("http://127.0.0.1:10001", "http://127.0.0.1:10002"),
                    ["asset"] = "0x" + new string('1', 40), ["bridge"] = "0x" + new string('2', 40), ["assetCodeHash"] = "0x" + new string('a', 64), ["vaultCodeHash"] = "0x" + new string('b', 64),
                    ["nativeNetwork"] = "povix-mainnet-rules-v1-unit", ["nativeToken"] = Native.ProtocolToken,
                    ["operators"] = new JArray("0x" + new string('3', 40), "0x" + new string('4', 40)), ["threshold"] = 2,
                    ["nativeAdministrators"] = new JArray(publicKeys.Select(k => Povix.Bridge.BridgeNative.Hash((string)k))), ["nativeSigningPublicKeys"] = publicKeys, ["nativeThreshold"] = 2,
                    ["nativeSnapshot"] = first, ["nativeSnapshots"] = new JArray(first, second), ["nativeConfirmations"] = 12, ["bscConfirmations"] = 64, ["reserveCap"] = "1000000000", ["dailyReleaseCap"] = "1000000"
                };
                AutomationRegression.Run(BridgeConfig.From(fixture), temp);
                string fixtureFile = Path.Combine(temp, "config.json"); File.WriteAllText(fixtureFile, fixture.ToString()); Main(new[] { "unit", fixtureFile, Path.Combine(temp, "state") });
            } finally { Directory.Delete(temp, true); } return;
        }
        if (args[0] == "sign-typed") {
            var c = BridgeConfig.Load(args[1]); var value = JObject.Parse(File.ReadAllText(args[3]));
            Console.WriteLine(Evm.Sign(c, args[2], value, OperatorKeys.LoadEvm(args[4]))); return;
        }
        var config = BridgeConfig.Load(args[1]);
        if (args[0] == "probe-journal") {
            try { using (var db = new Journal(args[2], config)) {} Environment.Exit(0); }
            catch { Environment.Exit(3); } return;
        }
        string dir = args[2]; Directory.CreateDirectory(dir);
        Check(config.Strings("operators").Length == 2 && config.Number("threshold") == 2, "two operators require both signatures");
        Reject(() => config.With("threshold", 1), "1-of-2 EVM quorum"); Reject(() => config.With("nativeThreshold", 1), "1-of-2 native quorum");
        Reject(() => config.With("threshold", 2.1), "fractional quorum is never rounded");
        Reject(() => config.With("reserveCap", 1000000000), "atomic limits require explicit decimal strings");
        Reject(() => config.With("operators", new JArray(config.Strings("operators")[0], config.Strings("operators")[0])), "duplicate identities");
        var value1 = new JObject { ["amount"] = "1", ["recipient"] = "a" }; string file = Path.Combine(dir, "operator.journal");
        using (var db = new Journal(file, config)) {
            db.Record("burn", value1); Reject(() => db.Record("burn", new JObject { ["amount"] = "2" }), "conflicting durable decision");
            Reject(() => { using (var second = new Journal(file, config)) {} }, "concurrent writer");
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            string exe = typeof(CSharpRegression).Assembly.Location;
            Func<string, string> quote = s => "\"" + s.Replace("\"", "\\\"") + "\"";
            var start = new ProcessStartInfo(windows ? exe : Environment.GetEnvironmentVariable("POVIX_MONO") ?? "mono",
                (windows ? "" : quote(exe) + " ") + "probe-journal " + quote(args[1]) + " " + quote(file)) { UseShellExecute = false, RedirectStandardError = true };
            using (var child = Process.Start(start)) { if (!child.WaitForExit(10000)) { child.Kill(); throw new Exception("Journal lock probe timed out"); } string error = child.StandardError.ReadToEnd(); Check(child.ExitCode == 3, "separate operator processes cannot share a journal writer (exit " + child.ExitCode + "; " + error.Trim() + ")"); }
        }
        using (var db = new Journal(file, config)) Check(Native.Canonical(db.Get("burn")) == Native.Canonical(value1), "decision survives restart");
        Reject(() => { using (var db = new Journal(file, config.With("nativeNetwork", config["nativeNetwork"] + "-other"))) {} }, "journal belongs to another network");
        File.AppendAllText(file, "{\"unfinished\":"); Reject(() => { using (var db = new Journal(file, config)) {} }, "torn write fails closed");
        string broken = Path.Combine(dir, "corrupt.journal"); File.WriteAllText(broken, "{\"previous\":\"wrong\",\"hash\":\"wrong\",\"key\":\"x\",\"value\":0}\n");
        Reject(() => { using (var db = new Journal(broken, config)) {} }, "corrupt journal fails closed");
        Reject(() => { using (var db = new Journal(Path.Combine(dir,"old.sqlite"),config)) {} }, "legacy journal is never silently replaced");
        Check(Native.Canonical(JObject.Parse("{\"b\":2,\"a\":9007199254740993}")) == "{\"a\":9007199254740993,\"b\":2}", "canonical JSON preserves signed integer precision");
    }
}
