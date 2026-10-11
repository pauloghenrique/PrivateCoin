'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
(async () => {
    const elements = new Map();
    for (const id of ['registration-app', 'receipt-title', 'receipt-description', 'receipt-status', 'receipt-validations', 'receipt-confirmations', 'receipt-block', 'receipt-hash', 'receipt-connectivity']) {
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
    assert.equal(elements.get('receipt-confirmations').textContent, '0');
    assert.equal(elements.get('receipt-validations').textContent, '0');
    response = {...response, status: 'validated', validations: 1};
    await refresh();
    assert.equal(elements.get('receipt-status').textContent, 'Validado');
    assert.match(elements.get('receipt-description').textContent, /confirmação em bloco/);
    assert.match(elements.get('receipt-description').textContent, /20 operações aprovadas ou após 10 segundos/);
    assert.equal(elements.get('receipt-block').textContent, 'Aguardando confirmação');
    assert.equal(elements.get('receipt-confirmations').textContent, '0');
    assert.equal(elements.get('receipt-validations').textContent, '1');
    assert.equal(elements.get('receipt-hash').textContent, '—');
    response = {...response, status: 'confirmed', confirmations: 1, blockHeight: 42, blockHash: 'a'.repeat(64)};
    await refresh();
    assert.equal(elements.get('receipt-title').textContent, 'Token registrado.');
    assert.equal(elements.get('receipt-hash').textContent, 'a'.repeat(64));
    assert.equal(elements.get('receipt-confirmations').textContent, '1');
    response = {...response, confirmations: 2};
    await refresh();
    assert.equal(elements.get('receipt-confirmations').textContent, '2');
    assert.equal(elements.get('receipt-validations').textContent, '1');
    response = {...response, status: 'validated', confirmations: 0, blockHeight: null, blockHash: null};
    elements.get('registration-app').dataset.operation = 'transfer';
    await refresh();
    assert.equal(elements.get('receipt-title').textContent, 'Movimentação validada.');
    assert.match(elements.get('receipt-description').textContent, /taxa serão efetivados após a confirmação em bloco/);
    assert.equal(elements.get('receipt-block').textContent, 'Aguardando confirmação');
    assert.equal(elements.get('receipt-hash').textContent, '—');
    assert.equal(elements.get('receipt-confirmations').textContent, '0');
    response = {...response, status: 'pending', validations: 0, confirmations: 2, blockHeight: 42, blockHash: 'a'.repeat(64)};
    await refresh();
    assert.equal(elements.get('receipt-status').textContent, 'Pendente');
    assert.equal(elements.get('receipt-confirmations').textContent, '0');
    assert.equal(elements.get('receipt-validations').textContent, '0');
    assert.equal(elements.get('receipt-block').textContent, 'Aguardando confirmação');
    assert.equal(elements.get('receipt-hash').textContent, '—');
    response = {...response, status: 'confirmed', blockHeight: null, validations: 1};
    await refresh();
    assert.equal(elements.get('receipt-title').textContent, 'Movimentação validada.');
    assert.equal(elements.get('receipt-confirmations').textContent, '0');
    assert.equal(elements.get('receipt-hash').textContent, '—');
    response = {...response, status: 'rejected'};
    await refresh();
    assert.equal(elements.get('receipt-status').textContent, 'Não confirmado');
    assert.equal(elements.get('receipt-confirmations').textContent, '0');
    assert.equal(elements.get('receipt-hash').textContent, '—');
    console.log('8 approval receipt transitions passed');
})().catch(error => {console.error(error); process.exitCode = 1;});
