'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8');
const predicate = source.slice(source.indexOf('    function visiblePool('), source.indexOf('    let swapDirection ='));
const start = source.indexOf('            const visiblePools =');
const end = source.indexOf('            visiblePools.forEach', start);
for (const [unlocked, addresses, managing, expected] of [
    [true, ['alice'], true, 2], [true, ['bob'], true, 1],
    [true, ['alice', 'bob'], true, 3], [false, ['alice'], true, 0],
    [true, [], true, 0], [true, ['alice'], false, 3]
]) {
    const nodes = { 'pool-list': { replaceChildren() { this.cleared = true; } }, 'pools-empty': {} };
    const context = { app: { dataset: {} }, managing, pools: [{ providerAddress: 'alice' }, { providerAddress: 'bob' }, { providerAddress: 'alice' }],
        wallet: { isUnlocked: () => unlocked, addresses: () => addresses }, byId: id => nodes[id] };
    vm.runInNewContext(predicate + source.slice(start, end) + '\nglobalThis.count = visiblePools.length;', context);
    assert.equal(context.count, expected);
    assert.equal(nodes['pools-empty'].hidden, expected > 0);
    assert.equal(nodes['pool-list'].cleared, true);
    if (!unlocked) assert.match(nodes['pools-empty'].textContent, /Conecte/);
}
console.log('PASS: owned pools, multiple wallet addresses, disconnected wallet, empty list and public market catalogue.');
const filterStart = source.indexOf('    function filterPoolList() {');
const filterEnd = source.indexOf("    if (managing) {", filterStart);
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

const actionNodes = {}, actionNode = id => {
    if (id === 'pool-reward-mode' || id === 'pool-burn-fields') return null;
    return actionNodes[id] || (actionNodes[id] = { value: '', children: [], dataset: { walletUrl: id === 'create-launch-pool' ? '/liquidez/lancamento' : '/liquidez/criar' },
        querySelectorAll: () => [], replaceChildren() { this.children = []; },
        setAttribute(key, value) { this[key] = value; }, removeAttribute(key) { delete this[key]; },
        addEventListener(type, callback) { this[type] = callback; } });
};
let connected = false;
const actionContext = { app: { dataset: {} }, byId: actionNode, wallet: { isUnlocked: () => connected }, assets: [], pools: [], busy: false, draft: null, network: null,
    integrated: false, managing: true, window: {}, updateUserLiquidity() {}, updatePrices() {}, selectedPool: () => null, asset: () => null,
    atomicText: () => '0', fee: () => '0', nativeCollecting: () => false };
const updateStart = source.indexOf('    function update() {'), updateEnd = source.indexOf('    function addReview(', updateStart);
const actionStart = source.indexOf("    if (managing) {\n        for (const id of ['create-pool', 'create-launch-pool']) byId(id).addEventListener"),
    actionEnd = source.indexOf("    if (byId('pool-search')) {", actionStart);
vm.createContext(actionContext); vm.runInContext(source.slice(updateStart, updateEnd) + source.slice(filterStart, filterEnd).split('    if (managing) {')[0] + source.slice(actionStart, actionEnd), actionContext);
for (const value of [false, true, false, true]) {
    connected = value;
    actionNode('pool-list').children = [{ dataset: { search: 'pool', group: 'available' } }];
    actionContext.update();
    assert.equal(actionNode('pool-list').hidden, !connected);
    assert.equal(actionNode('pool-list').children.length, connected ? 1 : 0, 'Disconnect clears all existing pool rows immediately');
    assert.equal(actionNode('pool-search').disabled, !connected);
    assert.equal(actionNode('pool-status-filter').disabled, !connected);
    for (const id of ['create-pool', 'create-launch-pool']) {
        const link = actionNode(id);
        assert.equal(link['aria-disabled'], String(!connected));
        assert.equal(link.href, connected ? link.dataset.walletUrl : undefined);
        let prevented = false; link.click({ preventDefault() { prevented = true; } });
        assert.equal(prevented, !connected, 'Disconnected actions cannot navigate');
    }
}
const liquidityView = fs.readFileSync('Povix.Dex/Views/Liquidity/_PoolPage.cshtml', 'utf8');
assert.match(liquidityView, /id="create-pool"[^>]*aria-disabled="true"/);
assert.match(liquidityView, /id="create-launch-pool"[^>]*aria-disabled="true"/);
assert.match(liquidityView, /id="pool-list" hidden/);
console.log('PASS: disconnected wallet hides pools and disables create actions/filters; reconnect restores actions.');
