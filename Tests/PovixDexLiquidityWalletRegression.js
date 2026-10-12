'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { webcrypto } = require('node:crypto');
const { JSDOM, VirtualConsole } = require('../work/liquidity-tools/node_modules/jsdom');
const directory = process.argv[2];
const fixture = JSON.parse(fs.readFileSync(path.join(directory, 'exchange-fixture.json'), 'utf8'));
const d = fixture.draft;

(async () => {
    let html = fs.readFileSync('Povix.Dex/Views/Liquidity/Index.cshtml', 'utf8');
    html = html.replace('@Html.Partial("_Wallet")', fs.readFileSync('Povix.Dex/Views/Shared/_Wallet.cshtml', 'utf8'))
        .replace('@Html.AntiForgeryToken()', '<input name="__RequestVerificationToken" value="test-csrf" type="hidden">')
        .replace(/data-([a-z-]+)="@Url.Action\([\s\S]*?\)"/g, (_, name) => 'data-' + name + '="/' + name + '"')
        .replace('@Model.NetworkId', fixture.networkId);
    const dom = new JSDOM(html, { url: 'https://localhost/liquidez', runScripts: 'outside-only', virtualConsole: new VirtualConsole() });
    const w = dom.window, byId = id => w.document.getElementById(id), intervals = [];
    Object.defineProperty(w, 'crypto', { value: webcrypto }); w.TextEncoder = TextEncoder; w.TextDecoder = TextDecoder;
    w.HTMLElement.prototype.scrollIntoView = () => {};
    w.URL.createObjectURL = () => 'blob:https://localhost/test-backup'; w.URL.revokeObjectURL = () => {};
    w.setInterval = callback => { intervals.push(callback); return intervals.length; };
    w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-token-wallet.js', 'utf8'));
    const wallet = w.PovixTokenWallet;
    const recordText = JSON.stringify(fixture.wallet);
    await wallet.importFile({ size: Buffer.byteLength(recordText), arrayBuffer: async () => new TextEncoder().encode(recordText).buffer }, 'dex-test-password-only');
    const expected = { operation: d.operation, assetA: d.assetA, assetB: d.assetB, reserveAAtomic: d.reserveAAtomic,
        reserveBAtomic: d.reserveBAtomic, feeAtomic: d.feeAtomic, changeAddress: d.changeAddress, poolId: d.poolId,
        reserveTransactionId: d.reserveTransactionId, providerAddress: d.providerAddress, inputSide: d.inputSide,
        amountAtomic: d.amountAtomic, minimumReceiveAtomic: d.minimumReceiveAtomic };
    await wallet.verifyExchangeDraft(d, expected, fixture.networkId, fixture.provider);
    const ownPool = d.operation === 'swap' && !fixture.provider && wallet.addresses().includes(d.providerAddress);
    const bad = change => { const draft = JSON.parse(JSON.stringify(d)); change(draft); return draft; };
    for (const alteration of [draft => { draft.outputs[0].address = '0'.repeat(64); },
        draft => { draft.outputs[0].amountAtomic = String(BigInt(draft.outputs[0].amountAtomic) + 1n); },
        draft => { draft.signingPayload = Buffer.from('tampered').toString('base64'); },
        draft => { draft.inputs[0].transactionId = '0'.repeat(64); },
        draft => { draft.expiresUtc = '2000-01-01T00:00:00Z'; },
        draft => { draft.assetA.id = draft.assetB.id; },
        draft => { draft.feeAtomic = '0'; }, draft => { draft.transactionKind = 4; }])
        await assert.rejects(wallet.verifyExchangeDraft(bad(alteration), expected, fixture.networkId, fixture.provider));
    if (d.operation === 'swap') {
        assert.equal(wallet.quoteExchange(d.inputSide === 0 ? d.reserveAAtomic : d.reserveBAtomic, d.inputSide === 0 ? d.reserveBAtomic : d.reserveAAtomic, d.amountAtomic), d.receiveAtomic);
        await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.inputs[draft.userInputCount].outputIndex = 2; }), expected, fixture.networkId, fixture.provider));
        await assert.rejects(wallet.verifyExchangeDraft(d, { ...expected, minimumReceiveAtomic: String(BigInt(d.receiveAtomic) + 1n) }, fixture.networkId, fixture.provider));
    }
    assert(BigInt(wallet.quoteExchange('4611686018427387903', '9223372036854775807', '4611686018427387903')) > 0n);
    wallet.lock();
    const app = byId('liquidity-app');
    for (const key of ['networkUrl', 'catalogueUrl', 'preparePoolUrl', 'prepareSwapUrl', 'prepareReservesUrl', 'submitReservesUrl', 'submitPoolUrl', 'submitSwapUrl', 'proposeUrl', 'requestsUrl', 'acceptUrl', 'requestStatusUrl']) app.dataset[key] = '/' + key;
    app.dataset.networkId = fixture.networkId; app.dataset.receiptUrl = '/liquidez/operacao/__id__';
    const lifecyclePool = fixture.catalogue.pools.find(pool => pool.available);
    let poolPhase = lifecyclePool ? 'pending' : 'available';
    if (lifecyclePool) app.dataset.poolId = lifecyclePool.poolId;
    let captured = null, preparations = 0, submittedUrl = null;
    w.fetch = async (url, options = {}) => {
        const body = options.body || new URLSearchParams();
        if (options.method === 'POST') assert.equal(body.get('__RequestVerificationToken'), 'test-csrf');
        let result;
        if (url === '/networkUrl') result = { CanCreate: true, PeerCount: 1, Height: 10, Fees: [1, 2, 4] };
        else if (url === '/catalogueUrl') {
            result = JSON.parse(JSON.stringify(fixture.catalogue));
            if (lifecyclePool && poolPhase !== 'available') {
                const pool = result.pools.find(item => item.poolId === lifecyclePool.poolId);
                pool.status = poolPhase; pool.available = false;
                pool.statusMessage = poolPhase === 'pending' ? 'A criação aguarda validação da rede e confirmação em bloco.' : 'A criação foi validada e aguarda confirmação em bloco.';
            }
        }
        else if (url === '/requestsUrl') result = fixture.provider ? [d] : [];
        else if (url === '/preparePoolUrl' || url === '/prepareSwapUrl') { preparations++; result = d; assert.equal(body.get('FeeAtomic'), d.feeAtomic); }
        else if (url === '/prepareReservesUrl') {
            preparations++; result = d; assert.equal(body.get('feeAtomic'), d.feeAtomic);
            assert.equal(body.get('operation'), d.operation); assert.equal(body.get('poolId'), d.poolId);
        }
        else if (url === '/submitReservesUrl' || url === '/submitPoolUrl' || url === '/submitSwapUrl' || url === '/acceptUrl' || url === '/proposeUrl') {
            assert.equal(body.get('draftId'), d.draftId); captured = body.getAll('signatures');
            submittedUrl = url;
            result = url === '/proposeUrl' ? { requestId: d.draftId, status: 'awaiting_provider' } : { transactionId: 'a'.repeat(64), receiptUrl: '/liquidez/operacao/' + 'a'.repeat(64) };
        } else if (url === '/requestStatusUrl') result = { status: 'awaiting_provider' };
        else throw new Error('Unexpected endpoint: ' + url);
        return { ok: true, status: 200, json: async () => result };
    };
    w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8'));
    const wait = async predicate => { for (let i = 0; i < 250 && !predicate(); i++) await new Promise(resolve => setTimeout(resolve, 20)); assert(predicate(), byId('app-message').textContent || 'UI timeout'); };
    await wait(() => byId('pool-asset-a').options.length > 1);
    assert.equal(byId('prepare-pool').disabled, true);
    byId('wallet-password').value = 'dex-test-password-only'; byId('wallet-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    await wait(() => !byId('wallet-selection').hidden && !byId('wallet-choice').disabled);
    byId('wallet-choice').value = '0'; byId('wallet-choice').dispatchEvent(new w.Event('change'));
    byId('wallet-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    await wait(() => !byId('wallet-connected').hidden && !byId('wallet-lock').disabled);
    await intervals[0](); await wait(() => !byId('prepare-pool').disabled);
    if (lifecyclePool) {
        const selected = () => byId('swap-pool').options[byId('swap-pool').selectedIndex];
        assert.equal(byId('swap-pool').value, lifecyclePool.poolId);
        assert(selected().disabled); assert(byId('prepare-swap').disabled);
        assert(byId('swap-pool-state').textContent.includes('aguarda validação'));
        poolPhase = 'validated'; await intervals[0]();
        assert(selected().disabled); assert(byId('prepare-swap').disabled);
        assert(byId('swap-pool-state').textContent.includes('validada'));
        poolPhase = 'available'; await intervals[0]();
        assert.equal(byId('swap-pool').value, lifecyclePool.poolId, 'confirmation keeps the newly created pool selected');
        assert.equal(selected().disabled, false); assert.equal(byId('prepare-swap').disabled, false);
        assert.equal(byId('wallet-form').hidden, true, 'confirmation does not require choosing the wallet again');
    }
    byId('pool-form').requestSubmit(); assert.equal(preparations, 0, 'empty fields must not prepare a transaction');
    if (fixture.provider) {
        await wait(() => byId('provider-requests').children.length > 0);
        byId('provider-requests').firstElementChild.click();
    } else if (d.operation === 'pool') {
        byId('pool-asset-a').value = d.assetA.id; byId('pool-asset-b').value = d.assetB.id;
        byId('pool-amount-a').value = wallet.formatAtomic(d.reserveAAtomic, d.assetA.decimals);
        byId('pool-amount-b').value = wallet.formatAtomic(d.reserveBAtomic, d.assetB.decimals);
        byId('pool-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    } else if (d.operation === 'lock' || d.operation === 'close') {
        const pool = fixture.catalogue.pools.find(pool => pool.poolId === d.poolId);
        assert(pool.canManage); assert.equal(pool.locked, d.operation === 'close');
        const button = [...byId('pool-list').querySelectorAll('button')].find(button =>
            button.previousElementSibling.href.includes(d.poolId));
        assert(button); button.click();
    } else {
        byId('swap-pool').value = d.poolId; byId('swap-side').value = String(d.inputSide);
        const input = d.inputSide === 0 ? d.assetA : d.assetB, output = d.inputSide === 0 ? d.assetB : d.assetA;
        byId('swap-amount').value = wallet.formatAtomic(d.amountAtomic, input.decimals);
        byId('swap-minimum').value = wallet.formatAtomic(d.minimumReceiveAtomic, output.decimals);
        byId('swap-form').dispatchEvent(new w.Event('change'));
        assert(byId('swap-estimate').textContent.includes(wallet.formatAtomic(d.receiveAtomic, output.decimals)));
        byId('swap-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    }
    await wait(() => !byId('sign-actions').hidden && !byId('download-backup').disabled);
    if (ownPool) {
        assert.equal(byId('review-title').textContent, 'Executar troca');
        assert(byId('review-description').textContent.includes('carteira selecionada'));
        assert.equal(byId('sign-exchange').textContent, 'Assinar e executar troca');
        assert(byId('wallet-form').hidden, 'the selected wallet stays open throughout the swap review');
    }
    assert(byId('exchange-review').textContent.includes(d.outputs[0].address));
    assert.equal(byId('sign-exchange').disabled, true, 'backup and review confirmation are required');
    byId('download-backup').click(); byId('backup-confirmed').checked = true; byId('backup-confirmed').dispatchEvent(new w.Event('change'));
    assert.equal(byId('sign-exchange').disabled, false); byId('sign-exchange').click(); await wait(() => captured !== null);
    assert.equal(captured.length, fixture.provider ? 2 : ownPool || d.operation === 'lock' || d.operation === 'close' ? d.inputs.length : d.userInputCount);
    if (d.operation === 'lock' || d.operation === 'close') assert.equal(submittedUrl, '/submitReservesUrl');
    if (ownPool) {
        assert.equal(submittedUrl, '/submitSwapUrl', 'the same wallet sends all signatures directly');
        assert.equal(byId('request-wait').hidden, true, 'no separate provider approval is requested');
        assert.equal(w.sessionStorage.getItem('povix-dex-swap-request'), null);
    } else if (d.operation === 'swap' && !fixture.provider) { await wait(() => !byId('request-wait').hidden); assert(byId('request-wait').textContent.includes('Aguardando assinatura do provedor')); }
    fs.writeFileSync(path.join(directory, 'exchange-browser-result.json'), JSON.stringify(captured));
    dom.window.close();
    console.log('PASS Web Crypto liquidity payload, tampering rejection, real form and wallet selection');
})().catch(error => { console.error(error); process.exitCode = 1; });
