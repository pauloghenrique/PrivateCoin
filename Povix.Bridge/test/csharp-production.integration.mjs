import test from 'node:test';
import { generateKeyPairSync, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import ganache from 'ganache';
import { createServer } from 'node:http';
import { BrowserProvider, Wallet, ContractFactory, keccak256, encryptKeystoreJson } from 'ethers';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { execFileSync, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { compile, root } from '../src/compile.mjs';
import { parseJson, protocolToken, networkHash, domain, signingTypes, sortSignatures, validateConfig } from '../src/bridge.mjs';
import { publicXml, loadNativeOperator } from '../src/native-keystore.mjs';
const invoke=promisify(execFile);
const csharp=path.join(root,'bin/CSharp/Release/Povix.Bridge.exe'),regression=path.join(root,'bin/Tests/Release/CSharpRegression.exe');
const configs=new WeakMap();let configSequence=0;
function configFile(c){let file=configs.get(c);if(!file){file=path.join(path.dirname(c.nativeSnapshot),'csharp-config-'+configSequence+++'.json');writeFileSync(file,JSON.stringify(c));configs.set(c,file);}return file;}
async function cs(c,command,...args){const result=await invoke(mono,[csharp,command,configFile(c),...args],{encoding:'utf8',timeout:300000,maxBuffer:16*1024*1024});return parseJson(result.stdout);}
function dataFile(c,value){const file=path.join(path.dirname(c.nativeSnapshot),'csharp-data-'+configSequence+++'.json');writeFileSync(file,JSON.stringify(value));return file;}
function journal(file){file=file.replace(/\.sqlite$/,'.journal');return {file,close(){},prepare(){return {get(){return {n:readFileSync(file,'utf8').trim().split('\n').map(JSON.parse).filter(r=>r.key.startsWith('history:')).length};}};}};}
function createNativeOperator(file){return parseJson(execFileSync(mono,[csharp,'create-native-operator',file],{encoding:'utf8',timeout:120000}));}
const propose=(c,kind,reference,db)=>cs(c,'propose-native',kind,reference?dataFile(c,reference):'-',db.file);
const signProposal=(c,p,key,db)=>cs(c,'sign-native',dataFile(c,p),key,db.file);
const complete=(c,p,signatures)=>cs(c,'complete-native',dataFile(c,p),dataFile(c,signatures));
const verifyProductionBurn=(c,binding)=>cs(c,'verify-burn',dataFile(c,binding));
const verifyProductionDeposit=(c,r)=>cs(c,'propose-native','mint',dataFile(c,r),path.join(path.dirname(c.nativeSnapshot),'not-confirmed.journal'));
const audit=c=>cs(c,'audit');
const refreshProposal=(c,p,db)=>cs(c,'refresh-native',dataFile(c,p),db.file);
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
test('C# client without Node runtime: key compatibility, deposit, independent signatures, replay, burn and delayed release',{timeout:900000},async()=>{
 const dir=mkdtempSync(path.join(tmpdir(),'povix-production-'));
 const rpc=ganache.provider({chain:{chainId:31337},logging:{quiet:true},wallet:{totalAccounts:7}});
 const provider=new BrowserProvider(rpc,undefined,{cacheTimeout:-1});provider.pollingInterval=10;
 const servers=await Promise.all([rpcServer(rpc),rpcServer(rpc)]);const journals=[];
 try {
  const run=(mode,...args)=>execFileSync(mono,[path.join(root,'build/native/ProductionNativeRegression.exe'),mode,dir,...args],{encoding:'utf8',timeout:240000});run('setup');
  const password='test-password-for-independent-native-operators';process.env.NATIVE_KEYSTORE_PASSWORD=password;
  const operators=Array.from({length:2},(_,i)=>createNativeOperator(path.join(dir,'operator-'+i+'.local.json'),password));
  assert.equal(loadNativeOperator(path.join(dir,'operator-0.local.json'),password).address,operators[0].address);
  assert.throws(()=>loadNativeOperator(path.join(dir,'operator-0.local.json'),'wrong-password'));
  const evm=Object.values(rpc.getInitialAccounts()).map(a=>new Wallet(a.secretKey));
  process.env.BRIDGE_KEYSTORE_PASSWORD='test-password-for-csharp-evm-operators';
  for(let i=0;i<2;i++)writeFileSync(path.join(dir,'evm-'+i+'.local.json'),await encryptKeystoreJson({address:evm[i].address,privateKey:evm[i].privateKey},process.env.BRIDGE_KEYSTORE_PASSWORD,{scrypt:{N:1024,r:8,p:1}}),{mode:0o600});
  const evmFile=path.join(dir,'csharp-generated-evm.local.json');const generated=parseJson(execFileSync(mono,[csharp,'create-operator',evmFile],{encoding:'utf8',timeout:120000}));
  assert.equal((await Wallet.fromEncryptedJson(readFileSync(evmFile,'utf8'),process.env.BRIDGE_KEYSTORE_PASSWORD)).address.toLowerCase(),generated.address.toLowerCase());
  const signer=await provider.getSigner(0),mock=compile('test/MockUsdt.sol','MockUsdt'),artifact=compile('contracts/ProductionVault.sol','ProductionVault');
  const token=await new ContractFactory(mock.abi,mock.bytecode,signer).deploy();await token.waitForDeployment();
  const config={mode:'production',chainId:31337,rpcUrl:servers[0].url,rpcUrls:servers.map(s=>s.url),asset:(await token.getAddress()).toLowerCase(),bridge:null,
   nativeNetwork:execFileSync(mono,[path.join(root,'build/native/BridgeNative.exe'),'network'],{encoding:'utf8'}).trim(),nativeToken:protocolToken,
   nativeAdministrators:operators.map(o=>o.address),nativeThreshold:2,nativeSigningPublicKeys:operators.slice(0,2).map(o=>o.publicKey),nativeConfirmations:12,bscConfirmations:64,
   nativeSnapshot:path.join(dir,'chain.json'),nativeSnapshots:['chain.json','independent-chain.json'].map(f=>path.join(dir,f)),
   operators:evm.slice(0,2).map(w=>w.address),threshold:2,reserveCap:'1000000000',dailyReleaseCap:'100000000',assetCodeHash:keccak256(await provider.getCode(await token.getAddress()))};
  validateConfig(config);assert.throws(()=>validateConfig({...config,chainId:56,mode:'test'}));assert.throws(()=>validateConfig({...config,chainId:56,rpcUrls:['http://host1','http://host2']}));
  const vault=await new ContractFactory(artifact.abi,artifact.bytecode,signer).deploy(config.asset,networkHash(config),'0x'+protocolToken,config.operators,2,config.reserveCap,config.dailyReleaseCap);await vault.waitForDeployment();
  config.bridge=(await vault.getAddress()).toLowerCase();config.vaultCodeHash=keccak256(await provider.getCode(config.bridge));validateConfig(config);
  writeFileSync(path.join(dir,'config.json'),JSON.stringify(config));console.log(run('configure').trim());
  assert.equal(await vault.bridgeVersion(),2n);assert.equal(await vault.releaseDelay(),86400n);
  const signEvm=async(kind,value)=>sortSignatures(config,kind,value,await Promise.all([0,1].map(async i=>{const result=await invoke(mono,[regression,'sign-typed',configFile(config),kind,dataFile(config,value),path.join(dir,'evm-'+i+'.local.json')],{encoding:'utf8',timeout:120000});return result.stdout.trim();})));
  const unit=await invoke(mono,[regression,'unit',configFile(config),path.join(dir,'unit')],{encoding:'utf8',timeout:120000});console.log(unit.stdout.trim());
  const deadline=async()=>Number((await provider.getBlock('latest')).timestamp)+300;
  let pause={paused:false,nonce:(await vault.pauseNonce()).toString(),deadline:await deadline()};
  const pauseAuth=await Promise.all([0,1].map(i=>cs(config,'sign-pause',path.join(dir,'evm-'+i+'.local.json'),'false',String(pause.deadline))));
  await assert.rejects(cs(config,'pause',dataFile(config,pauseAuth.slice(0,1)),path.join(dir,'evm-0.local.json')));
  await cs(config,'pause',dataFile(config,pauseAuth),path.join(dir,'evm-0.local.json'));assert.equal(await vault.paused(),false);
  const holder=readFileSync(path.join(dir,'holder.txt'),'utf8');
  const deposit=await cs(config,'deposit','100000000',holder,path.join(dir,'evm-0.local.json'));
  const receipt=await provider.getTransactionReceipt(deposit.transactionHash);assert.equal(deposit.logIndex,receipt.logs.find(l=>{try{return vault.interface.parseLog(l)?.name==='Deposited';}catch{return false;}}).index);
  const log=receipt.logs.find(l=>{try{return vault.interface.parseLog(l)?.name==='Deposited';}catch{return false;}}),reference={transactionHash:receipt.hash,logIndex:log.index};
  await assert.rejects(verifyProductionDeposit(config,reference));for(let i=0;i<63;i++)await rpc.request({method:'evm_mine',params:[]});
  const coordinator=journal(path.join(dir,'coordinator.sqlite'),config);journals.push(coordinator);
  console.log('Verifying deposit and creating production proposal');
  let proposal=await propose(config,'mint',reference,coordinator);assert.equal(proposal.draft.TokenCommand.BridgeDepositId.length,64);assert.deepEqual(await propose(config,'mint',reference,coordinator),proposal);
  console.log('Collecting two independent native signatures');
  const signatures=[];
  for(let i=0;i<2;i++){const db=journal(path.join(dir,'operator-'+i+'.sqlite'),config);journals.push(db);signatures.push(await signProposal(config,proposal,path.join(dir,'operator-'+i+'.local.json'),db));}
  await assert.rejects(complete(config,proposal,signatures.slice(0,1)));await assert.rejects(complete(config,proposal,[signatures[0],signatures[0]]));
  await assert.rejects(complete(config,{...proposal,deposit:{...proposal.deposit,recipient:'f'.repeat(64)}},signatures));
  const refreshed=await refreshProposal(config,proposal,coordinator);
  await assert.rejects(complete(config,refreshed,signatures));
  assert.equal(coordinator.prepare('SELECT COUNT(*) AS n FROM proposal_history').get().n,1);
  proposal=refreshed;signatures.length=0;
  for(let i=0;i<2;i++)signatures.push(await signProposal(config,proposal,path.join(dir,'operator-'+i+'.local.json'),journals[i+1]));
  const mint=await complete(config,proposal,signatures);writeFileSync(path.join(dir,'mint.json'),JSON.stringify(mint));console.log(run('confirm-mint').trim());
  assert.equal((await audit(config)).backed,true);
  await assert.rejects(signProposal(config,proposal,path.join(dir,'operator-0.local.json'),journals[1]));
  for(const kind of ['pause','resume']) {
   const control=await propose(config,kind,null,coordinator),proofs=[];
   for(let i=0;i<2;i++)proofs.push(await signProposal(config,control,path.join(dir,'operator-'+i+'.local.json'),journals[i+1]));
   writeFileSync(path.join(dir,'control.json'),JSON.stringify(await complete(config,control,proofs)));run('confirm-control');
   assert.equal((await audit(config)).native.paused,kind==='pause');
  }
  console.log('PASS proposal refresh and independent native pause/resume');
  console.log('Confirming native burn');
  console.log(run('burn',evm[6].address.toLowerCase()).trim());await assert.rejects(verifyProductionBurn({...config,nativeConfirmations:120},read(path.join(dir,'burn.json'))));const evidence=await verifyProductionBurn(config,read(path.join(dir,'burn.json')));await rpc.request({method:'evm_mine',params:[]});
  let value={burnId:evidence.depositId,recipient:evidence.recipient,nativeAmount:evidence.amount,deadline:await deadline(),nonce:'0'};
  const releaseAuth=await Promise.all([0,1].map(i=>cs(config,'sign-release',path.join(dir,'burn.json'),path.join(dir,'evm-'+i+'.local.json'),path.join(dir,'release-'+i+'.journal'),String(value.deadline))));
  const signatures0=sortSignatures(config,'Release',value,releaseAuth.map(a=>a.signature));
  await assert.rejects(cs(config,'release',path.join(dir,'burn.json'),dataFile(config,releaseAuth.slice(0,1)),path.join(dir,'evm-0.local.json')));
  await assert.rejects(cs(config,'release',path.join(dir,'burn.json'),dataFile(config,[releaseAuth[0],releaseAuth[0]]),path.join(dir,'evm-0.local.json')));
  const queued=await cs(config,'release',path.join(dir,'burn.json'),dataFile(config,releaseAuth),path.join(dir,'evm-0.local.json'));assert.equal(queued.stage,'queued-24h');
  assert.equal(await token.balanceOf(evm[6].address),0n);await assert.rejects(vault.executeRelease(value.burnId));
  await cs(config,'cancel-release',value.burnId,path.join(dir,'evm-0.local.json'));assert.equal(await vault.reservedForRelease(),0n);await assert.rejects(vault.release(value.burnId,value.recipient,value.nativeAmount,value.deadline,signatures0));
  value={...value,nonce:'1',deadline:await deadline()};await(await vault.release(value.burnId,value.recipient,value.nativeAmount,value.deadline,await signEvm('Release',value))).wait();
  console.log('Verifying C# factory deployment and reviewed asset onboarding');
  const factoryInfo=await cs(config,'deploy-factory',path.join(dir,'evm-0.local.json')),factoryConfig={...config,...factoryInfo};
  const assetArtifact=compile('test/MockAsset.sol','MockAsset'),other=await new ContractFactory(assetArtifact.abi,assetArtifact.bytecode,signer).deploy(6);await other.waitForDeployment();
  await rpc.request({method:'evm_mine',params:[]});
  const profile={asset:(await other.getAddress()).toLowerCase(),assetCodeHash:keccak256(await provider.getCode(await other.getAddress())),cap:'1000000000',dailyCap:'100000000',deadline:Math.floor(Date.now()/1000)+300};
  const profileFile=dataFile(factoryConfig,profile),assetAuth=await Promise.all([0,1].map(i=>cs(factoryConfig,'sign-asset',profileFile,path.join(dir,'evm-'+i+'.local.json'),path.join(dir,'asset-'+i+'.journal'))));
  await assert.rejects(cs(factoryConfig,'create-asset',profileFile,dataFile(factoryConfig,assetAuth.slice(0,1)),path.join(dir,'evm-0.local.json')));
  const created=await cs(factoryConfig,'create-asset',profileFile,dataFile(factoryConfig,assetAuth),path.join(dir,'evm-0.local.json'));
  const resolved=await cs(factoryConfig,'production-asset-config',profileFile);assert.equal(resolved.bridge,created.vault);assert.equal(resolved.nativeDecimals,6);assert.equal(resolved.nativeToken.slice(0,4),'b9b0');
  const registerDb=journal(path.join(dir,'register.journal'));const registration=await propose(resolved,'register',null,registerDb),registrationSignatures=[];
  for(let i=0;i<2;i++)registrationSignatures.push(await signProposal(resolved,registration,path.join(dir,'operator-'+i+'.local.json'),journal(path.join(dir,'register-'+i+'.journal'))));
  writeFileSync(path.join(dir,'control.json'),JSON.stringify(await complete(resolved,registration,registrationSignatures)));run('confirm-control');
  assert.equal((await audit(resolved)).backed,true);console.log('PASS C# factory, asset config and native registration');
  await cs(config,'emergency-pause',path.join(dir,'evm-0.local.json'));await rpc.request({method:'evm_increaseTime',params:[86401]});await rpc.request({method:'evm_mine',params:[]});await assert.rejects(vault.executeRelease(value.burnId));
  const block=await provider.getBlock('latest');pause={paused:false,nonce:(await vault.pauseNonce()).toString(),deadline:Number(block.timestamp)+600};
  await(await vault.setPaused(false,pause.deadline,await signEvm('Pause',pause))).wait();await cs(config,'execute-release',value.burnId,path.join(dir,'evm-0.local.json'));
  assert.equal(await token.balanceOf(evm[6].address),500000000000000000n);assert.equal(await vault.accountedReserve(),50000000n);await assert.rejects(vault.executeRelease(value.burnId));
 }finally{for(const db of journals)db.close();await provider.destroy();await Promise.all(servers.map(s=>s.close()));await rpc.disconnect();rmSync(dir,{recursive:true,force:true});delete process.env.NATIVE_KEYSTORE_PASSWORD;delete process.env.BRIDGE_KEYSTORE_PASSWORD;}
});

test('C# deployment pins source code and publishes a paused two-operator vault',{timeout:180000},async()=>{
 const dir=mkdtempSync(path.join(tmpdir(),'povix-csharp-deploy-'));
 const rpc=ganache.provider({chain:{chainId:31337},logging:{quiet:true},wallet:{totalAccounts:3}}),provider=new BrowserProvider(rpc,undefined,{cacheTimeout:-1});provider.pollingInterval=10;
 const servers=await Promise.all([rpcServer(rpc),rpcServer(rpc)]);
 const password='test-password-for-csharp-deployment';
 try {
  const wallets=Object.values(rpc.getInitialAccounts()).map(a=>new Wallet(a.secretKey)),signer=await provider.getSigner(0);
  const mock=compile('test/MockUsdt.sol','MockUsdt'),token=await new ContractFactory(mock.abi,mock.bytecode,signer).deploy();await token.waitForDeployment();
  const keys=Array.from({length:2},()=>publicXml(generateKeyPairSync('rsa',{modulusLength:3072}).privateKey));
  const config={mode:'production',chainId:31337,rpcUrl:servers[0].url,rpcUrls:servers.map(s=>s.url),asset:(await token.getAddress()).toLowerCase(),bridge:null,assetCodeHash:keccak256(await provider.getCode(await token.getAddress())),vaultCodeHash:null,
   nativeNetwork:execFileSync(mono,[csharp,'network'],{encoding:'utf8'}).trim(),nativeToken:protocolToken,nativeAdministrators:keys.map(k=>createHash('sha256').update(k).digest('hex')),nativeSigningPublicKeys:keys,nativeThreshold:2,
   nativeSnapshot:path.join(dir,'node1.json'),nativeSnapshots:['node1.json','node2.json'].map(f=>path.join(dir,f)),operators:wallets.slice(0,2).map(w=>w.address),threshold:2,reserveCap:'1000000000',dailyReleaseCap:'100000000',nativeConfirmations:12,bscConfirmations:64};
  const file=path.join(dir,'relay.local.json');writeFileSync(file,await encryptKeystoreJson({address:wallets[0].address,privateKey:wallets[0].privateKey},password,{scrypt:{N:1024,r:8,p:1}}),{mode:0o600});
  const nonce=await provider.getTransactionCount(wallets[0].address);
  await assert.rejects(invoke(mono,[csharp,'deploy',configFile({...config,assetCodeHash:'0x'+'f'.repeat(64)}),file],{encoding:'utf8',timeout:120000,env:{...process.env,BRIDGE_KEYSTORE_PASSWORD:password}}));
  assert.equal(await provider.getTransactionCount(wallets[0].address),nonce);
  const result=await invoke(mono,[csharp,'deploy',configFile(config),file],{encoding:'utf8',timeout:120000,env:{...process.env,BRIDGE_KEYSTORE_PASSWORD:password}}),deployed=parseJson(result.stdout);
  const abi=compile('contracts/ProductionVault.sol','ProductionVault').abi;
  const {Contract}=await import('ethers');const vault=new Contract(deployed.bridge,abi,provider);
  assert.equal(await vault.paused(),true);assert.equal(await vault.threshold(),2n);assert.equal(await vault.operators(0),wallets[0].address);assert.equal(await vault.operators(1),wallets[1].address);
  assert.equal(await vault.releaseDelay(),86400n);assert.equal(await vault.reserveCap(),1000000000n);assert.equal(deployed.vaultCodeHash,keccak256(await provider.getCode(deployed.bridge)));
 }finally{await provider.destroy();await Promise.all(servers.map(s=>s.close()));await rpc.disconnect();rmSync(dir,{recursive:true,force:true});}
});
