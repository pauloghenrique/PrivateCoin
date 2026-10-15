'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { webcrypto: crypto } = require('node:crypto');
const source = fs.readFileSync(require('node:path').join(__dirname, '../Povix.Dex/Scripts/povix-token-wallet.js'), 'utf8');
const local = new Map(), session = new Map();
const storage = map => ({ getItem: k => map.get(k) || null, setItem: (k,v) => map.set(k,v), removeItem: k => map.delete(k) });
const key = 'povix.dex.wallet-session.v1';
let downloaded, prompted = 0;
const file = bytes => ({ size: bytes.length, arrayBuffer: async () => Uint8Array.from(bytes).buffer });
async function encrypted(wallets, password) {
    const salt = crypto.getRandomValues(new Uint8Array(16)), iv = crypto.getRandomValues(new Uint8Array(16));
    const material = await crypto.subtle.importKey('raw', new TextEncoder().encode(password), 'PBKDF2', false, ['deriveBits']);
    const bits = new Uint8Array(await crypto.subtle.deriveBits({ name:'PBKDF2', salt, iterations:210000, hash:'SHA-256' }, material, 512));
    const aes = await crypto.subtle.importKey('raw', bits.slice(0,32), 'AES-CBC', false, ['encrypt']);
    const hmac = await crypto.subtle.importKey('raw', bits.slice(32), {name:'HMAC',hash:'SHA-256'}, false, ['sign']);
    const data = Buffer.from(await crypto.subtle.encrypt({name:'AES-CBC',iv}, aes, new TextEncoder().encode(JSON.stringify({Wallets:wallets}))));
    return {format:'povix-dex-wallet-v1',iterations:210000,salt:Buffer.from(salt).toString('base64'),iv:Buffer.from(iv).toString('base64'),data:data.toString('base64'),hmac:Buffer.from(await crypto.subtle.sign('HMAC',hmac,Buffer.concat([salt,iv,data]))).toString('base64')};
}
function page(wallets, tab = session) {
    const listeners = {};
    const context = vm.createContext({ crypto, TextEncoder, TextDecoder, Uint8Array, AbortController, Map, Blob,
        atob, btoa, setTimeout: () => 1, clearTimeout() {}, localStorage:storage(local), sessionStorage:storage(tab),
        addEventListener: (name, fn) => { listeners[name] = fn; },
        prompt: () => { prompted++; return 'export-backup-password'; },
        fetch: async (url, options) => {
            assert.equal(url,'http://127.0.0.1:4781/import');
            const body = JSON.parse(options.body); assert(body.password.length >= 10);
            return {ok:true,json:async()=>({wallet:await encrypted(wallets,body.password)})};
        },
        URL: {createObjectURL: blob => {downloaded=blob;return 'blob:test';},revokeObjectURL(){}},
        document:{createElement:()=>({click(){}})} });
    vm.runInContext(source,context);
    return {wallet:context.PovixTokenWallet,listeners};
}
(async () => {
    const pair = await crypto.subtle.generateKey({name:'RSASSA-PKCS1-v1_5',hash:'SHA-256',modulusLength:2048,publicExponent:new Uint8Array([1,0,1])},true,['sign','verify']);
    const jwk = await crypto.subtle.exportKey('jwk',pair.privateKey);
    const fields={Modulus:'n',Exponent:'e',D:'d',P:'p',Q:'q',DP:'dp',DQ:'dq',InverseQ:'qi'};
    const xml='<RSAKeyValue>'+Object.entries(fields).map(([name,k])=>'<'+name+'>'+Buffer.from(jwk[k],'base64url').toString('base64')+'</'+name+'>').join('')+'</RSAKeyValue>';
    const wallets=[{Name:'Primeira',PrivateKeys:[xml]},{Name:'Selecionada',PrivateKeys:[xml]}];
    const desktop=Buffer.concat([Buffer.from([1,0,0,0]),Buffer.from('0Iyd3wEV0RGMegDAT8KX6w==','base64')]);
    let current=page(wallets);
    const choices=await current.wallet.readFile(file(desktop));
    assert.equal(choices.length,2); assert.equal(prompted,0);
    await current.wallet.selectWallet('1');
    assert.equal(current.wallet.info().name,'Selecionada');
    const address=current.wallet.addresses()[0];
    assert(!local.values().next().value.includes(xml));
    current.listeners.pagehide(); assert.equal(current.wallet.isUnlocked(),false);
    current=page(wallets); assert.equal(await current.wallet.restoreSession(),true);
    assert.equal(current.wallet.info().name,'Selecionada'); assert.equal(current.wallet.addresses()[0],address);
    const payload=Buffer.from('session-signature');
    const signatures=await current.wallet.sign({signingPayload:payload.toString('base64'),inputAddresses:[address]});
    assert(await crypto.subtle.verify('RSASSA-PKCS1-v1_5',pair.publicKey,Buffer.from(signatures[0],'base64'),payload));
    const added=await current.wallet.createAddress();
    current=page(wallets); assert(await current.wallet.restoreSession()); assert(current.wallet.addresses().includes(added));
    await current.wallet.backup(); assert.equal(prompted,1);
    const backup=Buffer.from(await downloaded.arrayBuffer());
    const isolated=page(wallets,new Map()); assert.equal(await isolated.wallet.restoreSession(),false);
    await isolated.wallet.readFile(file(backup),'export-backup-password');
    await isolated.wallet.selectWallet('1');
    assert(isolated.wallet.addresses().includes(added));
    current.wallet.lock(); assert.equal(session.has(key),false);
    current=page(wallets); assert.equal(await current.wallet.restoreSession(),false);
    session.set(key,'{broken'); assert.equal(await current.wallet.restoreSession(),false); assert(!session.has(key));
    await assert.rejects(current.wallet.readFile(file(backup),'wrong-password'),/Senha incorreta/);
    console.log('PASS: passwordless Desktop connection, selected wallet across navigation, signing, generated keys, encrypted export, tab isolation, disconnect and corrupt sessions.');
})().catch(error=>{console.error(error);process.exitCode=1;});
