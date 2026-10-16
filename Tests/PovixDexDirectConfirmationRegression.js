'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
(async () => {
    for (const [file, button, verify] of [
        ['povix-token-create.js', 'sign-token', 'verifyDraft'],
        ['povix-token-transfer.js', 'sign-transfer', 'verifyTransferDraft'],
        ['povix-liquidity.js', 'sign-exchange', 'verifyExchangeDraft']
    ]) {
        const source = fs.readFileSync('Povix.Dex/Scripts/' + file, 'utf8');
        const start = source.indexOf("    byId('" + button + "').addEventListener('click', async () => {");
        const end = source.indexOf('\n    });', start) + '\n    });'.length;
        const nodes = new Map(), node = id => {
            if (!nodes.has(id)) nodes.set(id, { addEventListener: (_, fn) => { node(id).click = fn; }, textContent: '' });
            return nodes.get(id);
        };
        let signs = 0, verifies = 0, unlocked = true, redirected = 0;
        const submissions = [];
        const context = {
            byId: node, busy: false, network: { CanCreate: true }, draft: { draftId: 'draft', operation: 'swap', inputs: [{ address: 'existing' }], userInputCount: 1 },
            expected: {}, changeAddress: 'existing', signedSignatures: null, signed: null, signatures: null,
            app: { dataset: { networkId: 'native', submitUrl: '/submit', submitSwapUrl: '/swap' } }, integrated: false,
            wallet: { isUnlocked: () => unlocked, [verify]: async () => { verifies++; }, sign: async () => { signs++; return ['signature']; }, authorizeTransfer: async () => 'creator-signature', backup: () => { throw Error('backup must never be called'); } },
            update() {}, updateControls() {}, message() {}, resetDraft() {}, reset() {}, refresh() {}, receipt: () => '/receipt',
            location: { origin: 'https://dex.povix.org', assign: () => { redirected++; } }, URL,
            post: async (_, payload) => { submissions.push(payload); if (submissions.length === 1) throw Error('temporary network failure'); return { receiptUrl: '/receipt', transactionId: 'transaction' }; }
        };
        vm.runInNewContext(source.slice(start, end), context);
        unlocked = false; await node(button).click(); assert.equal(submissions.length, 0);
        unlocked = true; context.network.CanCreate = false; await node(button).click(); assert.equal(submissions.length, 0);
        context.network.CanCreate = true;
        await node(button).click(); assert.equal(submissions.length, 1, 'first click submits without file or checkbox');
        await node(button).click(); assert.equal(submissions.length, 2);
        assert.deepEqual(submissions[1], submissions[0], 'retry uses the same prepared transaction and signatures');
        assert.equal(signs, 1); assert(verifies > 0); assert.equal(redirected, 1); assert.equal(context.busy, false);
        for (const id of ['download-backup', 'backup-confirmed']) assert.equal(nodes.has(id), false);
    }
    for (const file of ['Tokens/Create', 'TokenTransfers/Index', 'Liquidity/Index', 'Liquidity/_PoolTrading']) {
        const view = fs.readFileSync('Povix.Dex/Views/' + file + '.cshtml', 'utf8');
        assert(!view.includes('id="download-backup"')); assert(!view.includes('id="backup-confirmed"'));
    }
    console.log('PASS: direct confirmation, no backup or checkbox, locked wallet/offline guards, verification, signatures and idempotent retries in all transaction flows.');
})().catch(error => { console.error(error); process.exitCode = 1; });
