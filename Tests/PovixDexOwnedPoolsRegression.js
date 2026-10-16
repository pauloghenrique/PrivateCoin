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
