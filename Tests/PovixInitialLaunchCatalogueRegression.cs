using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Web.Script.Serialization;
using PrivateCoin.Core;
using Povix.Dex.Services;

class PovixInitialLaunchCatalogueRegression
{
    public static void Main(string[] args)
    {
        Block[] blocks;
        using (var stream = File.OpenRead(args[0])) blocks = (Block[])new DataContractJsonSerializer(typeof(Block[])).ReadObject(stream);
        // Exercise catalogue discovery without a running node or any local DEX pool records.
        var service = (TokenNetworkService)FormatterServices.GetUninitializedObject(typeof(TokenNetworkService));
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var name in new[] { "sync", "submitted", "pending", "profiles" })
        {
            var field = typeof(TokenNetworkService).GetField(name, fields);
            field.SetValue(service, name == "sync" ? new object() : Activator.CreateInstance(field.FieldType));
        }
        typeof(TokenNetworkService).GetField("blockchain", fields).SetValue(service, new Blockchain(blocks));
        var json = new JavaScriptSerializer();
        var result = json.DeserializeObject(json.Serialize(service.GetExchangeCatalogue(new string[0]))) as Dictionary<string, object>;
        var pools = (object[])result["pools"];
        if (pools.Length != 1) throw new Exception("Automatic native pool was not discovered from the chain");
        var pool = (Dictionary<string, object>)pools[0];
        if ((string)pool["status"] != "collecting" || (bool)pool["available"] || (bool)pool["canManage"]) throw new Exception("Collecting native pool must not trade or close");
        if (((object[])result["assets"]).Length != 3) throw new Exception("Native plus two local funding assets expected");
        Console.WriteLine("3 native-launch DEX catalogue checks passed");
    }
}
