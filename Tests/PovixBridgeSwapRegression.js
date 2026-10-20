const assert = require('node:assert/strict');
const fs = require('node:fs');
const { JSDOM } = require('../work/liquidity-tools/node_modules/jsdom');
const real = require('../Povix.Bridge/node_modules/ethers');
const source = fs.readFileSync('Povix.Dex/Scripts/povix-bridge.js', 'utf8');
const tick = () => new Promise(resolve => setTimeout(resolve, 10));
async function screen({enabled=true, chain=97, paused=false, mismatch=false, reject=false, allowance=0n, multi=false, unknown=false, selectedPaused=false}={}) {
 const dom = new JSDOM(`<main id="bridge-app" data-config-url="/Bridge/Configuration"><div id="bridge-asset-picker" hidden><input id="bridge-asset"><button id="bridge-load" disabled></button></div><p id="bridge-selected"></p><form id="bridge-form"><button id="bridge-connect" disabled></button><input id="bridge-amount" disabled><input id="bridge-recipient" disabled><button id="bridge-submit" disabled></button></form><p id="bridge-account"></p><p id="bridge-status"></p><p id="bridge-quote"></p><p id="bridge-reference"></p></main>`, {runScripts:'outside-only',url:'https://dex.example'});
 const w=dom.window, address='0x'+'1'.repeat(40), vaultAddress='0x'+'2'.repeat(40), asset='0x'+'3'.repeat(40), network='povix-test', token='a'.repeat(64), destination='b'.repeat(64);
 const calls=[]; let listener={}, created=false;
 const sourceAsset='0x'+'5'.repeat(40), factoryAddress='0x'+'6'.repeat(40), selectedVault='0x'+'4'.repeat(40);
 const selectedId='b9b0'+real.sha256(real.toUtf8Bytes('povix-bnb-token-v1|97|'+sourceAsset)).slice(6);
 w.ethereum={request:async ({method})=>method==='eth_chainId'?'0x'+chain.toString(16):method==='wallet_switchEthereumChain'?(chain=97):[address],on:(name,fn)=>listener[name]=fn};
 const iface=new real.Interface(['event Deposited(bytes32 indexed depositId,address indexed sender,bytes32 indexed nativeRecipient,uint256 nativeAmount,uint256 assetAmount)']);
 const encoded=iface.encodeEventLog(iface.getEvent('Deposited'),['0x'+'c'.repeat(64),address,'0x'+destination,125000000n,1250000000000000000n]);
 const vault={asset:async()=>mismatch?vaultAddress:asset,nativeNetwork:async()=>real.sha256(real.toUtf8Bytes(network)),nativeToken:async()=>'0x'+token,
 scale:async()=>10000000000n,paused:async()=>paused,accountedReserve:async()=>0n,reserveCap:async()=>10000000000n,interface:iface,
 deposit:async (...args)=>{calls.push(['deposit',...args]);return {hash:'0x'+'d'.repeat(64),wait:async()=>({status:1,hash:'0x'+'d'.repeat(64),logs:[{address:vaultAddress,...encoded}]})};}};
 const t={decimals:async()=>18,balanceOf:async()=>10n**20n,allowance:async()=>allowance,approve:async (...args)=>{calls.push(['approve',...args]);if(reject)throw {code:4001};return {wait:async()=>({status:1})};}};
 const dynamic={...vault,asset:async()=>sourceAsset,nativeToken:async()=>'0x'+selectedId,scale:async()=>1n,paused:async()=>selectedPaused};
 const factory={nativeNetwork:vault.nativeNetwork,vaults:async a=>a.toLowerCase()===asset?vaultAddress:unknown&&!created?'0x'+'0'.repeat(40):selectedVault,representation:async()=>'0x'+selectedId,createVault:async a=>{calls.push(['create',a]);created=true;return {hash:'0x'+'e'.repeat(64),wait:async()=>({status:1})};}};
 w.ethers={...real,BrowserProvider:class{async getSigner(){return {getAddress:async()=>address};}async getCode(){return '0x1234';}},Contract:function(addr){return addr===factoryAddress?factory:addr===selectedVault?dynamic:addr===vaultAddress?vault:addr===sourceAsset?{...t,decimals:async()=>6,symbol:async()=>'LINK'}:t;}};
 w.fetch=async()=>({ok:true,json:async()=>({enabled,chainId:97,confirmations:15,asset,vault:vaultAddress,network,token,factory:multi?factoryAddress:null})});
 w.eval(source); await tick();
 const el=id=>w.document.getElementById('bridge-'+id);
 async function connect(){el('connect').click();await tick();}
 async function submit(){el('amount').value='1,25';el('recipient').value=destination;el('form').dispatchEvent(new w.Event('submit',{cancelable:true}));await tick();}
 return {w,el,calls,listener,connect,submit,sourceAsset};
}
module.exports = { screen, tick };
if (require.main === module) (async()=>{
 let s=await screen({enabled:false});assert.equal(s.el('connect').disabled,true);assert.match(s.el('status').textContent,/preparação/);
 const api=s.w.PovixBridge; assert.equal(api.parseAmount('1,00000001'),100000001n);
 for(const v of ['0','-1','1e3','1.000000001','1,000.00','92233720369']) assert.throws(()=>api.parseAmount(v));
 assert.throws(()=>api.recipient('0'.repeat(64)));assert.throws(()=>api.recipient('0x'+'1'.repeat(40)));
 s=await screen();await s.connect();assert.equal(s.el('submit').disabled,false);await s.submit();
 assert.equal(s.calls.length,2);assert.equal(s.calls[0][0],'approve');assert.equal(s.calls[0][2],1250000000000000000n);
 assert.equal(s.calls[1][0],'deposit');assert.match(s.el('status').textContent,/Aguardando validação/);assert.equal(s.el('submit').disabled,true);
 await s.submit();assert.equal(s.calls.length,2);assert.match(s.el('reference').textContent,/Identificador/);
 s=await screen({allowance:1n});await s.connect();await s.submit();assert.equal(s.calls.length,3);assert.equal(s.calls[0][2],0n);
 s=await screen({paused:true});await s.connect();assert.equal(s.el('submit').disabled,true);assert.match(s.el('status').textContent,/pausada/);
 s=await screen({mismatch:true});await s.connect();assert.equal(s.el('submit').disabled,true);
 s=await screen({reject:true});await s.connect();await s.submit();assert.equal(s.calls.length,1);assert.match(s.el('status').textContent,/cancelada/);assert.equal(s.el('submit').disabled,false);
 s=await screen();await s.connect();s.listener.accountsChanged([]);assert.equal(s.el('submit').disabled,true);await s.submit();assert.equal(s.calls.length,0);
 s=await screen({chain:56});await s.connect();assert.equal(s.el('submit').disabled,false);
 console.log('PASS: validation, disabled setup, exact approval, allowance reset, deposit receipt, pending issuance, duplicate prevention, pause, contract mismatch, cancellation, account change and testnet switch');
})().catch(e=>{console.error(e);process.exitCode=1;});
