using System;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using Povix.Bridge.CSharp;

public static class AutomationRegression {
    [DllImport("libc", SetLastError = true)] static extern int symlink(string target, string link);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS automation " + message); }
    static void Reject(Action action, string message) { try { action(); } catch { Console.WriteLine("PASS automation " + message); return; } throw new Exception("Accepted automation " + message); }
    public static void Run(BridgeConfig config, string temp) {
        string root = Path.Combine(temp, "automation"), exchange = Path.Combine(root, "exchange"), state = Path.Combine(root, "state"), file = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        var good = new JObject { ["enabled"] = true, ["exchangeDirectory"] = exchange, ["stateDirectory"] = state, ["pollSeconds"] = 15, ["processReleases"] = true,
            ["startBlock"] = 10, ["nativeListenPort"] = 39401, ["nativePeers"] = new JArray("127.0.0.1:3333"), ["relayKeystore"] = Path.Combine(root, "relay.local.json") };
        File.WriteAllText(file, good.ToString()); Check(AutomationConfig.Load(file, true).StartBlock == 10, "coordinator configuration");
        Action<JObject> save = j => File.WriteAllText(file, j.ToString());
        foreach (var mutation in new Action<JObject>[] {
            j => j["enabled"] = false,
            j => j["maxGasPriceWei"] = "0",
            j => j["maxGasPriceWei"] = 5000000000L,
            j => j["maxGasLimit"] = 5000001,
            j => j["nativeOperatorKeystore"] = Path.Combine(root,"native.json"),
            j => j["stateDirectory"] = exchange,
            j => j["stateDirectory"] = Path.Combine(exchange,"private"),
            j => j["relayKeystore"] = Path.Combine(exchange,"key.json"),
            j => j["startBlock"] = -1,
            j => j["startBlock"] = 1.5,
            j => j["pollSeconds"] = "15",
            j => j["nativePeers"] = new JArray(),
            j => j["nativePeers"] = new JArray("127.0.0.1:3333/path")
        }) { var bad = (JObject)good.DeepClone(); mutation(bad); save(bad); Reject(() => AutomationConfig.Load(file, true), "unsafe coordinator settings rejected"); }
        save(good); Reject(() => AutomationConfig.Load(file, false), "coordinator configuration cannot be used as operator configuration");
        var job = AutomationFiles.Job(config,"mint",new JObject { ["kind"] = "mint", ["amount"] = "100000000" });
        string id = AutomationFiles.Id(job), path = Path.Combine(exchange,id+".json");
        AutomationFiles.Put(path,job); AutomationFiles.Put(path,(JObject)job.DeepClone());
        AutomationFiles.ValidateJob(config,path,AutomationFiles.Read(path)); Check(File.Exists(path),"immutable atomic publication is idempotent");
        var changed=(JObject)job.DeepClone();changed["body"]["amount"]="200000000";
        Reject(()=>AutomationFiles.Put(path,changed),"existing immutable file cannot be overwritten");
        Reject(()=>AutomationFiles.ValidateJob(config,path,changed),"tampered job hash rejected");
        changed=(JObject)job.DeepClone();changed["scope"]="other-network";
        Reject(()=>AutomationFiles.ValidateJob(config,Path.Combine(exchange,AutomationFiles.Id(changed)+".json"),changed),"foreign bridge scope rejected");
        string malformed=Path.Combine(root,"bad.json");
        File.WriteAllText(malformed,"{\"a\":1,\"a\":2}");Reject(()=>AutomationFiles.Read(malformed),"duplicate JSON property rejected");
        File.WriteAllText(malformed,"{}{}");Reject(()=>AutomationFiles.Read(malformed),"trailing JSON rejected");
        File.WriteAllText(malformed,new string(' ',262145));Reject(()=>AutomationFiles.Read(malformed),"oversized file rejected");
        File.WriteAllText(malformed,new string('[',40)+"0"+new string(']',40));Reject(()=>AutomationFiles.Read(malformed),"invalid nested JSON rejected");
        if(Environment.OSVersion.Platform!=PlatformID.Win32NT) {
            string link=Path.Combine(root,"linked-exchange"); Check(symlink(exchange,link)==0,"test symlink created");Reject(()=>AutomationFiles.List(link),"directory symlink rejected");
            string fileLink=Path.Combine(root,"linked.json");Check(symlink(path,fileLink)==0,"test file symlink created");Reject(()=>AutomationFiles.Read(fileLink),"file symlink rejected");
        }
    }
}
