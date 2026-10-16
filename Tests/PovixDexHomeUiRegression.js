'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class Element {
    constructor() { this.children = []; this.textContent = ''; this.listeners = {}; this.value = ''; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener(event, callback) { this.listeners[event] = callback; }
}
(async () => {
    const ids = {}, get = id => ids[id] || (ids[id] = new Element());
    const app = get('dex-home'); app.dataset = { catalogueUrl: '/Liquidity/Catalogue', marketUrl: '/Liquidity/Market' };
    app.querySelector = () => ({ value: 'csrf-test' });
    let fail = false;
    const a = { id: 'povix', name: 'POVIX', symbol: 'POVIX', decimals: 8 };
    const b = { id: 'asset-b', name: '<unsafe>', symbol: 'TEST', decimals: 2 };
    let pools = [{ poolId: 'a'.repeat(64), available: true, assetA: a, assetB: b, reserveAAtomic: '9007199254740993', reserveBAtomic: '12345' }, { poolId: 'b'.repeat(64), available: false, assetA: a, assetB: b }];
    const storage = new Map();
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-home.js', 'utf8'), {
        document: { getElementById: get, createElement: () => new Element() },
        sessionStorage: { getItem: k => storage.get(k), setItem: (k, v) => storage.set(k, v) },
        window: { location: { href: 'https://dex.povix.org/' } }, URL, URLSearchParams,
        fetch: async (url, options) => { assert.equal(url, '/Liquidity/Catalogue'); assert.equal(options.credentials, 'same-origin'); assert.equal(options.body.get('__RequestVerificationToken'), 'csrf-test'); return { ok: !fail, json: async () => ({ pools }) }; }
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(get('dex-token-list').children.length, 1);
    assert.equal(get('dex-asset-count').textContent, 2);
    assert.equal(get('dex-catalogue-assets').children.length, 2);
    const row = get('dex-token-list').children[0];
    assert.equal(row.children[1].children[0].textContent, '90071992,54740993 POVIX');
    assert.equal(row.children[1].children[1].textContent, '123,45 TEST');
    const link = row.children[2].children[0];
    assert.equal(new URL(link.href).searchParams.get('poolId'), 'a'.repeat(64));
    link.listeners.click(); assert.match(storage.get('povi-dex-recent'), /aaaa/);
    get('dex-search').value = 'missing'; get('dex-search').listeners.input();
    assert.equal(get('dex-token-list').children.length, 0);
    assert.match(get('dex-status').textContent, /Nenhuma pool corresponde/);
    get('dex-search').value = ''; get('dex-search').listeners.input();
    assert.equal(get('dex-recent-pools').children.length, 1);
    fail = true; await get('dex-refresh').listeners.click();
    assert.equal(get('dex-token-list').children.length, 1);
    assert.match(get('dex-status').textContent, /último catálogo/);
    fail = false; pools = []; await get('dex-refresh').listeners.click();
    assert.equal(get('dex-token-list').children.length, 0);
    assert.match(get('dex-status').textContent, /Nenhuma pool disponível/);
    assert.equal(get('dex-refresh').disabled, false);
    console.log('PASS: native catalogue, exact reserves, search, recent pools, unavailable pools, stale catalogue and empty state.');
})().catch(error => { console.error(error); process.exitCode = 1; });
