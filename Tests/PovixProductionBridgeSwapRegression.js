const assert=require('node:assert/strict');
const {screen}=require('./PovixBridgeSwapRegression');
(async()=>{
 let s=await screen({production:true,chain:97});await s.connect();assert.equal(s.el('submit').disabled,false);assert.match(s.el('account').textContent,/Mainnet/);
 await s.submit();assert.deepEqual(s.waits,[64]);assert.match(s.el('reference').querySelector('a').href,/^https:\/\/bscscan.com\/tx\//);assert.match(s.el('status').textContent,/Aguardando validação/);
 s=await screen({production:true,chain:56,wrongVersion:true});await s.connect();assert.equal(s.el('submit').disabled,true);assert.equal(s.calls.length,0);assert.match(s.el('status').textContent,/produção/);
 s=await screen({production:true,enabled:false});assert.equal(s.el('connect').disabled,true);
 console.log('PASS: mainnet switch, 64 confirmations, correct explorer, production-version rejection and disabled-by-default deployment');
})().catch(e=>{console.error(e);process.exitCode=1;});
