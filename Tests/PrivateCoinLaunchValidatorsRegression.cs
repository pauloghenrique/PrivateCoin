using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using PrivateCoin.Core;
class PrivateCoinLaunchValidatorsRegression
{
    static int checks;
    static readonly BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool ok,string text) { if(!ok) throw new Exception(text); checks++; Console.WriteLine("PASS "+text); }
    static void Reject(Action action,string text) { try {action();} catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException || e.InnerException is ArgumentException) {Check(true,text);return;} catch(InvalidOperationException) {Check(true,text);return;} throw new Exception("Expected rejection: "+text); }
    static T Copy<T>(T value) {using(var stream=new MemoryStream()){var json=new DataContractJsonSerializer(typeof(T));json.WriteObject(stream,value);stream.Position=0;return (T)json.ReadObject(stream);}}
    static string Public(Wallet w) {using(var rsa=new RSACryptoServiceProvider()){rsa.PersistKeyInCsp=false;rsa.FromXmlString(w.ExportPrivateKeys().First());return rsa.ToXmlString(false);}}
    static string Sign(Wallet w,string text) {using(var rsa=new RSACryptoServiceProvider()){rsa.PersistKeyInCsp=false;rsa.FromXmlString(w.ExportPrivateKeys().First());return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(text),"SHA256"));}}
    static object Call(string name,params object[] args)=>typeof(Blockchain).GetMethod(name,Static).Invoke(null,args);
    static string Id(Transaction tx)=>(string)typeof(Transaction).GetMethod("CalculateId",Hidden).Invoke(tx,null);
    static Dictionary<string,UnspentOutput> Utxo(Blockchain chain)=>(Dictionary<string,UnspentOutput>)typeof(Blockchain).GetMethod("BuildUtxo",Hidden).Invoke(chain,null);
    static void Set(object value,string name,object next)=>value.GetType().GetProperty(name,BindingFlags.Public|Hidden).GetSetMethod(true).Invoke(value,new[]{next});
    static TransactionApproval Approval(Blockchain chain,Transaction tx,Wallet validator,string address,Transaction receipt) {
        var approval=new TransactionApproval {AnchorHeight=chain.Blocks.Last().Height,AnchorHash=chain.Blocks.Last().Hash,
            CollateralTransactionId=receipt.Id,CollateralOutputIndex=2,
            Proof=new TransactionValidation {TransactionId=tx.Id,ValidatorId=address,RewardAddress=address,PublicKey=Public(validator)}};
        approval.Proof.Signature=Sign(validator,(string)Call("CreateTransactionApprovalPayload",tx,approval));return approval;
    }
    public static void Main() {
        var chain=new Blockchain(); var a=new Wallet();var b=new Wallet();var c=new Wallet();var d=new Wallet();
        string aa=a.CreateReceiveAddress(),bb=b.CreateReceiveAddress(),cc=c.CreateReceiveAddress(),dd=d.CreateReceiveAddress();
        foreach(var address in new[]{aa,bb,cc,dd}) chain.AddBlock(new[]{chain.CreateWalletCreationTransaction(address,new Transaction[0])});
        var first=a.CreateInitialLaunchPurchase(chain,new Transaction[0],aa,aa,1);
        Check(Blockchain.IsFirstLaunchPurchase(first)&&Blockchain.IsSelfValidatedOperation(first),"first purchase bootstraps without validators");
        Reject(()=>chain.AddProofOfStakeBlock(new[]{first},new ValidatorStake[0]),"first purchase cannot claim a validator block reward");
        chain.AddBlock(new[]{first});
        var second=b.CreateInitialLaunchPurchase(chain,new Transaction[0],bb,bb,1);
        Check(!Blockchain.IsSelfValidatedOperation(second)&&Blockchain.RequiresLockedTokenApproval(second),"later purchase requires a locked-holder approval");
        Check(chain.GetValidatorsForTransactions(new[]{second}).Count==1,"confirmed first receipt supplies the only eligible validator");
        Check(Blockchain.MinimumValidatorsForTransactions(new[]{second})==1,"launch-only block needs one validator");
        long launchWeight = Blockchain.WalletCreationReward + first.Outputs[2].Amount;
        Check(chain.GetValidatorsForTransactions(new[]{second}).Single().LockedAmount==launchWeight,"launch purchase aggregates initial 600 and bought collateral once per identity");
        Reject(()=>chain.AddBlock(new[]{second}),"later purchase cannot bypass approval in an unsigned block");
        var stake=a.CreateValidatorStake(aa,launchWeight);
        Check(chain.CreateTransactionApproval(second,new[]{second},new[]{b.CreateValidatorStake(bb,first.Outputs[2].Amount)})==null,"unconfirmed purchaser cannot approve its own purchase");
        second.TransactionApproval=chain.CreateTransactionApproval(second,new[]{second},new[]{stake});
        Check(second.TransactionApproval!=null&&chain.HasValidTransactionApproval(second,new[]{second}),"locked receipt signs a valid purchase approval");
        var bad=Copy(second);bad.TransactionApproval.Proof.Signature=Convert.ToBase64String(new byte[256]);
        Reject(()=>chain.AddProofOfStakeBlock(new[]{bad},new[]{stake}),"invalid approval signature is rejected");
        var malformed=a.CreateValidatorStake(aa,launchWeight+1);
        Reject(()=>chain.AddProofOfStakeBlock(new[]{second},new[]{malformed}),"validator cannot inflate its locked receipt weight");
        long before=chain.GetBalance(new[]{aa}); int height=chain.Blocks.Count;
        var block=chain.AddProofOfStakeBlock(new[]{second},new[]{stake});
        Check(block.Validators.Count==1&&block.Validators[0].IsCreator,"one locked holder creates and signs the launch block");
        Check(chain.GetBalance(new[]{aa})-before==ProofOfStake.GetBlockReward(height)+second.Fee,"single creator receives full scheduled reward plus purchase fee");
        Check(new Blockchain(Copy(chain.Blocks.ToArray())).IsValid(),"single-validator launch block survives full chain replay");
        var third=c.CreateInitialLaunchPurchase(chain,new Transaction[0],cc,cc,1);
        Check(chain.GetValidatorsForTransactions(new[]{third}).Count==2,"second confirmed buyer also becomes eligible");
        var stakeB=b.CreateValidatorStake(bb,second.Outputs[2].Amount);
        third.TransactionApproval=chain.CreateTransactionApproval(third,new[]{third},new[]{stakeB});
        var oneOfMany=chain.AddProofOfStakeBlock(new[]{third},new[]{stakeB});
        Check(oneOfMany.Validators.Count==1,"one online eligible holder may produce a block even when other holders exist");
        Check(!Blockchain.HasCompatibleValidatorRules(new[]{third,new Transaction{Kind=TransactionKind.Transfer}}), "launch purchases cannot mix with ordinary transactions to bypass holder-only production");
        var transfer=a.CreateTransaction(chain,new Transaction[0],dd,Blockchain.OneCoin,1);
        Check(Blockchain.MinimumValidatorsForTransactions(new[]{transfer})==2,"ordinary transfers keep the two-validator minimum");
        Reject(()=>chain.AddProofOfStakeBlock(new[]{transfer},new[]{stake}),"launch-only rights cannot validate an ordinary transfer block");
        Check(chain.CreateTransactionApproval(transfer,new[]{transfer},new[]{stakeB})!=null,"existing automatic collateral can approve ordinary transfers during launch");
        var e=new Wallet();var f=new Wallet();string ee=e.CreateReceiveAddress(),ff=f.CreateReceiveAddress();
        chain.AddBlock(new[]{a.CreateSponsoredStakeLockTransaction(chain,new Transaction[0],Public(e),Blockchain.OneCoin)});
        chain.AddBlock(new[]{a.CreateSponsoredStakeLockTransaction(chain,new Transaction[0],Public(f),Blockchain.OneCoin)});
        var stakeE=e.CreateValidatorStake(ee,Blockchain.OneCoin);var stakeF=f.CreateValidatorStake(ff,Blockchain.OneCoin);
        // Near-target fixture: real final purchase, approval and block vote checks.
        var state=Utxo(chain); foreach(var item in state.Values) Set(item,"Output",Copy(item.Output)); var marker=state.Values.Single(item=>typeof(UnspentOutput).GetProperty("InitialLaunchState",Hidden).GetValue(item,null)!=null);
        var launch=typeof(UnspentOutput).GetProperty("InitialLaunchState",Hidden).GetValue(marker,null);Set(launch,"Purchases",InitialLaunch.MaximumPurchases-1);
        var current=chain.GetInitialLaunchStatus();var ra=state[current.ReserveTransactionId+":0"];var rb=state[current.ReserveTransactionId+":1"];
        ra.Output.Amount=InitialLaunch.PrincipalSupply+InitialLaunch.PurchaseAmount;rb.Output.Amount=(InitialLaunch.MaximumPurchases-1L)*InitialLaunch.FundingUnit;
        var draft=TokenExchange.PrepareInitialLaunchPurchase(chain,new Transaction[0],new[]{Public(d)},ra,rb,InitialLaunch.ProviderPublicKey,Public(d),dd,1);
        string inputSignature=Sign(d,Encoding.UTF8.GetString(draft.SigningPayload));
        var final=draft.Complete(Enumerable.Repeat(inputSignature,draft.UserInputCount),inputSignature);
        final.TransactionApproval=Approval(chain,final,a,aa,first);
        height=chain.Blocks.Count;var reward=new Transaction {TimestampUtcTicks=DateTime.UtcNow.Ticks,Outputs=new List<TransactionOutput>{new TransactionOutput{OneTimeAddress=aa,Amount=ProofOfStake.GetBlockReward(height)}}};reward.Id=Id(reward);
        var record=new BlockValidator {ValidatorId=aa,RewardAddress=aa,LockedAmount=launchWeight,OwnedAddresses=new List<string>{aa},PublicKey=Public(a),IsCreator=true};
        var finalBlock=new Block {ConsensusVersion=Blockchain.ConsensusVersion,Height=height,PreviousHash=chain.Blocks.Last().Hash,TimestampUtcTicks=reward.TimestampUtcTicks,
            Transactions=new List<Transaction>{reward,final},Validators=new List<BlockValidator>{record},TransactionValidations=new List<TransactionValidation>{final.TransactionApproval.Proof}};
        record.VoteSignature=Sign(a,(string)Call("CreateVotePayload",finalBlock));
        Call("ValidateProofOfStakeBlock",finalBlock,finalBlock.PreviousHash,state,chain.Blocks.ToList());
        Call("Apply",final,state,false,true,Blockchain.ConsensusVersion,height);
        Check(((InitialLaunchStatus)typeof(UnspentOutput).GetProperty("InitialLaunchState",Hidden).GetValue(marker,null)).Purchases==InitialLaunch.MaximumPurchases-1,"boundary replay replaces rather than mutates prior launch state");
        var eligible=(ValidatorStake[])Call("ConsensusValidators",new[]{final},state,chain.Blocks.ToList());
        Check(eligible.Length==0,"launch-only validator rights expire immediately after target activation");
        Check(state[final.Id+":0"].PoolParticipation.LockedShares==InitialLaunch.PrincipalSupply,"final single-validator purchase preserves permanent liquidity principal");
        Check(ProofOfStake.GetBlockReward(height+1)>0&&Blockchain.MinimumValidatorsForTransactions(new[]{new Transaction{Kind=TransactionKind.LiquiditySwap}})==2,"normal rewards continue after activation with normal validator rules");
        var normal=a.CreateTransaction(chain,new Transaction[0],dd,Blockchain.OneCoin,1);
        normal.TransactionApproval=chain.CreateTransactionApproval(normal,new[]{normal},new[]{stakeE});
        var regular=new[]{stakeE,stakeF};string previousHash=new string('a',64);int nextHeight=height+1;
        var creator=ProofOfStake.SelectCreator(regular,previousHash,nextHeight);
        var shares=ProofOfStake.DistributeReward(nextHeight,creator,regular.Where(item=>item.ValidatorId!=creator.ValidatorId));
        var normalReward=new Transaction {TimestampUtcTicks=DateTime.UtcNow.Ticks,Outputs=shares.Select(item=>new TransactionOutput{OneTimeAddress=item.RewardAddress,Amount=item.Amount}).ToList()};normalReward.Id=Id(normalReward);
        var normalBlock=new Block {ConsensusVersion=Blockchain.ConsensusVersion,Height=nextHeight,PreviousHash=previousHash,TimestampUtcTicks=normalReward.TimestampUtcTicks,
            Transactions=new List<Transaction>{normalReward,normal},TransactionValidations=new List<TransactionValidation>{normal.TransactionApproval.Proof},
            Validators=regular.Select(item=>new BlockValidator {ValidatorId=item.ValidatorId,RewardAddress=item.RewardAddress,LockedAmount=item.LockedAmount,OwnedAddresses=item.OwnedAddresses.ToList(),PublicKey=item.PublicKey,IsCreator=item.ValidatorId==creator.ValidatorId}).ToList()};
        foreach(var vote in normalBlock.Validators) vote.VoteSignature=Sign(vote.ValidatorId==ee?e:f,(string)Call("CreateVotePayload",normalBlock));
        var oneNormal=Copy(normalBlock);oneNormal.Validators.RemoveAt(1);
        Reject(()=>Call("ValidateProofOfStakeBlock",oneNormal,previousHash,state,chain.Blocks.ToList()),"one validator cannot produce an ordinary post-activation block");
        var oneRegistration = Copy(oneNormal);
        var newWallet = new Wallet();
        oneRegistration.Transactions[1] = chain.CreateWalletCreationTransaction(newWallet.CreateReceiveAddress(), new Transaction[0]);
        oneRegistration.TransactionValidations.Clear();
        Reject(()=>Call("ValidateProofOfStakeBlock",oneRegistration,previousHash,state,chain.Blocks.ToList()),"one validator cannot claim registration rewards after launch activation");
        Call("ValidateProofOfStakeBlock",normalBlock,previousHash,state,chain.Blocks.ToList());
        Call("Apply",normal,state,false,true,Blockchain.ConsensusVersion,nextHeight);
        Check(normalReward.Outputs.Sum(item=>item.Amount)==ProofOfStake.GetBlockReward(nextHeight),"post-activation ordinary block pays the unchanged scheduled reward");
        Check(state.ContainsKey(normal.Id+":"+normal.Outputs.Count),"ordinary post-activation validator receives the transaction fee");
        Console.WriteLine(checks+" launch validator checks passed");
    }
}
