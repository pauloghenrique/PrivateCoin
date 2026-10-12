'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
if (!global.crypto) global.crypto = require('node:crypto').webcrypto;
const storage = new Map();
global.localStorage = { getItem: key => storage.get(key) || null, setItem: (key, value) => storage.set(key, value) };
const directory = process.argv[2];
const fixture = JSON.parse(fs.readFileSync(path.join(directory, 'profile-fixture.json'), 'utf8'));
vm.runInThisContext(fs.readFileSync(path.join(__dirname, '../Povix.Dex/Scripts/povix-token-wallet.js'), 'utf8'));
(async () => {
    const wallet = global.PovixTokenWallet;
    const file = { size: Buffer.byteLength(JSON.stringify(fixture.wallet)), arrayBuffer: async () => new TextEncoder().encode(JSON.stringify(fixture.wallet)).buffer };
    await wallet.importFile(file, 'dex-test-password-only');
    for (const change of [{ name: 'Tampered' }, { renounced: true }, { tokenId: '0'.repeat(64) },
        { revision: 3 }, { website: 'https://attacker.example' }, { description: 'changed' },
        { creatorAddress: '0'.repeat(64) }, { networkId: 'other-network' }, { authorizationPayload: Buffer.from('fake').toString('base64') }])
        await assert.rejects(wallet.authorizeProfile({ ...fixture.draft, ...change }, fixture.expected, fixture.networkId));
    const signature = await wallet.authorizeProfile(fixture.draft, fixture.expected, fixture.networkId);
    assert.equal(Buffer.from(signature, 'base64').length, 256);
    wallet.lock();
    await assert.rejects(wallet.authorizeProfile(fixture.draft, fixture.expected, fixture.networkId));
    fs.writeFileSync(path.join(directory, 'profile-browser-result.json'), JSON.stringify({ signature }));
    console.log('PASS profile payload tampering, wallet lock and local signature');
})().catch(error => { console.error(error); process.exitCode = 1; });
