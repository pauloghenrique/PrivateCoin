import test from 'node:test';
import assert from 'node:assert/strict';
import ganache from 'ganache';
import { createServer } from 'node:http';
import { BrowserProvider, Wallet, ContractFactory, keccak256 } from 'ethers';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { compile, root } from '../src/compile.mjs';
import { journal, parseJson, protocolToken, networkHash, domain, signingTypes, sortSignatures, validateConfig } from '../src/bridge.mjs';
import { createNativeOperator, loadNativeOperator } from '../src/native-keystore.mjs';
import { propose, signProposal, complete, verifyProductionDeposit, verifyProductionBurn, audit, refreshProposal } from '../src/production.mjs';
const mono=process.env.POVIX_MONO||'mono';
const read=file=>parseJson(readFileSync(file,'utf8'));
async function rpcServer(provider){
 const server=createServer(async(req,res)=>{let body='';for await(const part of req)body+=part;
  const call=async item=>{try{return {jsonrpc:'2.0',id:item.id,result:await provider.request({method:item.method,params:item.params||[]})};}catch(error){return {jsonrpc:'2.0',id:item.id,error:{code:error.code||-32000,message:error.message}};}};
  const input=JSON.parse(body),output=Array.isArray(input)?await Promise.all(input.map(call)):await call(input);
  res.setHeader('Content-Type','application/json');res.end(JSON.stringify(output));});
 server.keepAliveTimeout=120000;server.headersTimeout=125000;
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 return {url:'http://127.0.0.1:'+server.address().port,close:()=>new Promise(resolve=>server.close(resolve))};
}
test('production federation: independent keys, consensus claim, delayed release and emergency controls',{timeout:900000},async()=>{
 const dir=mkdtempSync(path.join(tmpdir(),'povix-production-'));
 const rpc=ganache.provider({chain:{chainId:31337},logging:{quiet:true},wallet:{totalAccounts:7}});
 const provider=new BrowserProvider(rpc,undefined,{cacheTimeout:-1});provider.pollingInterval=10;
 const servers=await Promise.all([rpcServer(rpc),rpcServer(rpc)]);const journals=[];
 try {
  const run=(mode,...args)=>execFileSync(mono,[path.join(root,'build/native/ProductionNativeRegression.exe'),mode,dir,...args],{encoding:'utf8',timeout:240000});run('setup');
  const password='test-password-for-independent-native-operators';process.env.NATIVE_KEYSTORE_PASSWORD=password;
  const operators=Array.from({length:5},(_,i)=>createNativeOperator(path.join(dir,'operator-'+i+'.local.json'),password));
  assert.throws(()=>loadNativeOperator(path.join(dir,'operator-0.local.json'),'wrong-password'));
  const evm=Object.values(rpc.getInitialAccounts()).map(a=>new Wallet(a.secretKey));
  const signer=await provider.getSigner(0),mock=compile('test/MockUsdt.sol','MockUsdt'),artifact=compile('contracts/ProductionVault.sol','ProductionVault');
  const token=await new ContractFactory(mock.abi,mock.bytecode,signer).deploy();await token.waitForDeployment();
  const config={mode:'production',chainId:31337,rpcUrl:servers[0].url,rpcUrls:servers.map(s=>s.url),asset:(await token.getAddress()).toLowerCase(),bridge:null,
   nativeNetwork:execFileSync(mono,[path.join(root,'build/native/BridgeNative.exe'),'network'],{encoding:'utf8'}).trim(),nativeToken:protocolToken,
   nativeAdministrators:operators.map(o=>o.address),nativeThreshold:3,nativeSigningPublicKeys:operators.slice(0,3).map(o=>o.publicKey),nativeConfirmations:12,bscConfirmations:64,
   nativeSnapshot:path.join(dir,'chain.json'),nativeSnapshots:['chain.json','independent-chain.json'].map(f=>path.join(dir,f)),
   operators:evm.slice(0,5).map(w=>w.address),threshold:3,reserveCap:'1000000000',dailyReleaseCap:'100000000',assetCodeHash:keccak256(await provider.getCode(await token.getAddress()))};
  validateConfig(config);assert.throws(()=>validateConfig({...config,chainId:56,mode:'test'}));assert.throws(()=>validateConfig({...config,chainId:56,rpcUrls:['http://host1','http://host2']}));
  const vault=await new ContractFactory(artifact.abi,artifact.bytecode,signer).deploy(config.asset,networkHash(config),'0x'+protocolToken,config.operators,3,config.reserveCap,config.dailyReleaseCap);await vault.waitForDeployment();
  config.bridge=(await vault.getAddress()).toLowerCase();config.vaultCodeHash=keccak256(await provider.getCode(config.bridge));validateConfig(config);
  writeFileSync(path.join(dir,'config.json'),JSON.stringify(config));console.log(run('configure').trim());
  assert.equal(await vault.bridgeVersion(),2n);assert.equal(await vault.releaseDelay(),86400n);
  const signEvm=async(kind,value)=>sortSignatures(config,kind,value,await Promise.all(evm.slice(0,3).map(w=>w.signTypedData(domain(config),signingTypes(config,kind),value))));
  const deadline=async()=>Number((await provider.getBlock('latest')).timestamp)+300;
  let pause={paused:false,nonce:(await vault.pauseNonce()).toString(),deadline:await deadline()};await(await vault.setPaused(false,pause.deadline,await signEvm('Pause',pause))).wait();
  const holder=readFileSync(path.join(dir,'holder.txt'),'utf8');await(await token.approve(config.bridge,1000000000000000000n)).wait();
  const receipt=await(await vault.deposit(1000000000000000000n,'0x'+holder)).wait();
  const log=receipt.logs.find(l=>{try{return vault.interface.parseLog(l)?.name==='Deposited';}catch{return false;}}),reference={transactionHash:receipt.hash,logIndex:log.index};
  await assert.rejects(verifyProductionDeposit(config,reference));for(let i=0;i<63;i++)await rpc.request({method:'evm_mine',params:[]});
  const coordinator=journal(path.join(dir,'coordinator.sqlite'),config);journals.push(coordinator);
  console.log('Verifying deposit and creating production proposal');
  let proposal=await propose(config,'mint',reference,coordinator);assert.equal(proposal.draft.TokenCommand.BridgeDepositId.length,64);assert.deepEqual(await propose(config,'mint',reference,coordinator),proposal);
  console.log('Collecting three independent native signatures');
  const signatures=[];
  for(let i=0;i<3;i++){const db=journal(path.join(dir,'operator-'+i+'.sqlite'),config);journals.push(db);signatures.push(await signProposal(config,proposal,path.join(dir,'operator-'+i+'.local.json'),db));}
  await assert.rejects(complete(config,proposal,signatures.slice(0,2)));await assert.rejects(complete(config,proposal,[signatures[0],signatures[0],signatures[2]]));
  await assert.rejects(complete(config,{...proposal,deposit:{...proposal.deposit,recipient:'f'.repeat(64)}},signatures));
  const refreshed=await refreshProposal(config,proposal,coordinator);
  await assert.rejects(complete(config,refreshed,signatures));
  assert.equal(coordinator.prepare('SELECT COUNT(*) AS n FROM proposal_history').get().n,1);
  proposal=refreshed;signatures.length=0;
  for(let i=0;i<3;i++)signatures.push(await signProposal(config,proposal,path.join(dir,'operator-'+i+'.local.json'),journals[i+1]));
  const mint=await complete(config,proposal,signatures);writeFileSync(path.join(dir,'mint.json'),JSON.stringify(mint));console.log(run('confirm-mint').trim());
  assert.equal((await audit(config)).backed,true);
  await assert.rejects(signProposal(config,proposal,path.join(dir,'operator-0.local.json'),journals[1]));
  for(const kind of ['pause','resume']) {
   const control=await propose(config,kind,null,coordinator),proofs=[];
   for(let i=0;i<3;i++)proofs.push(await signProposal(config,control,path.join(dir,'operator-'+i+'.local.json'),journals[i+1]));
   writeFileSync(path.join(dir,'control.json'),JSON.stringify(await complete(config,control,proofs)));run('confirm-control');
   assert.equal((await audit(config)).native.paused,kind==='pause');
  }
  console.log('PASS proposal refresh and independent native pause/resume');
  console.log('Confirming native burn');
  console.log(run('burn',evm[6].address.toLowerCase()).trim());assert.throws(()=>verifyProductionBurn({...config,nativeConfirmations:120},read(path.join(dir,'burn.json'))));const evidence=verifyProductionBurn(config,read(path.join(dir,'burn.json')));
  let value={burnId:evidence.depositId,recipient:evidence.recipient,nativeAmount:evidence.amount,deadline:await deadline(),nonce:'0'};
  const signatures0=await signEvm('Release',value);await(await vault.release(value.burnId,value.recipient,value.nativeAmount,value.deadline,signatures0)).wait();
  assert.equal(await token.balanceOf(evm[6].address),0n);await assert.rejects(vault.executeRelease(value.burnId));
  await(await vault.cancelRelease(value.burnId)).wait();assert.equal(await vault.reservedForRelease(),0n);await assert.rejects(vault.release(value.burnId,value.recipient,value.nativeAmount,value.deadline,signatures0));
  value={...value,nonce:'1',deadline:await deadline()};await(await vault.release(value.burnId,value.recipient,value.nativeAmount,value.deadline,await signEvm('Release',value))).wait();
  await(await vault.emergencyPause()).wait();await rpc.request({method:'evm_increaseTime',params:[86401]});await rpc.request({method:'evm_mine',params:[]});await assert.rejects(vault.executeRelease(value.burnId));
  const block=await provider.getBlock('latest');pause={paused:false,nonce:(await vault.pauseNonce()).toString(),deadline:Number(block.timestamp)+600};
  await(await vault.setPaused(false,pause.deadline,await signEvm('Pause',pause))).wait();await(await vault.executeRelease(value.burnId)).wait();
  assert.equal(await token.balanceOf(evm[6].address),500000000000000000n);assert.equal(await vault.accountedReserve(),50000000n);await assert.rejects(vault.executeRelease(value.burnId));
 }finally{for(const db of journals)db.close();await provider.destroy();await Promise.all(servers.map(s=>s.close()));await rpc.disconnect();rmSync(dir,{recursive:true,force:true});delete process.env.NATIVE_KEYSTORE_PASSWORD;}
});
