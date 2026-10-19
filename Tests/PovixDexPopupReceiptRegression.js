'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8');
const partial = fs.readFileSync('Povix.Dex/Views/Liquidity/_TransactionReview.cshtml', 'utf8');
assert(partial.includes('id="exchange-status"'));
assert(!fs.readFileSync('Povix.Dex/Views/Liquidity/_PoolTrading.cshtml', 'utf8').includes('id="exchange-status"'));
const nodes = new Map(), byId = id => {
    if (!nodes.has(id)) nodes.set(id, { textContent: '', hidden: false, open: false,
        setAttribute(name, value) { this[name] = value; }, removeAttribute(name) { delete this[name]; },
        replaceChildren() {}, showModal() { this.open = true; }, close() { this.open = false; } });
    return nodes.get(id);
};
let status = 'pending', requests = 0;
const storage = new Map();
const context = { byId, URL, managing: false, integrated: true, preferredPool: 'pool',
    draft: {}, expected: {}, signatures: [], update() {}, atomicText: value => value,
    location: { origin: 'https://dex.example' }, app: { dataset: { receiptUrl: '/receipt/__id__', statusUrl: '/status/__id__' } },
    document: { hidden: false, dispatchEvent() {} }, Event: class {},
    sessionStorage: { setItem: (k,v) => storage.set(k,v), removeItem: k => storage.delete(k) },
    fetch: async () => { requests++; return { ok: true, json: async () => ({ status, poolId: 'pool', blockHeight: status === 'confirmed' ? 42 : null, confirmations: 2, blockHash: 'hash', receive: { amountAtomic: '6', decimals: 8, symbol: 'POVIX' }, feeAtomic: '1' }) }; }
};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('    function reset('), source.indexOf('    function options(')) +
    source.slice(source.indexOf('    function receipt('), source.indexOf("    byId('sign-exchange').addEventListener")), context);
(async () => {
    const id = 'a'.repeat(64), dialog = byId('transaction-review-dialog');
    dialog.open = true;
    context.reset(true); context.showReceipt(id);
    assert(dialog.open, 'The review popup stays open to display the result');
    assert(byId('transaction-review-content').hidden);
    assert.equal(byId('exchange-status').hidden, false);
    assert.equal(dialog['aria-labelledby'], 'exchange-status-title');
    await context.refreshReceipt(); assert.equal(byId('exchange-status-state').textContent, 'Pendente');
    status = 'confirmed'; await context.refreshReceipt();
    assert.equal(byId('exchange-status-title').textContent, 'Troca confirmada.');
    assert.equal(byId('exchange-status-block').textContent, '42');
    assert.equal(byId('exchange-status-receive').textContent, '6 POVIX');
    context.reset();
    assert.equal(dialog.open, false); assert(byId('exchange-status').hidden);
    assert.equal(byId('transaction-review-content').hidden, false);
    assert.equal(byId('exchange-status-transaction').textContent, '');
    assert.equal(byId('exchange-status-receive').textContent, '');
    assert.equal(storage.size, 0);
    const prior = requests; await context.refreshReceipt(); assert.equal(requests, prior, 'Closing stops receipt polling');
    assert(!source.includes('showReceipt(saved)'), 'Old results are not restored on page load');
    console.log('PASS: receipt inside review popup, pending to confirmed, close clears data and stops polling.');
})().catch(error => { console.error(error); process.exitCode = 1; });
