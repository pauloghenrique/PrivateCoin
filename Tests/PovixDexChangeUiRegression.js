'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

module.exports = async function checkChangeUi(wallet, fixture) {
    const originalAddresses = wallet.addresses();
    const nodes = new Map();
    const node = id => {
        if (!nodes.has(id)) nodes.set(id, {
            value: '', files: [], dataset: {}, listeners: {}, options: [], hidden: false, textContent: '',
            classList: { toggle() {}, add() {} },
            addEventListener(type, listener) { this.listeners[type] = listener; },
            querySelector(selector) { return { value: selector.includes('FeePriority') ? '2' : 'test-csrf' }; },
            replaceChildren(...options) { this.options = options; this.value = options[0].value; },
            add(option) { this.options.push(option); },
            reportValidity() { return true; }, scrollIntoView() {}
        });
        return nodes.get(id);
    };
    node('token-app').dataset = { networkUrl: '/network', balanceUrl: '/balance', prepareUrl: '/prepare', networkId: fixture.networkId };
    for (const key of ['Name', 'Symbol', 'Supply', 'Decimals', 'DestinationAddress']) node(key).value = fixture.expected[key];
    node('DestinationAddress').value = '';
    let pending = false, refresh, preparations = 0;
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../Povix.Dex/Scripts/povix-token-create.js'), 'utf8'), {
        window: { PovixTokenWallet: wallet }, document: { getElementById: node, querySelectorAll: () => [] },
        Option: class { constructor(text, value) { this.text = text; this.value = value; } },
        URLSearchParams, setInterval(callback) { refresh = callback; },
        fetch: async (url, options) => {
            let result;
            if (url === '/network') result = { CanCreate: true, PeerCount: 2, Height: 5, Fees: ['1', '2', '4'] };
            else if (url === '/balance') result = { balanceAtomic: pending ? '0' : '600000000', confirmedAtomic: '600000000',
                reservedAtomic: pending ? '600000000' : '0', pendingIncomingAtomic: pending ? fixture.draft.changeAtomic : '0' };
            else {
                assert.equal(url, '/prepare');
                const body = new URLSearchParams(options.body);
                assert(originalAddresses.includes(body.get('changeAddress')), 'change must return to an address in the original wallet');
                assert.equal(wallet.addresses().length, originalAddresses.length, 'preparation must not add a browser-only key');
                assert.equal(body.get('changeAddress'), fixture.changeAddress);
                preparations++;
                result = fixture.draft;
            }
            return { ok: true, json: async () => result };
        }
    });
    const bytes = new TextEncoder().encode(JSON.stringify(fixture.wallet));
    node('wallet-file').files = [{ size: bytes.length, arrayBuffer: async () => bytes.slice().buffer }];
    node('wallet-password').value = 'dex-test-password-only';
    await node('wallet-form').listeners.submit({ preventDefault() {} });
    node('wallet-choice').value = '0';
    await node('wallet-form').listeners.submit({ preventDefault() {} });
    assert.equal(node('DestinationAddress').value, originalAddresses[0], 'opening the wallet defaults the issuance to an existing key');
    assert(node('destination-help').textContent.includes('já salvo'));
    node('DestinationAddress').value = '0'.repeat(64);
    node('token-form').listeners.input();
    assert(node('review-destination-help').textContent.includes('não pertence'), 'external issuance must explain why creator authorization alone cannot spend it');
    node('wallet-lock').listeners.click();
    node('wallet-password').value = 'dex-test-password-only';
    await node('wallet-form').listeners.submit({ preventDefault() {} });
    node('wallet-choice').value = '0';
    await node('wallet-form').listeners.submit({ preventDefault() {} });
    assert.equal(node('DestinationAddress').value, '0'.repeat(64), 'opening a wallet must preserve an explicitly chosen destination');
    assert.equal(node('app-message').textContent, '');
    assert.equal(wallet.isUnlocked(), true);
    await node('own-address').listeners.click();
    assert.equal(node('DestinationAddress').value, originalAddresses[0]);
    assert.deepEqual(wallet.addresses(), originalAddresses, 'the receive button must not create a key missing from wallets.dat');
    node('DestinationAddress').value = fixture.expected.DestinationAddress;
    node('token-form').listeners.input();
    await node('token-form').listeners.submit({ preventDefault() {} });
    assert.equal(node('app-message').textContent, '');
    assert.equal(preparations, 1);
    assert.equal(node('review-change').textContent, wallet.formatAtomic(fixture.draft.changeAtomic, 8) + ' POVIX');
    assert.deepEqual(wallet.addresses(), originalAddresses);
    pending = true;
    await refresh();
    assert.equal(node('wallet-balance').textContent, '0,00000000 POVIX');
    assert(node('wallet-balance-detail').textContent.includes('Confirmado: 6,00000000 POVIX'));
    assert(node('wallet-balance-detail').textContent.includes('Reservado: 6,00000000 POVIX'));
    assert(node('wallet-balance-detail').textContent.includes('A receber após confirmação: ' + wallet.formatAtomic(fixture.draft.changeAtomic, 8)));
    console.log('PASS token form receives issuance and change under original keys, explains external destinations and distinguishes reserved funds from the fee');
};
