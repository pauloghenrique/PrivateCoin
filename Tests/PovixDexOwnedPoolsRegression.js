'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8');
const start = source.indexOf('            const visiblePools =');
const end = source.indexOf('            visiblePools.forEach', start);
for (const [unlocked, addresses, managing, expected] of [
    [true, ['alice'], true, 2], [true, ['bob'], true, 1],
    [true, ['alice', 'bob'], true, 3], [false, ['alice'], true, 0],
    [true, [], true, 0], [true, ['alice'], false, 3]
]) {
    const nodes = { 'pool-list': { replaceChildren() { this.cleared = true; } }, 'pools-empty': {} };
    const context = { managing, pools: [{ providerAddress: 'alice' }, { providerAddress: 'bob' }, { providerAddress: 'alice' }],
        wallet: { isUnlocked: () => unlocked, addresses: () => addresses }, byId: id => nodes[id] };
    vm.runInNewContext(source.slice(start, end) + '\nglobalThis.count = visiblePools.length;', context);
    assert.equal(context.count, expected);
    assert.equal(nodes['pools-empty'].hidden, expected > 0);
    assert.equal(nodes['pool-list'].cleared, true);
    if (!unlocked) assert.match(nodes['pools-empty'].textContent, /Conecte/);
}
console.log('PASS: owned pools, multiple wallet addresses, disconnected wallet, empty list and public market catalogue.');
const filterStart = source.indexOf('    function filterPoolList() {');
const filterEnd = source.indexOf("    if (byId('pool-search')) {", filterStart);
const rows = [
    { dataset: { search: 'povix abc pool-one', group: 'available' } },
    { dataset: { search: 'povix xyz pool-two', group: 'closed' } },
    { dataset: { search: 'abc xyz pool-three', group: 'waiting' } }
];
const nodes = { 'pool-search': { value: '' }, 'pool-status-filter': { value: 'all' },
    'pool-list': { children: rows }, 'pool-result-count': {}, 'pool-filter-empty': {} };
const filterContext = { byId: id => nodes[id] };
vm.runInNewContext(source.slice(filterStart, filterEnd), filterContext);
for (const [query, status, count] of [['', 'all', 3], ['POVIX', 'all', 2], [' abc ', 'available', 1], ['pool-two', 'closed', 1], ['xyz', 'available', 0]]) {
    nodes['pool-search'].value = query; nodes['pool-status-filter'].value = status;
    filterContext.filterPoolList();
    assert.equal(rows.filter(row => !row.hidden).length, count);
    assert.equal(nodes['pool-filter-empty'].hidden, count > 0);
    assert.equal(nodes['pool-result-count'].textContent, count + ' de 3 pools');
}
nodes['pool-list'].children = []; filterContext.filterPoolList();
assert.equal(nodes['pool-filter-empty'].hidden, true);
console.log('PASS: pool search, combined status filters, result counts and empty results.');
