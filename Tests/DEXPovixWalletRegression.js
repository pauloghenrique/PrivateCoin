'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
if (!globalThis.crypto) globalThis.crypto = require('node:crypto').webcrypto;
const walletApi = require('../DEXPovix/Scripts/dexpovix-wallet.js');
const directory = process.argv[3] || 'work/dexpovix';
const password = 'test-only-backup-password';

async function run() {
    fs.mkdirSync(directory, { recursive: true });
    if (process.argv[2] === 'sign') {
        const wallet = await walletApi.decrypt(JSON.parse(fs.readFileSync(path.join(directory, 'browser-backup.json'))), password);
        const draft = JSON.parse(fs.readFileSync(path.join(directory, 'browser-draft.json')));
        const signatures = await walletApi.sign(wallet, draft.Payload, draft.InputAddresses);
        fs.writeFileSync(path.join(directory, 'browser-signatures.json'), JSON.stringify(signatures));
        console.log('PASS Web Crypto signs the exact Core payload');
        return;
    }
    const keys = await Promise.all([walletApi.newKey(), walletApi.newKey(), walletApi.newKey()]);
    const wallet = { keys: keys, receiveAddress: keys[0].address };
    const backup = await walletApi.encrypt(wallet, password);
    const restored = await walletApi.decrypt(backup, password);
    assert.deepEqual(restored.keys.map(key => key.address), keys.map(key => key.address));
    assert.equal(restored.receiveAddress, wallet.receiveAddress);
    console.log('PASS encrypted backup restores all funding, token and change keys');
    await assert.rejects(() => walletApi.decrypt(backup, 'wrong-backup-password'), /Senha incorreta/);
    const corrupted = { ...backup, data: (backup.data[0] === 'A' ? 'B' : 'A') + backup.data.slice(1) };
    await assert.rejects(() => walletApi.decrypt(corrupted, password), /Senha incorreta/);
    await assert.rejects(() => walletApi.encrypt(wallet, 'short'), /12 caracteres/);
    await assert.rejects(() => walletApi.sign(wallet, 'payload', ['missing-key']), /não possui/);
    console.log('PASS wrong passwords, ciphertext tampering, short passwords and unknown signing keys fail');
    const rawBackup = JSON.stringify(backup);
    assert(!rawBackup.includes(keys[0].jwk.d));
    assert(!rawBackup.includes(keys[0].publicKey));
    // Exercise the maximum supported wallet size without large base64 argument lists.
    const many = await walletApi.encrypt({ keys: Array(100).fill(keys[0]), receiveAddress: keys[0].address }, password);
    assert(many.data.length > 100000);
    console.log('PASS encrypted exports contain no plaintext keys and support 100 keys');
    fs.writeFileSync(path.join(directory, 'browser-backup.json'), rawBackup);
    fs.writeFileSync(path.join(directory, 'browser-public.json'), JSON.stringify({
        PublicKey: keys[0].publicKey, Address: keys[0].address, Recipient: keys[1].address, ChangeAddress: keys[2].address
    }));
}
run().catch(error => { console.error(error); process.exitCode = 1; });
