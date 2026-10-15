'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class Element {
    constructor() { this.value=''; this.files=[]; this.handlers={}; this.options=[]; }
    addEventListener(name,fn) { this.handlers[name]=fn; }
    replaceChildren(...items) { this.options=items; this.value=items[0]?.value || ''; }
    add(item) { this.options.push(item); }
    showModal() { this.open=true; }
    close() { this.open=false; }
    async fire(name) { await this.handlers[name]({preventDefault(){}}); }
}
(async()=>{
    const nodes=new Map(), get=id=>{if(!nodes.has(id))nodes.set(id,new Element());return nodes.get(id);};
    const events={}, windowEvents={}, link=new Element(), label=new Element();
    let unlocked=false, restores=0, changes=0, password;
    const wallet={isUnlocked:()=>unlocked, info:()=>({name:'Minha carteira'}),
        restoreSession:async()=>{restores++;unlocked=true;},lock:()=>{unlocked=false;},
        readFile:async(file,pwd)=>{password=pwd;return [{id:'0',name:'Minha carteira',addressCount:1},{id:'1',name:'Vazia',addressCount:0}];},
        selectWallet:async id=>{assert.equal(id,'0');unlocked=true;}};
    const context={window:{PovixTokenWallet:wallet,addEventListener:(name,fn)=>{windowEvents[name]=fn;}},
        document:{getElementById:get,querySelectorAll:selector=>selector==='[data-wallet-open]'?[link]:[label],addEventListener:(name,fn)=>{events[name]=fn;},dispatchEvent:()=>{changes++;}},
        Uint8Array, Event, Option:class{constructor(text,value){this.text=text;this.value=value;}}};
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-wallet-menu.js','utf8'),context);
    await events.DOMContentLoaded();assert.equal(restores,1);assert.equal(changes,1);assert.equal(get('wallet-form').hidden,true);assert.equal(label.textContent,'Minha carteira');
    await link.fire('click');assert(get('wallet-menu').open);
    await get('wallet-lock').fire('click');assert(!unlocked);assert.equal(get('wallet-form').hidden,false);
    const selectFile=async bytes=>{get('wallet-file').files=[{slice:()=>({arrayBuffer:async()=>Uint8Array.from(bytes).buffer})}];await get('wallet-file').fire('change');};
    await selectFile([1,0,0,0]);assert.equal(get('wallet-password-field').hidden,true);assert.equal(get('wallet-password').required,false);
    await get('wallet-form').fire('submit');assert.equal(password,'');assert.equal(unlocked,false);assert.equal(get('wallet-choice').options[2].disabled,true);
    get('wallet-choice').value='0';await get('wallet-choice').fire('change');await get('wallet-form').fire('submit');
    assert(unlocked);assert.equal(get('wallet-menu').open,false);
    context.window.PovixWalletMenu.setBusy(true);await get('wallet-lock').fire('click');assert(unlocked);
    context.window.PovixWalletMenu.setBusy(false);await get('wallet-lock').fire('click');assert(!unlocked);
    await selectFile([123,34,102,111]);assert.equal(get('wallet-password-field').hidden,false);assert.equal(get('wallet-password').required,true);
    await windowEvents.pageshow({persisted:true}); // Async restoration completes after the event handler returns.
    await new Promise(resolve=>setImmediate(resolve));assert.equal(restores,2);assert(unlocked);
    const layout=fs.readFileSync('Povix.Dex/Views/Shared/_Layout.cshtml','utf8');
    assert.equal((layout.match(/@Html.Partial\("_Wallet"\)/g)||[]).length,1);
    assert(layout.indexOf('~/bundles/povix-wallet')<layout.indexOf('@RenderSection("scripts"'));
    for(const page of ['Tokens/Create','TokenTransfers/Index','TokenProfiles/Index','Liquidity/Index','Liquidity/_PoolTrading'])assert(!fs.readFileSync('Povix.Dex/Views/'+page+'.cshtml','utf8').includes('@Html.Partial("_Wallet")'));
    console.log('PASS: shared menu restore, passwordless Desktop UI, encrypted-backup password, explicit selection, busy guard, disconnect, browser Back and single layout instance.');
})().catch(error=>{console.error(error);process.exitCode=1;});
