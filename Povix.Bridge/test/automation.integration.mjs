import test from 'node:test';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import ganache from 'ganache';
import { createServer } from 'node:http';
import { createServer as tcpServer } from 'node:net';
import { BrowserProvider, Wallet, ContractFactory, encryptKeystoreJson, keccak256 } from 'ethers';
import { mkdtempSync, writeFileSync, readFileSync, readdirSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { execFile, execFileSync, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { compile, root } from '../src/compile.mjs';
import { parseJson, protocolToken, networkHash } from '../src/bridge.mjs';
const invoke=promisify(execFile),mono=process.env.POVIX_MONO||'mono';
const cli=path.join(root,'bin/CSharp/Release/Povix.Bridge.exe'),helper=path.join(root,'bin/Tests/Release/CSharpRegression.exe');
const native=path.join(root,'build/native/ProductionNativeRegression.exe');
const canonical=v=>JSON.isRawJSON(v)?JSON.stringify(v):Array.isArray(v)?'['+v.map(canonical).join(',')+']':v&&typeof v==='object'?'{'+Object.keys(v).sort().map(k=>JSON.stringify(k)+':'+canonical(v[k])).join(',')+'}':JSON.stringify(v);
async function server(rpc,control) {
 const s=createServer(async(req,res)=>{
  let body='';for await(const chunk of req)body+=chunk;
  async function call(item){try{const result=await rpc.request({method:item.method,params:item.params||[]});
   if(control.gasSpike&&item.method==='eth_gasPrice')return {jsonrpc:'2.0',id:item.id,result:'0x165a0bc00'};
   if(control.loseSendReply&&item.method==='eth_sendRawTransaction'){control.loseSendReply=false;return {jsonrpc:'2.0',id:item.id,error:{code:-32000,message:'simulated lost response AFTER broadcast'}};}
   if(control.corruptHistoricalBlock&&item.method==='eth_getBlockByNumber'&&item.params[0]!=='latest'&&result) return {jsonrpc:'2.0',id:item.id,result:{...result,hash:'0x'+'ff'.repeat(32)}};
   return {jsonrpc:'2.0',id:item.id,result};}catch(e){return {jsonrpc:'2.0',id:item.id,error:{code:e.code||-32000,message:e.message}};}}
  const input=JSON.parse(body);res.setHeader('Content-Type','application/json');res.end(JSON.stringify(Array.isArray(input)?await Promise.all(input.map(call)):await call(input)));
 });await new Promise(resolve=>s.listen(0,'127.0.0.1',resolve));return {url:'http://127.0.0.1:'+s.address().port,close:()=>new Promise(resolve=>s.close(resolve))};
}
async function freePort(){const s=tcpServer();await new Promise(resolve=>s.listen(0,'127.0.0.1',resolve));const port=s.address().port;await new Promise(resolve=>s.close(resolve));return port;}
async function peer(port,file){const p=spawn(mono,[helper,'automation-peer',String(port),file],{stdio:['ignore','pipe','pipe']});
 let output='';await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('peer startup timeout')),20000);p.stdout.on('data',c=>{output+=c;if(output.includes('READY')){clearTimeout(timer);resolve();}});p.on('exit',code=>reject(new Error('peer exited '+code)));});return p;}

test('automatic operators: discover deposit, independent signatures, P2P mint, restart-safe delayed release',{timeout:900000},async()=>{
 const dir=mkdtempSync(path.join(tmpdir(),'povix-automation-')),rpc=ganache.provider({chain:{chainId:31337},logging:{quiet:true},wallet:{totalAccounts:5}});
 const provider=new BrowserProvider(rpc,undefined,{cacheTimeout:-1});provider.pollingInterval=10;
 const controls=[{loseSendReply:false},{loseSendReply:false}],servers=await Promise.all(controls.map(c=>server(rpc,c)));
 let nativePeer;
 const password='automatic-operators-test-password-strong';
 const env={...process.env,NATIVE_KEYSTORE_PASSWORD:password,BRIDGE_KEYSTORE_PASSWORD:password};
 const run=(mode,...a)=>execFileSync(mono,[native,mode,dir,...a],{encoding:'utf8',timeout:240000});
 const cs=async(command,...a)=>{const r=await invoke(mono,[cli,command,path.join(dir,'config.json'),...a],{encoding:'utf8',env,timeout:180000});if(command.startsWith("run-")&&r.stderr.trim())console.log(command+": "+r.stderr.trim());return r.stdout.trim()?parseJson(r.stdout):null;};
 try {
  run('setup');const operators=[];
  for(let i=0;i<2;i++)operators.push(parseJson((await invoke(mono,[cli,'create-native-operator',path.join(dir,'native-'+i+'.local.json')],{encoding:'utf8',env,timeout:120000})).stdout));
  const wallets=Object.values(rpc.getInitialAccounts()).map(a=>new Wallet(a.secretKey));
  for(let i=0;i<3;i++)writeFileSync(path.join(dir,'evm-'+i+'.local.json'),await encryptKeystoreJson({address:wallets[i].address,privateKey:wallets[i].privateKey},password,{scrypt:{N:1024,r:8,p:1}}),{mode:0o600});
  const signer=await provider.getSigner(0),mock=compile('test/MockUsdt.sol','MockUsdt'),artifact=compile('contracts/ProductionVault.sol','ProductionVault');
  const token=await new ContractFactory(mock.abi,mock.bytecode,signer).deploy();await token.waitForDeployment();
  const config={mode:'production',chainId:31337,rpcUrl:servers[0].url,rpcUrls:servers.map(s=>s.url),asset:(await token.getAddress()).toLowerCase(),bridge:null,
   nativeNetwork:execFileSync(mono,[cli,'network'],{encoding:'utf8'}).trim(),nativeToken:protocolToken,nativeAdministrators:operators.map(o=>o.address),nativeThreshold:2,nativeSigningPublicKeys:operators.map(o=>o.publicKey),nativeConfirmations:12,bscConfirmations:64,
   nativeSnapshot:path.join(dir,'chain.json'),nativeSnapshots:['chain.json','independent-chain.json'].map(f=>path.join(dir,f)),operators:wallets.slice(0,2).map(w=>w.address),threshold:2,reserveCap:'1000000000',dailyReleaseCap:'100000000',assetCodeHash:keccak256(await provider.getCode(await token.getAddress()))};
  const vault=await new ContractFactory(artifact.abi,artifact.bytecode,signer).deploy(config.asset,networkHash(config),'0x'+protocolToken,config.operators,2,config.reserveCap,config.dailyReleaseCap);await vault.waitForDeployment();
  config.bridge=(await vault.getAddress()).toLowerCase();config.vaultCodeHash=keccak256(await provider.getCode(config.bridge));writeFileSync(path.join(dir,'config.json'),JSON.stringify(config));run('configure');
  const deadline=String(Math.floor(Date.now()/1000)+300),auth=[];
  for(let i=0;i<2;i++)auth.push(await cs('sign-pause',path.join(dir,'evm-'+i+'.local.json'),'false',deadline));
  const authFile=path.join(dir,'activate.json');writeFileSync(authFile,JSON.stringify(auth));await cs('pause',authFile,path.join(dir,'evm-2.local.json'));
  const holder=readFileSync(path.join(dir,'holder.txt'),'utf8');
  const sourceCheckpoint=await rpc.request({method:'evm_snapshot',params:[]});
  const deposit=await cs('deposit','100000000',holder,path.join(dir,'evm-0.local.json'));
  const receipt=await provider.getTransactionReceipt(deposit.transactionHash);for(let i=0;i<63;i++)await rpc.request({method:'evm_mine',params:[]});
  const port=await freePort();nativePeer=await peer(port,path.join(dir,'mint.json'));
  const coordFile=path.join(dir,'coordinator.local.json'),operatorFiles=[0,1].map(i=>path.join(dir,'operator-'+i+'.automation.json'));
  const common={enabled:true,exchangeDirectory:path.join(dir,'public-exchange'),pollSeconds:5,processReleases:true};
  writeFileSync(coordFile,JSON.stringify({...common,stateDirectory:path.join(dir,'coordinator-state'),startBlock:receipt.blockNumber,nativePeers:['127.0.0.1:'+port],relayKeystore:path.join(dir,'evm-2.local.json')}));
  operatorFiles.forEach((file,i)=>writeFileSync(file,JSON.stringify({...common,stateDirectory:path.join(dir,'operator-state-'+i),nativeOperatorKeystore:path.join(dir,'native-'+i+'.local.json'),evmOperatorKeystore:path.join(dir,'evm-'+i+'.local.json')})));
  const coordinator=()=>cs('run-coordinator',coordFile,'--once'),operator=i=>cs('run-operator',operatorFiles[i],'--once');
  console.log('Automatic deposit discovery and independent operators');
  await coordinator();assert.equal(readdirSync(path.join(common.exchangeDirectory,'requests')).length,1);
  const requests=path.join(common.exchangeDirectory,'requests');
  const forged=parseJson(readFileSync(path.join(requests,readdirSync(requests)[0]),'utf8'));
  forged.body=await cs('propose-native','pause','-',path.join(dir,'manual-control-proposal.journal')); // valid unsigned administrative proposal, forbidden to the automatic signer
  const forgedId=createHash('sha256').update(canonical(forged)).digest('hex');
  writeFileSync(path.join(requests,forgedId+'.json'),JSON.stringify(forged));
  await operator(0);
  assert.equal(existsSync(path.join(common.exchangeDirectory,'signatures',operators[0].address,forgedId+'.json')),false,'automatic operator never signs pause disguised as mint');
  await coordinator();assert.equal(existsSync(path.join(dir,'mint.json')),false,'one signature never broadcasts a mint');
  await operator(1);await coordinator();
  for(let i=0;i<20&&!existsSync(path.join(dir,'mint.json'));i++)await new Promise(r=>setTimeout(r,100));
  assert.ok(existsSync(path.join(dir,'mint.json')),'fully signed mint reached the native P2P peer');
  console.log(run('confirm-mint').trim());await coordinator();await coordinator();
  const audit=await cs('audit');assert.equal(audit.native.supply,100000000);assert.equal(audit.backed,true);
  console.log('Confirmed deposit remains idempotent after worker restarts');
  run('burn',wallets[3].address.toLowerCase());
  const unexpected=parseJson(readFileSync(path.join(dir,'burn.json'),'utf8'));unexpected.privateKey='must-never-enter-the-public-queue';
  const unexpectedFile=path.join(dir,'unexpected-binding.json');writeFileSync(unexpectedFile,JSON.stringify(unexpected));
  await assert.rejects(cs('enqueue-burn',coordFile,unexpectedFile),/Unexpected binding field/);
  await cs('enqueue-burn',coordFile,path.join(dir,'burn.json'));await coordinator();await operator(0);await operator(1);
  const relayBefore=await provider.getTransactionCount(wallets[2].address);
  controls[0].gasSpike=true;await assert.rejects(coordinator(),/gas exceeds/);controls[0].gasSpike=false;
  assert.equal(await provider.getTransactionCount(wallets[2].address),relayBefore,'excessive fee never signs or broadcasts');
  controls[0].loseSendReply=true;
  await assert.rejects(coordinator(),/simulated lost response AFTER broadcast/);
  const burn=parseJson(readFileSync(path.join(dir,'burn.json'),'utf8'));
  const evidence=await cs('verify-burn',path.join(dir,'burn.json')),burnId=evidence.depositId;
  assert.ok((await vault.pendingReleases(burnId)).executeAfter>0n,'queue succeeded despite lost RPC response');
  for(let i=0;i<64;i++)await rpc.request({method:'evm_mine',params:[]});
  await coordinator();assert.equal(await provider.getTransactionCount(wallets[2].address),relayBefore+1,'restart does not allocate a second nonce');
  assert.equal(await token.balanceOf(wallets[3].address),0n);
  const beforeSecondBurn=config.nativeSnapshots.map(f=>readFileSync(f));
  // A separate legitimate burn is queued, then cancelled by one operator.
  // The daemon must never create fresh authorizations or requeue it after cancellation.
  run('burn',wallets[3].address.toLowerCase());
  await cs('enqueue-burn',coordFile,path.join(dir,'burn.json'));await coordinator();await operator(0);await operator(1);await coordinator();
  const cancelled=(await cs('verify-burn',path.join(dir,'burn.json'))).depositId;
  const afterSecondBurn=config.nativeSnapshots.map(f=>readFileSync(f));
  assert.ok((await vault.pendingReleases(cancelled)).executeAfter>0n);
  for(let i=0;i<64;i++)await rpc.request({method:'evm_mine',params:[]});
  await (await vault.cancelRelease(cancelled)).wait();await coordinator();
  assert.equal((await vault.pendingReleases(cancelled)).executeAfter,0n);
  assert.equal(await vault.releaseNonces(cancelled),1n);
  assert.equal(await provider.getTransactionCount(wallets[2].address),relayBefore+2);
  await rpc.request({method:'evm_increaseTime',params:[86401]});await rpc.request({method:'evm_mine',params:[]});
  // Hold a new mint awaiting signatures so the deposit scan anchor stays behind the payout.
  await cs('deposit','10000000',holder,path.join(dir,'evm-0.local.json'));
  for(let i=0;i<64;i++)await rpc.request({method:'evm_mine',params:[]});
  const beforePayout=await rpc.request({method:'evm_snapshot',params:[]});
  await coordinator();assert.equal(await token.balanceOf(wallets[3].address),500000000000000000n);
  for(let i=0;i<64;i++)await rpc.request({method:'evm_mine',params:[]});
  await coordinator();await coordinator();assert.equal(await vault.processedBurns(burnId),true);
  assert.equal(await provider.getTransactionCount(wallets[2].address),relayBefore+3,'exactly two queues + one execute; cancelled request never requeues');
  assert.equal((await cs('audit')).backed,true);
  assert.equal((await vault.pendingReleases(cancelled)).executeAfter,0n);
  // Losing the second native burn still leaves aggregate backing (0.5 supply / 0.6 reserve).
  // Historical burn tracking must nevertheless stop the relay before it sends anything else.
  config.nativeSnapshots.forEach((f,i)=>writeFileSync(f,beforeSecondBurn[i]));
  assert.equal((await cs('audit')).backed,true);
  await assert.rejects(coordinator(),/native burn disappeared/);
  config.nativeSnapshots.forEach((f,i)=>writeFileSync(f,afterSecondBurn[i]));
  controls[0].corruptHistoricalBlock=true;
  await assert.rejects(coordinator(),/providers disagree/i);controls[0].corruptHistoricalBlock=false;
  await rpc.request({method:'evm_revert',params:[beforePayout]});
  await assert.rejects(coordinator(),/relay anchor changed/);
  await rpc.request({method:'evm_revert',params:[sourceCheckpoint]});
  await assert.rejects(coordinator(),/anchor changed/);
  console.log('Automatic burn queue, durable relay restart, cancellation, disagreement and deep-reorg guards passed');
 } finally {if(nativePeer){nativePeer.kill('SIGTERM');await new Promise(resolve=>nativePeer.once('exit',resolve));}await provider.destroy();await Promise.all(servers.map(s=>s.close()));await rpc.disconnect();if(!process.env.POVIX_KEEP_AUTOMATION_FIXTURE)rmSync(dir,{recursive:true,force:true});else console.log('Retained test fixture '+dir);}
});
