'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const draft = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const context = { TextEncoder, TextDecoder, crypto: webcrypto,
    btoa: value => Buffer.from(value, 'binary').toString('base64'),
    atob: value => Buffer.from(value, 'base64').toString('binary'),
    __testWallet: { keys: new Map([[draft.changeAddress, {}]]) } };
vm.createContext(context);
// Inject only the unlocked key ownership set; verify the real browser verifier
// against the signing payload emitted by the C# consensus transaction.
const source = fs.readFileSync('Povix.Dex/Scripts/povix-token-wallet.js', 'utf8')
    .replace('let active = null;', 'let active = global.__testWallet;');
vm.runInContext(source, context);
(async () => {
    await context.PovixTokenWallet.verifyExchangeDraft(draft, draft, draft.networkId);
    for (const alter of [d => { d.participation.lockedSharesAtomic = '0'; },
        d => { d.participation.lockedSharesAtomic = String(BigInt(d.participation.lockedSharesAtomic) - 1n); },
        d => { d.outputs[0].amountAtomic = String(BigInt(d.outputs[0].amountAtomic) + 1n); },
        d => { d.consensusVersion = 21; }]) {
        const bad = JSON.parse(JSON.stringify(draft)); alter(bad);
        await assert.rejects(context.PovixTokenWallet.verifyExchangeDraft(bad, draft, draft.networkId));
    }
    console.log('5 initial-launch browser payload checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
