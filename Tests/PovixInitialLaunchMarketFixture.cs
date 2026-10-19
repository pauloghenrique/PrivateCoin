using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Runtime.Serialization;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using Povix.Dex.Models;
using PrivateCoin.Core;
using Povix.Dex.Services;
class PovixInitialLaunchMarketFixture
{
    static BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static void Set(object value,string name,object data) => value.GetType().GetField(name,Flags).SetValue(value,data);
    static string Hash(string text) { using(var sha=SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b=>b.ToString("x2"))); }
    public static void Main(string[] args) {
        var wallet=new Wallet(); string address=wallet.CreateReceiveAddress(), key;
        using(var rsa=new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp=false; rsa.FromXmlString(wallet.ExportPrivateKeys().First()); key=rsa.ToXmlString(false); }
        var chain=new Blockchain(); var provider=new Wallet();
        chain.AddBlock(new[]{chain.CreateWalletCreationTransaction(provider.CreateReceiveAddress(),new Transaction[0])});
        chain.AddBlock(new[]{chain.CreateWalletCreationTransaction(address,new Transaction[0])});
        if(chain.GetBalance(new[]{address})!=0) throw new Exception("Buyer must start with zero POVIX");
        var reserves=chain.GetUnspentOutputs(new[]{Hash(InitialLaunch.ProviderPublicKey)}).Where(item=>item.OutputIndex>=0).OrderBy(item=>item.OutputIndex).ToArray();
        var json=new JavaScriptSerializer();
        var service=(TokenNetworkService)FormatterServices.GetUninitializedObject(typeof(TokenNetworkService));
        foreach(string name in new[]{"sync","submitted","pending","profiles","drafts","transferDrafts","exchangeDrafts"}) {
            var field=typeof(TokenNetworkService).GetField(name,Flags); field.SetValue(service,name=="sync"?new object():Activator.CreateInstance(field.FieldType));
        }
        Set(service,"blockchain",chain); Set(service,"synchronized",true);
        var node=(PeerNode)FormatterServices.GetUninitializedObject(typeof(PeerNode));
        var peers=new ConcurrentDictionary<TcpClient,SemaphoreSlim>(); { var peer=(TcpClient)FormatterServices.GetUninitializedObject(typeof(TcpClient));
            peers.TryAdd(peer,new SemaphoreSlim(1)); Set(node,"peers",peers); Set(service,"node",node);
            Action[] gatedOperations = {
                () => service.PreparePool(new CreatePoolViewModel(), new[]{key}, address, "test"),
                () => service.PrepareLaunchPool(new CreateLaunchPoolViewModel(), new[]{key}, address, "test"),
                () => service.PrepareLiquidityChange(new LiquidityChangeViewModel {Operation="add"}, new[]{key}, address, "test")
            };
            foreach (var operation in gatedOperations) {
                try { operation(); throw new Exception("Pool access accepted before principal target"); }
                catch (TokenOperationException error) { if(error.Message != InitialLaunch.PoolAccessMessage) throw; }
            }
            var catalogue = (Dictionary<string,object>)json.DeserializeObject(json.Serialize(service.GetExchangeCatalogue(new[]{key})));
            if ((bool)catalogue["canCreatePools"]) throw new Exception("Catalogue enables early pool creation");
            Console.WriteLine("DEX gates both pool types and liquidity additions while principal collects");
            var request=new SwapViewModel {PoolId=chain.GetInitialLaunchStatus().RootTransactionId,InputSide=1,InputAssetId=InitialLaunch.UsdcTokenId,OutputAssetId="povix",Amount="1",MinimumReceive="5.99999999",FeeAtomic=1,FeePriority=1};
            var response=service.PrepareSwap(request,new[]{key},address,"test");
            File.WriteAllText(args[0]+".service.json",json.Serialize(response));
            request.InputSide=0;
            try { service.PrepareSwap(request,new[]{key},address,"test"); throw new Exception("Launch SELL accepted"); } catch(TokenOperationException) {}
            request.InputSide=1; request.Amount="2";
            try { service.PrepareSwap(request,new[]{key},address,"test"); throw new Exception("Nonfixed launch amount accepted"); } catch(TokenOperationException) {}
            var id=(string)((Dictionary<string,object>)json.DeserializeObject(json.Serialize(response)))["draftId"];
            var stored=((IDictionary)typeof(TokenNetworkService).GetField("exchangeDrafts",Flags).GetValue(service))[id];
            var prepared=(UnsignedTokenExchange)stored.GetType().GetField("Transaction",Flags).GetValue(stored);
            var signatures=new List<string>();
            using(var rsa=new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp=false; rsa.FromXmlString(wallet.ExportPrivateKeys().First());
                for(int i=0;i<prepared.UserInputCount+1;i++) signatures.Add(Convert.ToBase64String(rsa.SignData(prepared.SigningPayload,"SHA256")));
            }
            var current=typeof(TokenNetworkService).GetMethod("CurrentReserves",Flags);
            if(!(bool)current.Invoke(service,new[]{stored})) throw new Exception("Collecting reserves rejected at submit");
            string cache=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".json"); Set(service,"statePath",cache);
            try {
                var accepted=(Transaction)typeof(TokenNetworkService).GetMethod("AcceptExchange",Flags).Invoke(service,new object[]{id,stored,signatures.ToArray()});
                if(accepted.Outputs[2].Amount != InitialLaunch.PurchaseAmount - accepted.Fee || accepted.Inputs.Take(accepted.Inputs.Count-2).Any(input => input.PublicKey == InitialLaunch.ProviderPublicKey) || accepted.InitialLaunchIdentity.Signature!=signatures.Last() || !accepted.Outputs[2].InitialLaunchLocked) throw new Exception("Identity signature or lock lost during submit");
                var prior=typeof(TokenNetworkService).GetMethod("PriorExchange",Flags).Invoke(service,new object[]{id,"swap",signatures.ToArray()});
                if(prior==null) throw new Exception("Accepted purchase retry not idempotent");
                request.Amount="1";
                try { service.PrepareSwap(request,new[]{key},address,"test"); throw new Exception("Second pending purchase accepted"); } catch(TokenOperationException) {}
                chain.AddBlock(new[]{accepted});
                if(accepted.TransactionApproval!=null || !new Blockchain(chain.Blocks).IsValid()) throw new Exception("Launch purchase requires validator approval or fails replay");
                if(service.GetExchangeReceipt(accepted.Id).Status!="confirmed") throw new Exception("Confirmed approval-free receipt missing");
                Console.WriteLine("DEX purchase acceptance, identity signature, locked receipt, persistence, retry and pending duplicate checks passed");
            } finally { if(File.Exists(cache)) File.Delete(cache); if(File.Exists(cache+".bak")) File.Delete(cache+".bak"); }

        }

        foreach(bool final in new[]{false,true}) {
            if(final) { reserves[0].Output.Amount=InitialLaunch.PrincipalSupply+InitialLaunch.PurchaseAmount; reserves[1].Output.Amount=(InitialLaunch.MaximumPurchases-1L)*InitialLaunch.FundingUnit; }
            var unsigned=TokenExchange.PrepareInitialLaunchPurchase(chain,new Transaction[0],new[]{key},reserves[0],reserves[1],InitialLaunch.ProviderPublicKey,key,address,1);
            var type=typeof(TokenNetworkService).GetNestedType("PreparedExchange",BindingFlags.NonPublic); var draft=Activator.CreateInstance(type);
            Set(draft,"Transaction",unsigned); Set(draft,"Operation","swap"); Set(draft,"InitialLaunchIdentityKey",key); Set(draft,"PoolId",chain.GetInitialLaunchStatus().RootTransactionId);
            Set(draft,"ReserveId",reserves[0].TransactionId); Set(draft,"ProviderKey",InitialLaunch.ProviderPublicKey); Set(draft,"Address",address);
            Set(draft,"AssetA",new TokenDefinition {Id="povix",Name="POVIX",Symbol="POVIX",Decimals=8}); Set(draft,"AssetB",InitialLaunch.FundingTokens().First());
            Set(draft,"ReserveA",reserves[0].Output.Amount); Set(draft,"ReserveB",reserves[1].Output.Amount); Set(draft,"InputSide",1); Set(draft,"Amount",InitialLaunch.FundingUnit); Set(draft,"MinimumReceive",InitialLaunch.PurchaseAmount-1); Set(draft,"ExpiresUtc",DateTime.UtcNow.AddMinutes(15));
            var response=typeof(TokenNetworkService).GetMethod("DraftResponse",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{(object)new string('a',32),draft});
            File.WriteAllText(args[0]+(final?".final.json":".json"),json.Serialize(response));
        }
        Console.WriteLine("Generated real DEX launch drafts for first and final purchases");
    }
}
