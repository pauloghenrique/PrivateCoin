'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
// Exercise the actual list predicate before the page starts its network polling.
const source = fs.readFileSync(path.join(__dirname, '../Povix.Dex/Scripts/povix-liquidity.js'), 'utf8')
    .replace("    let swapDirection =", '    window.visiblePool = visiblePool; return;\n    let swapDirection =');
let checks = 0;
for (const mode of ['manage', 'launch', 'all']) {
    for (const unlocked of [true, false]) {
        const context = { window: { PovixTokenWallet: { isUnlocked: () => unlocked, addresses: () => ['mine'] } },
            document: { getElementById: id => id === 'liquidity-app' ? { dataset: { mode } } :
                { querySelector: () => ({ value: 'test-csrf' }) } } };
        vm.runInNewContext(source, context);
        const visible = context.window.visiblePool;
        assert(visible({ nativeInitialLaunch: true, providerAddress: 'ownerless', launch: null }));
        assert.equal(visible({ providerAddress: 'other', launch: null }), mode === 'all');
        assert.equal(visible({ providerAddress: 'mine', launch: null }), mode === 'all' || mode === 'manage' && unlocked);
        assert.equal(visible({ providerAddress: 'other', launch: {} }), mode !== 'manage');
        checks += 4;
    }
}
console.log(checks + ' native pool visibility checks passed');
