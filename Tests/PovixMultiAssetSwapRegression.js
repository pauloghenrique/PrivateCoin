const assert=require('node:assert/strict');
const { screen, tick }=require('./PovixBridgeSwapRegression');
(async()=>{
 let s=await screen({multi:true});await s.connect();
 assert.equal(s.el('asset-picker').hidden,false);assert.equal(s.el('load').disabled,false);
 s.el('asset').value=s.sourceAsset;s.el('load').click();await tick();
 assert.match(s.el('selected').textContent,/LINK → pLINK/);assert.equal(s.el('submit').disabled,false);
 assert.equal(s.w.PovixBridge.parseAmount('1,000001',6),1000001n);assert.throws(()=>s.w.PovixBridge.parseAmount('1.0000001',6));
 assert.equal(s.w.PovixBridge.parseAmount('12',0),12n);assert.throws(()=>s.w.PovixBridge.parseAmount('1.1',0));
 s.el('amount').value='1,25';s.el('amount').dispatchEvent(new s.w.Event('input'));assert.equal(s.el('quote').textContent,'1.25 pLINK');
 // A new contract creates a paused vault before deposits can be requested.
 s=await screen({multi:true,unknown:true,selectedPaused:true});await s.connect();s.el('asset').value=s.sourceAsset;s.el('load').click();await tick();
 assert.deepEqual(s.calls.map(c=>c[0]),['create']);assert.equal(s.el('submit').disabled,true);assert.match(s.el('status').textContent,/pausada/);assert.match(s.el('selected').textContent,/USDT → pUSDT/);assert.equal(s.el('quote').textContent,'— pUSDT');
 // Reconnecting restores the configured primary asset and permits selecting another token.
 await s.connect();assert.equal(s.el('load').disabled,false);
 s=await screen({multi:true});await s.connect();s.el('asset').value='same-symbol-is-not-an-address';s.el('load').click();await tick();
 assert.equal(s.calls.length,0);assert.equal(s.el('submit').disabled,true);
 console.log('PASS: contract selection, six/zero decimal conversion, quote, paused automatic vault creation, reconnect and invalid contract rejection');
})().catch(e=>{console.error(e);process.exitCode=1;});
