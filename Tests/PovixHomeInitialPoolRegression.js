'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
class Node {
    constructor() { this.children = []; this.textContent = ''; this.value = ''; this.listeners = {}; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    addEventListener(event, handler) { this.listeners[event] = handler; }
    querySelector() { return { value: 'csrf' }; }
}
const nodes = new Map();
const get = id => { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
get('dex-home').dataset = { marketUrl: '/Liquidity/Market', catalogueUrl: '/Liquidity/Catalogue' };
const assetA = { id: 'povix', name: 'POVIX', symbol: 'POVIX', decimals: 8 };
const assetB = { id: 'usdc', name: 'USDC local', symbol: 'USDC', decimals: 6 };
let pools = [
    { poolId: 'a'.repeat(64), nativeInitialLaunch: true, available: false, status: 'collecting', assetA, assetB, reserveAAtomic: '35940000000000', reserveBAtomic: '0' },
    { poolId: 'b'.repeat(64), available: false, assetA, assetB },
    { poolId: 'c'.repeat(64), available: true, assetA, assetB, reserveAAtomic: '100000000', reserveBAtomic: '1000000' }
];
const context = { document: { getElementById: get, createElement: () => new Node() },
    window: { location: { href: 'https://dex.example/' } }, URL, URLSearchParams,
    fetch: async () => ({ ok: true, json: async () => ({ pools }) }) };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../Povix.Dex/Scripts/povix-home.js'), 'utf8'), context);
const text = node => node.textContent + node.children.map(text).join(' ');
(async () => {
    await new Promise(resolve => setImmediate(resolve));
    const rows = get('dex-token-list').children;
    assert.equal(rows.length, 2);
    assert.match(text(rows[0]), /359\.400 POVIX/);
    assert.match(text(rows[0]), /0 USDC/);
    assert.match(text(rows[0]), /Em captação/);
    assert.match(text(rows[0]), /Ver pool/);
    assert(!text(rows[0]).includes('Negociar'));
    assert.equal(get('dex-pool-count').textContent, 2);
    assert.equal(get('dex-asset-count').textContent, 2);
    get('dex-search').value = 'a'.repeat(64); get('dex-search').listeners.input();
    assert.equal(get('dex-token-list').children.length, 1);
    get('dex-search').value = '';
    pools[0] = { ...pools[0], available: true, status: 'available', reserveAAtomic: '17970000000000', reserveBAtomic: '29950000000' };
    await get('dex-refresh').listeners.click();
    assert.match(text(get('dex-token-list').children[0]), /Negociação aberta/);
    assert.match(text(get('dex-token-list').children[0]), /179\.700 POVIX/);
    assert.match(text(get('dex-token-list').children[0]), /Negociar/);
    console.log('PASS: native collecting pool on home, live reserves, status, metrics, search and trading activation.');
})().catch(error => { console.error(error); process.exitCode = 1; });
