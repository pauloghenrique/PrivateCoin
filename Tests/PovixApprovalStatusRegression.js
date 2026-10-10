'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
(async () => {
    const elements = new Map();
    for (const id of ['registration-app', 'receipt-title', 'receipt-description', 'receipt-status', 'receipt-confirmations', 'receipt-block', 'receipt-hash', 'receipt-connectivity']) {
        elements.set(id, {textContent: '', dataset: {statusUrl: '/status'}, classList: {toggle() {}}});
    }
    let refresh, response = {status: 'pending', confirmations: 0, validations: 0, blockHeight: null, blockHash: null, peerCount: 1};
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-token-status.js', 'utf8'), {
        document: {getElementById: id => elements.get(id)},
        fetch: async () => ({ok: true, json: async () => response}),
        setInterval: callback => {refresh = callback;}
    });
    const tick = () => new Promise(resolve => setImmediate(resolve));
    await tick();
    assert.equal(elements.get('receipt-status').textContent, 'Aguardando validação');
    response = {...response, status: 'validated', validations: 1};
    await refresh();
    assert.equal(elements.get('receipt-status').textContent, 'Validado');
    assert.match(elements.get('receipt-description').textContent, /confirmação em bloco/);
    assert.equal(elements.get('receipt-block').textContent, 'Aguardando confirmação');
    assert.equal(elements.get('receipt-confirmations').textContent, '1');
    assert.equal(elements.get('receipt-hash').textContent, '—');
    response = {...response, status: 'confirmed', confirmations: 1, blockHeight: 42, blockHash: 'confirmed-block'};
    await refresh();
    assert.equal(elements.get('receipt-title').textContent, 'Token registrado.');
    assert.equal(elements.get('receipt-hash').textContent, 'confirmed-block');
    response = {...response, status: 'validated', confirmations: 0, blockHeight: null, blockHash: null};
    elements.get('registration-app').dataset.operation = 'transfer';
    await refresh();
    assert.equal(elements.get('receipt-title').textContent, 'Movimentação validada.');
    assert.match(elements.get('receipt-description').textContent, /taxa serão efetivados após a confirmação em bloco/);
    assert.equal(elements.get('receipt-block').textContent, 'Aguardando confirmação');
    assert.equal(elements.get('receipt-hash').textContent, '—');
    response = {...response, status: 'rejected'};
    await refresh();
    assert.equal(elements.get('receipt-status').textContent, 'Não confirmado');
    console.log('5 approval receipt transitions passed');
})().catch(error => {console.error(error); process.exitCode = 1;});
