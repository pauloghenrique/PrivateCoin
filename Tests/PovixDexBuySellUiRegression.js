'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class Element {
    constructor() { this.value = ''; this.handlers = {}; this.children = []; this.options = [{ value: '1' }, { value: '0' }]; this.dataset = {}; }
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
    const pool = { poolId: 'a'.repeat(64), available: true, status: 'available', assetA: { id: 'aaa', symbol: 'AAA', decimals: 2 }, assetB: { id: 'bbb', symbol: 'BBB', decimals: 2 }, reserveAAtomic: '10000', reserveBAtomic: '20000' };
    get('liquidity-app').dataset = { integrated: 'true', poolId: pool.poolId, networkUrl: '/network', catalogueUrl: '/catalogue', prepareSwapUrl: '/prepare-swap', marketUrl: '/market/__id__', receiptUrl: '/receipt/__id__' };
    get('exchange-priority').value = '2';
    let unlocked = true; const preparedSides = [];
    const wallet = { isUnlocked: () => unlocked, hasSavedWallet: () => false, publicKeys: () => [], addresses: () => ['address'], formatAtomic: value => value, parseSupply: value => { if (!value) throw Error(); return '100'; }, quoteExchange: () => '200', lock: () => { unlocked = false; } };
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8'), {
        document: { getElementById: get, createElement: () => new Element(), addEventListener() {} }, window: { PovixTokenWallet: wallet },
        fetch: async (url, options) => {
            if (url === '/prepare-swap') { preparedSides.push(options.body.get('InputSide')); return { ok: false, status: 400, json: async () => ({ error: 'Test stops before signing' }) }; }
            return { ok: true, json: async () => url === '/network' ? { CanCreate: true, Fees: ['1', '2', '4'] } : { assets: [{ id: 'povix', balanceAtomic: '10000' }, { ...pool.assetA, balanceAtomic: '10000' }, { ...pool.assetB, balanceAtomic: '10000' }], pools: [pool] } };
        },
        URL, location: { origin: 'https://example.test' }, URLSearchParams, Option: class { constructor(text, value) { this.textContent = text; this.value = value; } }, setInterval() {}, sessionStorage: { getItem: () => null }
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(get('swap-sell').disabled, false, get('app-message').textContent);
    assert.equal(get('swap-side').value, '1');
    assert.equal(get('swap-buy')['aria-pressed'], 'true');
    assert.equal(get('swap-buy').title, 'BBB → AAA');
    assert.equal(get('swap-sell').title, 'AAA → BBB');
    get('swap-amount').value = '1'; get('swap-minimum').value = '0,5';
    get('swap-sell').handlers.click();
    assert.equal(get('swap-side').value, '0');
    assert.equal(get('swap-sell')['aria-pressed'], 'true');
    assert.equal(get('swap-amount').value, ''); assert.equal(get('swap-minimum').value, '');
    get('swap-amount').value = '1'; get('swap-form').handlers.input();
    assert.match(get('swap-estimate').textContent, /BBB/);
    get('swap-buy').handlers.click(); get('swap-amount').value = '1'; get('swap-form').handlers.input();
    assert.match(get('swap-estimate').textContent, /AAA/);
    assert.equal(get('swap-side').options[0].textContent, 'BBB → AAA', 'labels must follow option values rather than position');
    assert.equal(get('swap-side').options[1].textContent, 'AAA → BBB');
    for (const side of ['0', '1']) {
        get('swap-side').value = side;
        get('swap-amount').value = '1'; get('swap-minimum').value = '0,1';
        await get('swap-form').handlers.submit({ preventDefault() {} });
        assert.equal(preparedSides.at(-1), side, 'the request must preserve the selected direction');
    }
    get('swap-side').value = 'invalid';
    const before = preparedSides.length;
    await get('swap-form').handlers.submit({ preventDefault() {} });
    assert.equal(preparedSides.length, before, 'an invalid direction must not prepare a swap');
    get('swap-side').value = '1';
    get('wallet-lock').handlers.click();
    assert.equal(get('swap-buy').disabled, true); assert.equal(get('swap-sell').disabled, true);
    get('swap-sell').handlers.click(); assert.equal(get('swap-side').value, '1');
    console.log('PASS: BUY B → A, SELL A → B, quote asset, cleared amounts and locked-wallet guards.');
})().catch(error => { console.error(error); process.exitCode = 1; });
