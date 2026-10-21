using System;
using System.Linq;
using PrivateCoin.Core;
using Povix.Bridge;
internal static class ProductionBurnLimitRegression {
    public static void Main() {
        using(var founder=new Wallet()) {
            var admins=Enumerable.Range(0,5).Select(i=>new Wallet()).ToArray();
            try {
                var chain=new Blockchain();var owner=founder.CreateReceiveAddress();
                chain.AddBlock(new[]{chain.CreateWalletCreationTransaction(owner,new Transaction[0])});
                var config=new NativeConfig { nativeNetwork=Blockchain.NetworkId,nativeToken=InitialLaunch.PusdtTokenId,chainId=31337,
                    bridge="0x"+new string('1',40),asset="0x"+new string('2',40),nativeConfirmations=120,reserveCap="1000000000",dailyReleaseCap="100000000",
                    nativeAdministrators=admins.Select(a=>a.CreateReceiveAddress()).ToArray(),nativeThreshold=3 };
                chain.AddBlock(new[]{BridgeNative.Configure(chain,config,founder.ExportPrivateKeys().ToArray())});
                config.mode="production";
                try { BridgeNative.PrepareOperation(chain,config,new ReleaseRequest { operation="burn",amount="100000001",fee=0 },new string[0]);throw new Exception("Oversized burn was accepted"); }
                catch(InvalidOperationException e) { if(!e.Message.Contains("Split the redemption"))throw; }
                Console.WriteLine("PASS production native preparation rejects burns exceeding daily release cap before selecting keys or consuming tokens");
            } finally { foreach(var a in admins)a.Dispose(); }
        }
    }
}
