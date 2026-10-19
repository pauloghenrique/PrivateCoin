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
    window: { location: { href: 'https://dex.example/' }, setInterval: handler => { context.poll = handler; return 1; }, clearInterval: () => {}, addEventListener: () => {} }, URL, URLSearchParams,
    fetch: async () => ({ ok: true, json: async () => ({ pools }) }) };
get('dex-home').dataset.initialCatalogue = JSON.stringify({ pools });
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../Povix.Dex/Scripts/povix-home.js'), 'utf8'), context);
assert.equal(get('dex-token-list').children.length, 1, 'Only launched pools belong in the main list');
assert.equal(get('dex-launch-list').children.length, 1, 'Collecting pools render in the launch card before fetch resolves');
const text = node => node.textContent + node.children.map(text).join(' ');
(async () => {
    await new Promise(resolve => setImmediate(resolve));
    const rows = get('dex-launch-list').children;
    assert.equal(rows.length, 1);
    assert.equal(get('dex-token-list').children.length, 1);
    assert.match(text(rows[0]), /POVIX\/USDC/);
    assert.match(text(rows[0]), /POVIX \/ USDC local/);
    assert.equal(rows[0].children.length, 2);
    assert(!text(rows[0]).includes('359.400'));
    assert(!text(rows[0]).includes('…'));
    
    assert(!text(rows[0]).includes('Em captação'));
    assert.match(text(rows[0]), /Ver pool/);
    assert(!text(rows[0]).includes('Negociar'));
    assert.equal(get('dex-pool-count').textContent, 2);
    assert.equal(get('dex-asset-count').textContent, 2);
    get('dex-search').value = 'a'.repeat(64); get('dex-search').listeners.input();
    assert.equal(get('dex-token-list').children.length, 0);
    assert.equal(get('dex-launch-list').children.length, 1);
    assert.equal(get('dex-launch-status').hidden, true);
    get('dex-search').value = '';
    pools[0] = { ...pools[0], available: true, status: 'available', reserveAAtomic: '17970000000000', reserveBAtomic: '29950000000' };
    await get('dex-refresh').listeners.click();
    assert.equal(get('dex-launch-list').children.length, 0, 'Activated launch leaves the launch card');
    assert.equal(get('dex-token-list').children.length, 2);
    assert.match(get('dex-launch-status').textContent, /Nenhuma pool em lançamento/);
    assert.match(text(get('dex-token-list').children[0]), /Negociação aberta/);
    assert.match(text(get('dex-token-list').children[0]), /179\.700 POVIX/);
    assert.match(text(get('dex-token-list').children[0]), /Negociar/);
    pools = [];
    await context.poll();
    assert.equal(get('dex-token-list').children.length, 0);
    pools = [{ poolId: 'd'.repeat(64), nativeInitialLaunch: true, available: false, status: 'collecting', assetA, assetB, reserveAAtomic: '35940000000000', reserveBAtomic: '0' }];
    await context.poll();
    assert.equal(get('dex-token-list').children.length, 0);
    assert.match(text(get('dex-launch-list').children[0]), /Ver pool/);
    pools.push({ ...pools[0], poolId: 'e'.repeat(64), nativeInitialLaunch: false, launch: {} });
    pools.push({ ...pools[0], poolId: 'f'.repeat(64), status: 'launch_expired' });
    await context.poll();
    assert.equal(get('dex-launch-list').children.length, 2, 'Native and token launches appear; expired launches do not');
    assert(!text(get('dex-launch-list').children[1]).includes('6 POVIX'));
    get('dex-search').value = 'no-match'; get('dex-search').listeners.input();
    assert.equal(get('dex-launch-list').children.length, 0);
    assert.match(get('dex-launch-status').textContent, /corresponde à busca/);
    context.fetch = async () => { throw new Error('offline'); };
    await context.poll();
    assert.match(get('dex-launch-status').textContent, /Não foi possível atualizar/);
    assert.match(get('dex-status').textContent, /Não foi possível atualizar/);
    console.log('PASS: separate launch card and launched list, native and token launches, search, activation, empty states and refresh failure.');
})().catch(error => { console.error(error); process.exitCode = 1; });
