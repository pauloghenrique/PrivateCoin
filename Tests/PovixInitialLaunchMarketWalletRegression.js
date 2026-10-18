'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), vm = require('node:vm');
const { webcrypto: crypto } = require('node:crypto');
const source = fs.readFileSync('Povix.Dex/Scripts/povix-token-wallet.js','utf8').replace('let active = null;', 'let active = global.__testWallet;');
(async()=>{
 let checks=0;
 for(const suffix of ['.json','.final.json','.service.json']) {
  const draft=JSON.parse(fs.readFileSync(process.argv[2]+suffix,'utf8'));
  const context={crypto,TextEncoder,TextDecoder,atob,btoa,__testWallet:{keys:new Map([[draft.outputs[2].address,{publicXml:draft.initialLaunchIdentityKey}]])}};
  vm.runInNewContext(source,context);
  const verify=d=>context.PovixTokenWallet.verifyExchangeDraft(d,draft,draft.networkId);
  await verify(draft); checks++;
  for(const change of [d=>d.outputs[2].initialLaunchLocked=false,d=>d.outputs[2].amountAtomic='700000000',d=>d.initialLaunchIdentityKey+='x',d=>d.initialLaunchPurchase=false,d=>d.inputSide=0,d=>d.transactionKind=8,d=>d.outputs[0].amountAtomic='1',d=>d.amountAtomic='2000000',d=>d.providerAddress='f'.repeat(64)]) {
   const bad=structuredClone(draft); change(bad); await assert.rejects(verify(bad)); checks++;
  }
 }
 console.log(checks+' native market browser checks passed (first and final purchase, locks, identity, rate, reserves and kind)');
})().catch(error=>{console.error(error);process.exitCode=1;});
