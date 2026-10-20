using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Serialization.Json;
using PrivateCoin.Core;
using Povix.Bridge;

internal static class ProductionNativeRegression {
    static string Dir;
    static void Write<T>(string file, T value) { using (var s = File.Create(Path.Combine(Dir, file))) new DataContractJsonSerializer(typeof(T)).WriteObject(s, value); }
    static void Save(Blockchain chain) { Write("chain.json", chain.Blocks.ToArray()); Write("independent-chain.json", chain.Blocks.ToArray()); }
    static void Confirm(Blockchain chain, Transaction tx) {
        if (chain.CanCreateUnsignedBlock(new[] { tx })) { chain.AddBlock(new[] { tx }); return; }
        using (var founder = Wallet.FromPrivateKeys(BridgeNative.Read<string[]>(Path.Combine(Dir,"founder.local.json"))))
        using (var batch = new ValidationBatchFixture(chain)) batch.Confirm(chain,new[]{tx},new[]{founder.CreateValidatorStake(founder.OwnedOneTimeAddresses.First(),Blockchain.WalletCreationReward)});
    }
    static void Check(bool value,string text) { if(!value)throw new Exception(text); Console.WriteLine("PASS " + text); }
    public static void Main(string[] args) {
        Dir=args[1];
        if(args[0]=="setup") {
            Directory.CreateDirectory(Dir);
            using(var founder=new Wallet()) using(var holder=new Wallet()) {
                var chain=new Blockchain(); var address=founder.CreateReceiveAddress(); holder.CreateReceiveAddress();
                chain.AddBlock(new[]{chain.CreateWalletCreationTransaction(address,new Transaction[0])});
                Write("founder.local.json",founder.ExportPrivateKeys().ToArray());Write("holder.local.json",holder.ExportPrivateKeys().ToArray());
                File.WriteAllText(Path.Combine(Dir,"holder.txt"),holder.OwnedOneTimeAddresses.First());Save(chain);
            }return;
        }
        var ledger=BridgeNative.LoadChain(Path.Combine(Dir,"chain.json"));var config=BridgeNative.Read<NativeConfig>(Path.Combine(Dir,"config.json"));
        if(args[0]=="configure") {
            var before=new Blockchain(ledger.Blocks);
            var mainnet=BridgeNative.Read<NativeConfig>(Path.Combine(Dir,"config.json")); mainnet.chainId=56;
            var root=BridgeNative.Configure(before,mainnet,BridgeNative.Read<string[]>(Path.Combine(Dir,"founder.local.json")));Confirm(before,root);
            var unsigned=TokenManagement.Prepare(before,new Transaction[0],mainnet.nativeSigningPublicKeys,mainnet.nativeToken,"mint",1,
                File.ReadAllText(Path.Combine(Dir,"holder.txt")),BridgeNative.Hash(mainnet.nativeSigningPublicKeys[0]),0).Preview;
            unsigned.Id=LegacyConsensusFixture.Id(unsigned);
            try { before.ValidatePendingTransactions(new[]{unsigned});throw new Exception("Accepted mainnet mint without claim"); }
            catch(InvalidOperationException e) { Check(e.Message.Contains("claimed"),"mainnet consensus rejects a mint with no deposit claim"); }
            Confirm(ledger,BridgeNative.Configure(ledger,config,BridgeNative.Read<string[]>(Path.Combine(Dir,"founder.local.json"))));Save(ledger);return;
        }
        if(args[0]=="confirm-control") {
            Confirm(ledger,BridgeNative.Read<Transaction>(Path.Combine(Dir,"control.json")));Save(ledger);return;
        }
        if(args[0]=="confirm-mint") {
            var tx=BridgeNative.Read<Transaction>(Path.Combine(Dir,"mint.json"));
            Check(!ledger.HasBridgeDeposit(config.nativeToken,tx.TokenCommand.BridgeDepositId),"claim unspent before mint");
            var before=new Blockchain(ledger.Blocks);Confirm(ledger,tx);
            Check(ledger.HasBridgeDeposit(config.nativeToken,tx.TokenCommand.BridgeDepositId),"claim consumed by consensus");
            Check(!before.HasBridgeDeposit(config.nativeToken,tx.TokenCommand.BridgeDepositId),"reorg to before mint removes claim");
            var attack=TokenManagement.Prepare(ledger,new Transaction[0],config.nativeSigningPublicKeys,config.nativeToken,"mint",1,
                File.ReadAllText(Path.Combine(Dir,"holder.txt")),BridgeNative.Hash(config.nativeSigningPublicKeys[0]),0,null,null,
                tx.TokenCommand.BridgeDepositId,tx.TokenCommand.BridgeBlockHash).Preview;
            attack.Id=LegacyConsensusFixture.Id(attack);
            try{ledger.ValidatePendingTransactions(new[]{attack});throw new Exception("Duplicate deposit accepted");}
            catch(InvalidOperationException e){Check(e.Message.Contains("claimed"),"duplicate deposit rejected independently of coordinator journal and revision");}
            Save(ledger);Check(BridgeNative.LoadChain(Path.Combine(Dir,"chain.json")).HasBridgeDeposit(config.nativeToken,tx.TokenCommand.BridgeDepositId),"claim survives full snapshot replay");return;
        }
        if(args[0]=="burn") {
            var keys=BridgeNative.Read<string[]>(Path.Combine(Dir,"holder.local.json"));
            var burn=BridgeNative.PrepareOperation(ledger,config,new ReleaseRequest{operation="burn",amount="50000000",fee=0},keys);Confirm(ledger,burn);
            for(int i=0;i<config.nativeConfirmations-1;i++)Confirm(ledger,ledger.CreateWalletCreationTransaction(BridgeNative.Hash("prod-confirm-"+ledger.Blocks.Count),new Transaction[0]));
            var binding=BridgeNative.SignBinding(ledger,new Binding{transactionId=burn.Id,outputIndex=0,chainId=config.chainId,bridge=config.bridge,recipient=args[2]},keys);
            Check(BridgeNative.VerifyBurn(ledger,config,binding).confirmations>=config.nativeConfirmations,"burn independently verified with the configured confirmation depth");
            Write("burn.json",binding);Save(ledger);return;
        }
    }
}
