'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const directory = process.argv[2];
const fixture = JSON.parse(fs.readFileSync(path.join(directory, 'transfer-fixture.json'), 'utf8'));
const storage = new Map();
global.localStorage = { getItem: key => storage.get(key) || null, setItem: (key, value) => storage.set(key, value) };
vm.runInThisContext(fs.readFileSync(path.join(__dirname, '../Povix.Dex/Scripts/povix-token-wallet.js'), 'utf8'));
const wallet = global.PovixTokenWallet;
const nodes = new Map();
let backupDownloads = 0, refresh, redirected, selectedPriority = '2';
const node = id => {
    if (!nodes.has(id)) nodes.set(id, {
        value: '', files: [], dataset: {}, listeners: {}, options: [], hidden: false, textContent: '', checked: false, children: [],
        classList: { toggle() {} }, addEventListener(type, listener) { this.listeners[type] = listener; },
        querySelector(selector) { return { value: selector.includes('FeePriority') ? selectedPriority : 'test-csrf' }; },
        replaceChildren(...children) { this.children = children; this.options = children; if (children[0] && children[0].value != null) this.value = children[0].value; },
        add(option) { this.options.push(option); }, reportValidity() { return true; }, scrollIntoView() {},
        appendChild(child) { this.children.push(child); }, click() { backupDownloads++; }
    });
    return nodes.get(id);
};
const document = { getElementById: node, querySelectorAll: () => [], createElement: name => ({
    name, children: [], appendChild(child) { this.children.push(child); }, click() { backupDownloads++; }
}) };
global.document = document;
node('transfer-app').dataset = { networkUrl: '/network', balanceUrl: '/balance', tokensUrl: '/tokens', historyUrl: '/history',
    prepareUrl: '/prepare', submitUrl: '/submit', receiptUrl: '/tokens/movimentacao/__id__', networkId: fixture.networkId, tokenId: fixture.expected.TokenId };
node('Amount').value = ''; node('DestinationAddress').value = '';
const submissions = [];
let preparations = 0, nativeBalance = null, tokenBalance = null, tokenConfirmed = null;
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../Povix.Dex/Scripts/povix-token-transfer.js'), 'utf8'), {
    window: { PovixTokenWallet: wallet }, document, URL, URLSearchParams,
    location: { origin: 'https://dex.test', assign(url) { redirected = url; } },
    Option: class { constructor(text, value) { this.text = text; this.value = value; } },
    setInterval(callback) { refresh = callback; },
    fetch: async (url, options) => {
        let result, status = 200;
        if (url === '/network') result = { CanCreate: true, PeerCount: 2, Height: 10, Fees: ['1', fixture.expected.FeeAtomic, '4'] };
        else if (url === '/balance') {
            const native = (BigInt(fixture.draft.povixChangeAtomic) + BigInt(fixture.draft.feeAtomic)).toString();
            result = { balanceAtomic: nativeBalance == null ? native : nativeBalance, confirmedAtomic: native, reservedAtomic: '0', pendingIncomingAtomic: '0' };
        } else if (url === '/tokens') {
            const metadata = wallet.addresses().includes(fixture.incompleteToken.receivingAddresses[0].address) ? fixture.tokens[0] : fixture.incompleteToken;
            result = [{ ...metadata, balanceAtomic: tokenBalance == null ? metadata.balanceAtomic : tokenBalance,
                confirmedAtomic: tokenConfirmed == null ? metadata.confirmedAtomic : tokenConfirmed,
                receivingAddresses: metadata.receivingAddresses.map(item => ({ ...item,
                    confirmedAtomic: tokenConfirmed == null ? item.confirmedAtomic : tokenConfirmed })) }];
        }
        else if (url === '/history') result = [];
        else if (url === '/prepare') {
            const body = new URLSearchParams(options.body);
            assert.equal(body.get('changeAddress'), fixture.changeAddress);
            assert.equal(body.get('TokenId'), fixture.expected.TokenId); assert.equal(body.get('Amount'), fixture.expected.Amount);
            assert.equal(body.get('DestinationAddress'), fixture.expected.DestinationAddress);
            assert(body.getAll('publicKeys').every(key => !Buffer.from(key, 'base64').toString('utf8').includes('<D>')));
            preparations++; result = fixture.draft;
        } else {
            assert.equal(url, '/submit');
            const body = new URLSearchParams(options.body);
            assert.deepEqual(Array.from(new Set(body.keys())).sort(), ['__RequestVerificationToken', 'creatorSignature', 'draftId', 'signatures'].sort());
            submissions.push({ signatures: body.getAll('signatures'), creatorSignature: body.get('creatorSignature') });
            if (submissions.length === 1) { status = 503; result = { error: 'Aguarde a conexão.', code: 'network_unavailable' }; }
            else result = { receiptUrl: '/tokens/movimentacao/' + 'a'.repeat(64), transactionId: 'a'.repeat(64) };
        }
        return { ok: status === 200, status, json: async () => result };
    }
});

(async () => {
    assert.equal(node('prepare-transfer').disabled, true);
    async function open(record) {
        const bytes = new TextEncoder().encode(JSON.stringify(record));
        node('wallet-file').files = [{ size: bytes.length, arrayBuffer: async () => bytes.buffer }];
        node('wallet-password').value = 'dex-test-password-only';
        await node('wallet-form').listeners.submit({ preventDefault() {} });
        assert.equal(wallet.isUnlocked(), false, 'wallet selection must be explicit');
        node('wallet-choice').value = '0';
        await node('wallet-form').listeners.submit({ preventDefault() {} });
        await refresh();
    }
    await open(fixture.incompleteWallet);
    assert(node('token-balance').textContent.includes('Disponível: 0,00000000'));
    assert(node('transfer-hint').textContent.includes('wallets.dat'));
    const receivingText = () => node('token-receiving-addresses').children.map(item => item.textContent).join(' ');
    assert(receivingText().includes(fixture.incompleteToken.receivingAddresses[0].address));
    assert(receivingText().includes('Chave ausente'));
    assert(receivingText().includes(wallet.formatAtomic(fixture.incompleteToken.receivingAddresses[0].confirmedAtomic, fixture.expected.Decimals)));
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('.povixwallet'));
    node('wallet-lock').listeners.click();
    assert.equal(receivingText(), '', 'locking the wallet removes the previous issuance diagnostics');
    await open(fixture.wallet);
    assert(node('token-balance').textContent.includes(wallet.formatAtomic(fixture.tokens[0].balanceAtomic, fixture.expected.Decimals)));
    assert(receivingText().includes('Chave presente'));
    console.log('PASS original Desktop keys show zero and the missing issuance key; reopening the updated backup restores spendable tokens before signing');
    await refresh();
    assert.equal(node('app-message').textContent, '');
    assert.equal(node('TokenId').value, fixture.expected.TokenId);
    assert.equal(node('prepare-transfer').disabled, false);
    node('TokenId').value = ''; node('TokenId').listeners.change();
    assert.equal(node('prepare-transfer').disabled, false);
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('Selecione um token'));
    node('TokenId').value = fixture.expected.TokenId; node('TokenId').listeners.change();
    node('transfer-form').listeners.invalid();
    assert(node('app-message').textContent.includes('quantidade'));
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('quantidade'));
    node('Amount').value = fixture.expected.Amount;
    node('transfer-form').listeners.change();
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('endereço'));
    node('DestinationAddress').value = fixture.expected.DestinationAddress;
    nativeBalance = '1'; await refresh();
    selectedPriority = '4'; node('transfer-form').listeners.change();
    assert.equal(node('prepare-transfer').disabled, false);
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('taxa selecionada'));
    selectedPriority = '1'; node('transfer-form').listeners.change();
    assert(node('transfer-hint').textContent.includes('Revise a movimentação'));
    tokenBalance = '0'; tokenConfirmed = '0'; nativeBalance = null; await refresh();
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('envios já realizados'));
    assert(!node('app-message').textContent.includes('chave desse endereço não está'), 'an owned address with no remaining tokens is not a missing-key diagnosis');
    tokenConfirmed = '9223372036854775807'; await refresh();
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('reservado'));
    tokenBalance = '1'; await refresh();
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(preparations, 0); assert(node('app-message').textContent.includes('saldo disponível do token'));
    tokenBalance = '9223372036854775807'; selectedPriority = '2'; await refresh();
    node('transfer-form').listeners.change();
    assert.equal(node('prepare-transfer').disabled, false);
    console.log('PASS review control validates missing token, amount, destination, selected fee, absent token keys, reserved tokens and excessive amounts without a silent disabled button');
    const addresses = wallet.addresses();
    await node('transfer-form').listeners.submit({ preventDefault() {} });
    assert.equal(node('app-message').textContent, ''); assert.equal(preparations, 1);
    assert.deepEqual(wallet.addresses(), addresses, 'transfer must not generate new change keys');
    assert.equal(node('review-token-change').textContent, wallet.formatAtomic(fixture.draft.tokenChangeAtomic, fixture.expected.Decimals) + ' ' + fixture.expected.Symbol);
    assert.equal(node('review-povix-change').textContent, wallet.formatAtomic(fixture.draft.povixChangeAtomic, 8) + ' POVIX');
    for (const changed of [
        { destinationAddress: '0'.repeat(64) }, { tokenId: '0'.repeat(64) }, { creatorAddress: '0'.repeat(64) },
        { amountAtomic: '1' }, { feeAtomic: '100000000' }, { tokenChangeAtomic: '1' }, { povixChangeAtomic: '1' },
        { changeAddress: '0'.repeat(64) }, { authorizationPayload: Buffer.from('forged').toString('base64') },
        { signingPayload: Buffer.from('forged').toString('base64') }, { draftId: '0'.repeat(32) }, { expiresUtc: '2000-01-01T00:00:00Z' }
    ]) await assert.rejects(wallet.verifyTransferDraft({ ...fixture.draft, ...changed }, fixture.expected, fixture.networkId, fixture.changeAddress));
    console.log('PASS transfer review rejects forged destination, token, creator, amounts, fee, both change outputs, payloads, replay and expiry');
    await node('sign-transfer').listeners.click(); assert.equal(submissions.length, 0);
    node('backup-confirmed').checked = true;
    await node('sign-transfer').listeners.click(); assert.equal(submissions.length, 0, 'backup download is required');
    const schedule = global.setTimeout;
    global.setTimeout = callback => { callback(); return 0; };
    try { node('download-backup').listeners.click(); } finally { global.setTimeout = schedule; }
    assert.equal(backupDownloads, 1);
    await node('sign-transfer').listeners.click();
    assert.equal(submissions.length, 1); assert.equal(node('sign-actions').hidden, false, 'temporary failure keeps the same draft');
    assert.equal(wallet.isUnlocked(), true); assert(node('app-message').textContent.includes('network_unavailable'));
    await node('sign-transfer').listeners.click();
    assert.equal(submissions.length, 2); assert.deepEqual(submissions[1], submissions[0], 'retry resends the same signatures and authorization');
    assert.equal(wallet.isUnlocked(), false); assert(redirected.startsWith('https://dex.test/tokens/movimentacao/'));
    assert(submissions[1].signatures.every(signature => Buffer.from(signature, 'base64').length === 256));
    assert.equal(Buffer.from(submissions[1].creatorSignature, 'base64').length, 256);
    fs.writeFileSync(path.join(directory, 'transfer-browser-result.json'), JSON.stringify(submissions[1]));
    console.log('PASS real transfer UI: explicit wallet selection, original change keys, backup gating, local signatures, creator proof and idempotent retry');
})().catch(error => { console.error(error); process.exitCode = 1; });
