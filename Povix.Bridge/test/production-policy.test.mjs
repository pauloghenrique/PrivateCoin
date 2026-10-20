import test from 'node:test';
import assert from 'node:assert/strict';
import ganache from 'ganache';
import { BrowserProvider, Wallet, ContractFactory, Contract, keccak256, verifyTypedData } from 'ethers';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { compile, root } from '../src/compile.mjs';
import { protocolToken, validateConfig, domain, signingTypes } from '../src/bridge.mjs';
import { verifyProductionDeposit, assetTypes, assetDomain, reviewOnNativeNodes } from '../src/production.mjs';
const config={mode:'production',chainId:31337,rpcUrl:'http://127.0.0.1:101',rpcUrls:['http://127.0.0.1:101','http://127.0.0.1:102'],asset:'0x'+'1'.repeat(40),assetCodeHash:'0x'+'2'.repeat(64),bridge:'0x'+'3'.repeat(40),vaultCodeHash:'0x'+'4'.repeat(64),nativeNetwork:'povix-mainnet-rules-v1-test',nativeToken:protocolToken,nativeAdministrators:['a','b','c','d','e'].map(x=>x.repeat(64)),nativeThreshold:3,nativeSnapshot:'/node1',nativeSnapshots:['/node1','/node2'],nativeConfirmations:120,bscConfirmations:64,operators:[1,2,3,4,5].map(x=>'0x'+x.toString().repeat(40)),threshold:3,reserveCap:'1000000000',dailyReleaseCap:'1000000'};
test('production fails closed for provider errors, disagreement, weak thresholds and wrong pins',async()=>{
 let destroyed=0;const deps={provider:url=>({url,destroy:async()=>destroyed++}),connect:async()=>({contract:{}}),verifyDeposit:async p=>({blockHash:p.url,depositId:'a'})};
 await assert.rejects(verifyProductionDeposit(config,{},deps),/disagree/);assert.equal(destroyed,2);
 destroyed=0;await assert.rejects(verifyProductionDeposit(config,{}, {...deps,verifyDeposit:async()=>{throw Error('RPC unavailable');}}),/unavailable/);assert.equal(destroyed,2);
 assert.throws(()=>reviewOnNativeNodes(config,{},'review',c=>c.nativeSnapshot),/disagree/);
 for(const patch of [{chainId:56,mode:'test'},{chainId:56,rpcUrls:['https://one.example/a','https://one.example/b'],rpcUrl:'https://one.example/a'},{threshold:2},{nativeThreshold:2},{chainId:56,rpcUrl:'https://one.example',rpcUrls:['https://one.example','https://two.example'],bscConfirmations:15},{chainId:56,rpcUrl:'https://one.example',rpcUrls:['https://one.example','https://two.example'],nativeConfirmations:12},{vaultCodeHash:null},{assetCodeHash:null}])assert.throws(()=>validateConfig({...config,...patch}));
});
test('production asset onboarding needs quorum, code hash and per-asset limits',{timeout:120000},async()=>{
 const rpc=ganache.provider({chain:{chainId:31337},logging:{quiet:true},wallet:{totalAccounts:6}}),provider=new BrowserProvider(rpc,undefined,{cacheTimeout:-1});provider.pollingInterval=10;
 try{
  const wallets=Object.values(rpc.getInitialAccounts()).map(a=>new Wallet(a.secretKey)),signer=await provider.getSigner(0);
  const mock=compile('test/MockAsset.sol','MockAsset'),artifact=compile('contracts/ProductionVault.sol','ProductionVault'),factoryArtifact=compile('contracts/ProductionVaultFactory.sol','ProductionVaultFactory');
  const deploy=async p=>{const t=await new ContractFactory(mock.abi,mock.bytecode,signer).deploy(p);await t.waitForDeployment();return t;};
  const primary=await deploy(18),asset=await deploy(6);
  const vault=await new ContractFactory(artifact.abi,artifact.bytecode,signer).deploy(await primary.getAddress(),'0x'+'a'.repeat(64),'0x'+protocolToken,wallets.slice(0,5).map(w=>w.address),3,1000000000,1000000);await vault.waitForDeployment();
  const factory=await new ContractFactory(factoryArtifact.abi,factoryArtifact.bytecode,signer).deploy(await vault.getAddress());await factory.waitForDeployment();
  const c={...config,factory:await factory.getAddress()},block=await provider.getBlock('latest');
  const value={asset:await asset.getAddress(),codeHash:keccak256(await provider.getCode(await asset.getAddress())),cap:1000000,dailyCap:250000,deadline:Number(block.timestamp)+300};
  const signatures=(await Promise.all(wallets.slice(0,3).map(async w=>{const signature=await w.signTypedData(assetDomain(c),assetTypes,value);return {signature,address:verifyTypedData(assetDomain(c),assetTypes,value,signature)};}))).sort((a,b)=>BigInt(a.address)<BigInt(b.address)?-1:1).map(s=>s.signature);
  const create=(v,s)=>factory.createVault(v.asset,v.codeHash,v.cap,v.dailyCap,v.deadline,s);
  await assert.rejects(create(value,[]));await assert.rejects(create(value,[signatures[0],signatures[0],signatures[2]]));
  await assert.rejects(create({...value,cap:2000000},signatures));await assert.rejects(create({...value,codeHash:'0x'+'f'.repeat(64)},signatures));
  await(await create(value,signatures)).wait();const created=new Contract(await factory.vaults(value.asset),artifact.abi,provider);
  assert.equal(await created.reserveCap(),1000000n);assert.equal(await created.dailyReleaseCap(),250000n);assert.equal(await created.paused(),true);assert.equal(await created.nativeDecimals(),6n);
  await assert.rejects(create(value,signatures));
  const bridgeConfig={mode:'production',chainId:31337,bridge:await vault.getAddress()};
  const unpause={paused:false,nonce:'0',deadline:Number((await provider.getBlock('latest')).timestamp)+300};
  const pauseSignatures=(await Promise.all(wallets.slice(0,3).map(async w=>({address:w.address,signature:await w.signTypedData(domain(bridgeConfig),signingTypes(bridgeConfig,'Pause'),unpause)})))).sort((a,b)=>BigInt(a.address)<BigInt(b.address)?-1:1).map(v=>v.signature);
  await(await vault.setPaused(false,unpause.deadline,pauseSignatures)).wait();
  await(await primary.approve(await vault.getAddress(),1000000000000000000n)).wait();
  await(await vault.deposit(1000000000000000000n,'0x'+'1'.repeat(64))).wait();
  await assert.rejects(vault.release.staticCall('0x'+'2'.repeat(64),wallets[5].address,1000001,unpause.deadline,[]),/invalid release/);
 }finally{await provider.destroy();await rpc.disconnect();}
});

test('oversized native redemption is rejected before burning',()=>{
 const output=execFileSync(process.env.POVIX_MONO||'mono',[path.join(root,'build/native/ProductionBurnLimitRegression.exe')],{encoding:'utf8',timeout:120000});
 assert.match(output,/PASS production native preparation/);
});
