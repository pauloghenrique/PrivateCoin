'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class Element {
    constructor() { this.value = ''; this.handlers = {}; this.children = []; this.options = [{}, {}]; this.dataset = {}; }
    addEventListener(name, handler) { this.handlers[name] = handler; }
    setAttribute(key, value) { this[key] = value; }
    querySelector() { return { value: 'csrf' }; }
    querySelectorAll() { return []; }
    replaceChildren(...items) { this.children = items; this.options = items; this.value = items[0]?.value || ''; }
    add(item) { this.options.push(item); }
    append(...items) { this.children.push(...items); }
    appendChild(item) { this.children.push(item); }
}
(async () => {
    const elements = new Map(), get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
    const pool = { poolId: 'a'.repeat(64), available: true, status: 'available', assetA: { symbol: 'AAA', decimals: 2 }, assetB: { symbol: 'BBB', decimals: 2 }, reserveAAtomic: '10000', reserveBAtomic: '20000' };
    get('liquidity-app').dataset = { integrated: 'true', poolId: pool.poolId, networkUrl: '/network', catalogueUrl: '/catalogue', marketUrl: '/market/__id__', receiptUrl: '/receipt/__id__' };
    get('exchange-priority').value = '2';
    let unlocked = true;
    const wallet = { isUnlocked: () => unlocked, hasSavedWallet: () => false, publicKeys: () => [], formatAtomic: value => value, parseSupply: value => { if (!value) throw Error(); return '100'; }, quoteExchange: () => '50', lock: () => { unlocked = false; } };
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8'), {
        document: { getElementById: get, createElement: () => new Element(), addEventListener() {} }, window: { PovixTokenWallet: wallet },
        fetch: async url => ({ ok: true, json: async () => url === '/network' ? { CanCreate: true, Fees: ['1', '2', '4'] } : { assets: [], pools: [pool] } }),
        URL, location: { origin: 'https://example.test' }, URLSearchParams, Option: class { constructor(text, value) { this.textContent = text; this.value = value; } }, setInterval() {}, sessionStorage: { getItem: () => null }
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(get('swap-sell').disabled, false, get('app-message').textContent);
    assert.equal(get('swap-side').value, '0');
    assert.equal(get('swap-buy')['aria-pressed'], 'true');
    assert.equal(get('swap-buy').title, 'AAA → BBB');
    assert.equal(get('swap-sell').title, 'BBB → AAA');
    get('swap-amount').value = '1'; get('swap-minimum').value = '0,5';
    get('swap-sell').handlers.click();
    assert.equal(get('swap-side').value, '1');
    assert.equal(get('swap-sell')['aria-pressed'], 'true');
    assert.equal(get('swap-amount').value, ''); assert.equal(get('swap-minimum').value, '');
    get('swap-amount').value = '1'; get('swap-form').handlers.input();
    assert.match(get('swap-estimate').textContent, /AAA/);
    get('swap-buy').handlers.click(); get('swap-amount').value = '1'; get('swap-form').handlers.input();
    assert.match(get('swap-estimate').textContent, /BBB/);
    get('wallet-lock').handlers.click();
    assert.equal(get('swap-buy').disabled, true); assert.equal(get('swap-sell').disabled, true);
    get('swap-sell').handlers.click(); assert.equal(get('swap-side').value, '0');
    console.log('PASS: BUY A → B, SELL B → A, quote asset, cleared amounts and locked-wallet guards.');
})().catch(error => { console.error(error); process.exitCode = 1; });
