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
    const integrated = d.operation === 'swap';
    let html = integrated ? fs.readFileSync('Povix.Dex/Views/Liquidity/Market.cshtml', 'utf8').replace('@Html.Partial("_PoolTrading", Model)', fs.readFileSync('Povix.Dex/Views/Liquidity/_PoolTrading.cshtml', 'utf8')) : fs.readFileSync('Povix.Dex/Views/Liquidity/Index.cshtml', 'utf8');
    html = html.replace('@Html.Partial("_TransactionReview")', fs.readFileSync('Povix.Dex/Views/Liquidity/_TransactionReview.cshtml', 'utf8'));
    html = (html + fs.readFileSync('Povix.Dex/Views/Shared/_Wallet.cshtml', 'utf8'))
        .replace('@Html.AntiForgeryToken()', '<input name="__RequestVerificationToken" value="test-csrf" type="hidden">')
        .replace(/data-([a-z-]+)="@Url.Action\([\s\S]*?\)"/g, (_, name) => 'data-' + name + '="/' + name + '"')
        .replace('@Model.NetworkId', fixture.networkId).replace(/@ViewBag.SelectedPoolId/g, d.poolId || '');
    const dom = new JSDOM(html, { url: integrated ? 'https://localhost/Liquidity/Market?poolId=' + d.poolId : 'https://localhost/liquidez', runScripts: 'outside-only', pretendToBeVisual: true, virtualConsole: new VirtualConsole() });
    const w = dom.window, byId = id => w.document.getElementById(id), intervals = [];
    Object.defineProperty(w, 'crypto', { value: webcrypto }); w.TextEncoder = TextEncoder; w.TextDecoder = TextDecoder;
    w.HTMLElement.prototype.scrollIntoView = () => {};
    w.URL.createObjectURL = () => 'blob:https://localhost/test-backup'; w.URL.revokeObjectURL = () => {};
    w.setInterval = (callback, delay) => { intervals.push({ callback, delay }); return intervals.length; };
    w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-token-wallet.js', 'utf8'));
    const wallet = w.PovixTokenWallet;
    const native = { id: 'povix' }, token = { id: 'a'.repeat(64) };
    const percent = { basisPoints: 1000, fixedAAtomic: '0', fixedBAtomic: '0' };
    const ownQuote = wallet.quotePoolSwap('1000', '1000', '100', native, token, 0, percent);
    assert.equal(ownQuote.inputBurnAtomic, '0'); assert.equal(ownQuote.outputBurnAtomic, '9');
    assert.equal(ownQuote.receiveAtomic, '81'); assert.equal(ownQuote.inputDebitAtomic, '100');
    const reverseQuote = wallet.quotePoolSwap('1000', '1000', '100', native, token, 1, percent);
    assert.equal(reverseQuote.inputBurnAtomic, '10'); assert.equal(reverseQuote.outputBurnAtomic, '0');
    assert.equal(reverseQuote.inputDebitAtomic, '110');
    assert.equal(wallet.quotePoolSwap('1000', '1000', '100', native, token, 0, null).receiveAtomic, '90');
    assert.throws(() => wallet.quotePoolSwap('1000', '1000', '100', native, token, 0, { ...percent, fixedAAtomic: '1' }));
    assert.throws(() => wallet.quotePoolSwap('1000', '1000', '100', native, token, 0, { ...percent, basisPoints: 10000 }));
    const recordText = JSON.stringify(fixture.wallet);
    await wallet.importFile({ size: Buffer.byteLength(recordText), arrayBuffer: async () => new TextEncoder().encode(recordText).buffer }, 'dex-test-password-only');
    const previousPool = fixture.catalogue.pools.find(pool => pool.poolId === d.poolId);
    const expected = { launch: d.operation === 'pool' ? d.launch : previousPool?.launch, burnEnabled: d.burnEnabled, burnPolicy: d.burnPolicy, loyaltyPolicy: d.loyaltyPolicy, rewardMode: d.rewardMode, allowUserLiquidity: d.allowUserLiquidity, participation: previousPool?.participation, sharesAtomic: d.sharesAtomic, operation: d.operation, assetA: d.assetA, assetB: d.assetB, reserveAAtomic: d.reserveAAtomic,
        reserveBAtomic: d.reserveBAtomic, feeAtomic: d.feeAtomic, changeAddress: d.changeAddress, poolId: d.poolId,
        reserveTransactionId: d.reserveTransactionId, providerAddress: d.providerAddress, inputSide: d.inputSide,
        amountAtomic: d.amountAtomic, minimumReceiveAtomic: d.minimumReceiveAtomic };
    await wallet.verifyExchangeDraft(d, expected, fixture.networkId);
    const bad = change => { const draft = JSON.parse(JSON.stringify(d)); change(draft); return draft; };
    for (const alteration of [draft => { draft.outputs[0].address = '0'.repeat(64); },
        draft => { draft.outputs[0].amountAtomic = String(BigInt(draft.outputs[0].amountAtomic) + 1n); },
        draft => { draft.signingPayload = Buffer.from('tampered').toString('base64'); },
        draft => { draft.inputs[0].transactionId = '0'.repeat(64); },
        draft => { draft.expiresUtc = '2000-01-01T00:00:00Z'; },
        draft => { draft.assetA.id = draft.assetB.id; },
        draft => { draft.feeAtomic = '0'; }, draft => { draft.transactionKind = 4; }])
        await assert.rejects(wallet.verifyExchangeDraft(bad(alteration), expected, fixture.networkId));
    await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.burnEnabled = !Boolean(draft.burnEnabled); }), expected, fixture.networkId));
    if (d.burnEnabled) await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.burnPolicy.basisPoints = 9999; }), expected, fixture.networkId));
    if (d.operation === 'swap' && d.burnEnabled) await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.outputBurnAtomic = '0'; }), expected, fixture.networkId));
    if (d.operation === 'pool') await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.rewardMode = d.rewardMode === 'loyalty' ? 'fees' : 'loyalty'; }), expected, fixture.networkId));
    if (d.operation === 'pool' && d.loyaltyPolicy) for (const mutation of [policy => { policy.feeBasisPoints++; }, policy => { policy.minimumReserveAtomic = '1'; }, policy => { policy.earlyExit = policy.earlyExit === 'locked' ? 'forfeit' : 'locked'; }]) await assert.rejects(wallet.verifyExchangeDraft(bad(draft => mutation(draft.loyaltyPolicy)), expected, fixture.networkId));
    if (d.launch) for (const change of [draft => { draft.launch.targetAtomic = String(BigInt(draft.launch.targetAtomic) + 1n); }, draft => { draft.launch.contributors[0].address = 'f'.repeat(64); }, draft => { draft.launch.deadlineHeight++; }]) await assert.rejects(wallet.verifyExchangeDraft(bad(change), expected, fixture.networkId));
    if (d.allowUserLiquidity) {
        await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.participation.holders[0].sharesAtomic = '1'; }), expected, fixture.networkId));
        await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.allowUserLiquidity = false; }), expected, fixture.networkId));
    }
    if (d.operation === 'swap') {
        assert.equal(wallet.quotePoolSwap(d.inputSide === 0 ? d.reserveAAtomic : d.reserveBAtomic, d.inputSide === 0 ? d.reserveBAtomic : d.reserveAAtomic, d.amountAtomic, d.assetA, d.assetB, d.inputSide, d.burnPolicy).receiveAtomic, d.receiveAtomic);
        await assert.rejects(wallet.verifyExchangeDraft(bad(draft => { draft.inputs[draft.userInputCount].outputIndex = 2; }), expected, fixture.networkId));
        await assert.rejects(wallet.verifyExchangeDraft(d, { ...expected, minimumReceiveAtomic: String(BigInt(d.receiveAtomic) + 1n) }, fixture.networkId));
    }
    assert(BigInt(wallet.quoteExchange('4611686018427387903', '9223372036854775807', '4611686018427387903')) > 0n);
    const app = byId('liquidity-app');
    for (const key of ['networkUrl', 'catalogueUrl', 'prepareLaunchPoolUrl', 'prepareLaunchOperationUrl', 'submitLaunchUrl', 'preparePoolUrl', 'prepareSwapUrl', 'prepareLiquidityUrl', 'submitLiquidityUrl', 'prepareReservesUrl', 'submitReservesUrl', 'submitPoolUrl', 'submitSwapUrl']) app.dataset[key] = '/' + key;
    if (integrated) for (const key of ['prepareLaunchPoolUrl', 'prepareLaunchOperationUrl', 'submitLaunchUrl']) delete app.dataset[key];
    app.dataset.marketUrl = '/Liquidity/Market?poolId=__id__';
    app.dataset.networkId = fixture.networkId; app.dataset.receiptUrl = '/liquidez/operacao/__id__'; app.dataset.statusUrl = '/status/__id__';
    const lifecyclePool = d.launch ? null : fixture.catalogue.pools.find(pool => pool.available && (!integrated || pool.poolId === d.poolId));
    let poolPhase = lifecyclePool ? 'pending' : 'available';
    if (lifecyclePool) app.dataset.poolId = lifecyclePool.poolId;
    let captured = null, preparations = 0, submittedUrl = null, attempts = 0, firstSignatures = null, statusCalls = 0, statusPhase = 'pending', statusFailing = false;
    const originalLocation = w.location.href;
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
        else if (url === '/prepareLaunchPoolUrl' || url === '/prepareLaunchOperationUrl') { preparations++; result = d; assert.equal(body.get('FeeAtomic'), d.feeAtomic); }
        else if (url === '/prepareLiquidityUrl') { preparations++; result = d; assert.equal(body.get('Shares'), d.sharesAtomic); assert.equal(body.get('Operation'), d.operation); }
        else if (url === '/preparePoolUrl' || url === '/prepareSwapUrl') { preparations++; result = d; assert.equal(body.get('FeeAtomic'), d.feeAtomic); if (d.operation === 'pool') { assert.equal(body.get('RewardMode'), d.rewardMode || 'fees'); assert.equal(body.get('BurnEnabled'), String(Boolean(d.burnEnabled))); if (d.burnEnabled) { assert.equal(body.get('BurnMode'), d.burnPolicy.basisPoints ? 'percent' : 'fixed'); } } }
        else if (url === '/prepareReservesUrl') {
            preparations++; result = d; assert.equal(body.get('feeAtomic'), d.feeAtomic);
            assert.equal(body.get('operation'), d.operation); assert.equal(body.get('poolId'), d.poolId);
        }
        else if (url === '/submitLaunchUrl' || url === '/submitLiquidityUrl' || url === '/submitReservesUrl' || url === '/submitPoolUrl' || url === '/submitSwapUrl') {
            assert.equal(body.get('draftId'), d.draftId); attempts++;
            const currentSignatures = body.getAll('signatures');
            if (integrated && attempts === 1) {
                firstSignatures = currentSignatures;
                return { ok: false, status: 503, json: async () => ({ error: 'Envio indisponível' }) };
            }
            if (integrated) assert.deepEqual(currentSignatures, firstSignatures, 'retry reuses the exact signed transaction');
            captured = currentSignatures; submittedUrl = url;
            result = { transactionId: 'a'.repeat(64), receiptUrl: '/liquidez/operacao/' + 'a'.repeat(64) };
        } else if (new URL(url, w.location.origin).pathname === '/status/' + 'a'.repeat(64)) {
            statusCalls++;
            if (statusFailing) return { ok: false, status: 503 };
            const output = d.inputSide === 0 ? d.assetB : d.assetA;
            result = { status: statusPhase, poolId: d.poolId, blockHeight: statusPhase === 'confirmed' ? 42 : null,
                blockHash: statusPhase === 'confirmed' ? 'b'.repeat(64) : null, confirmations: statusPhase === 'confirmed' ? 2 : 0,
                feeAtomic: d.feeAtomic, receive: { amountAtomic: d.receiveAtomic, symbol: output.symbol, decimals: output.decimals }, peerCount: 2 };
        }
        else throw new Error('Unexpected endpoint: ' + url);
        return { ok: true, status: 200, json: async () => result };
    };
    w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8'));
    const wait = async predicate => { for (let i = 0; i < 250 && !predicate(); i++) await new Promise(resolve => setTimeout(resolve, 20)); assert(predicate(), byId('app-message').textContent || 'UI timeout'); };
    await wait(() => byId('pool-asset-a').options.length > 1);
    // The selected wallet was imported before the page controller starts.
    w.document.dispatchEvent(new w.Event('povix-wallet-change'));
    await intervals.find(timer => timer.delay === 10000).callback(); await wait(() => !byId('prepare-pool').disabled);
    if (lifecyclePool) {
        const selected = () => byId('swap-pool').options[byId('swap-pool').selectedIndex];
        assert.equal(byId('swap-pool').value, lifecyclePool.poolId);
        assert(selected().disabled); assert(byId('prepare-swap').disabled);
        assert(byId('swap-pool-state').textContent.includes('aguarda validação'));
        poolPhase = 'validated'; await intervals.find(timer => timer.delay === 10000).callback();
        assert(selected().disabled); assert(byId('prepare-swap').disabled);
        assert(byId('swap-pool-state').textContent.includes('validada'));
        poolPhase = 'available'; await intervals.find(timer => timer.delay === 10000).callback();
        assert.equal(byId('swap-pool').value, lifecyclePool.poolId, 'confirmation keeps the newly created pool selected');
        assert.equal(selected().disabled, false); assert.equal(byId('prepare-swap').disabled, false);
        assert.equal(wallet.isUnlocked(), true, 'confirmation does not require choosing the wallet again');
    }
    for (const pool of fixture.catalogue.pools.filter(pool => pool.closureRenounced && pool.available)) {
        const market = [...byId('pool-list').querySelectorAll('a')].find(link => link.href.includes('/Liquidity/Market') && link.href.includes(pool.poolId));
        assert(market, 'renounced pool retains its market link');
        assert.notEqual(market.nextElementSibling?.tagName, 'BUTTON', 'renunciation removes the closure action');
        assert(byId('pool-list').textContent.includes(pool.managementMessage), 'the pool explains the renunciation');
    }
    byId('pool-form').requestSubmit(); assert.equal(preparations, 0, 'empty fields must not prepare a transaction');
    if (d.launch && d.operation === 'pool') {
        byId('launch-token').value = d.assetA.id; byId('launch-funding-asset').value = d.assetB.id;
        byId('launch-sale').value = wallet.formatAtomic(d.launch.saleTokensAtomic, d.assetA.decimals);
        byId('launch-liquidity').value = wallet.formatAtomic(BigInt(d.launch.totalTokensAtomic) - BigInt(d.launch.saleTokensAtomic), d.assetA.decimals);
        byId('launch-target').value = wallet.formatAtomic(d.launch.targetAtomic, d.assetB.decimals);
        byId('launch-seed').value = wallet.formatAtomic(d.launch.raisedAtomic, d.assetB.decimals);
        byId('launch-deadline').value = String(d.launch.deadlineHeight);
        byId('launch-user-liquidity').checked = d.launch.allowUserLiquidity;
        byId('launch-create-form').dispatchEvent(new w.Event('input'));
        assert.equal(byId('prepare-launch-create').disabled, false);
        byId('launch-create-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    } else if (['launch-contribute', 'launch-refund'].includes(d.operation)) {
        byId('launch-pool').value = d.poolId; byId('launch-operation').value = d.operation === 'launch-refund' ? 'refund' : 'contribute';
        byId('launch-amount').value = d.operation === 'launch-refund' ? '' : wallet.formatAtomic(d.amountAtomic, d.assetB.decimals);
        byId('launch-contribute-form').dispatchEvent(new w.Event('input'));
        assert.equal(byId('prepare-launch-operation').disabled, false);
        byId('launch-contribute-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    } else if (d.operation === 'pool') {
        byId('pool-reward-mode').value = d.rewardMode || 'fees';
        byId('pool-form').dispatchEvent(new w.Event('change'));
        assert.equal(byId('pool-reward-title').textContent, d.rewardMode === 'loyalty' ? 'Bônus por permanência' : 'Participação nas taxas');
        if (d.rewardMode === 'loyalty') {
            assert(byId('pool-reward-detail').textContent.includes('ainda não gera pagamentos'));
            assert.equal(byId('pool-loyalty-policy').hidden, false);
            byId('pool-loyalty-fee').value = wallet.formatAtomic(d.loyaltyPolicy.feeBasisPoints, 2);
            byId('pool-loyalty-minimum').value = wallet.formatAtomic(d.loyaltyPolicy.minimumReserveAtomic, 8);
            byId('pool-loyalty-exit').value = d.loyaltyPolicy.earlyExit;
        } else assert.equal(byId('pool-loyalty-policy').hidden, true);
        assert.equal(byId('pool-burn-enabled').checked, false, 'pool burn is opt-in');
        byId('pool-burn-enabled').checked = Boolean(d.burnEnabled);
        if (d.burnEnabled) {
            byId('pool-burn-mode').value = d.burnPolicy.basisPoints ? 'percent' : 'fixed';
            byId('pool-burn-percent').value = wallet.formatAtomic(d.burnPolicy.basisPoints, 2);
            byId('pool-burn-fixed-a').value = wallet.formatAtomic(d.burnPolicy.fixedAAtomic, d.assetA.decimals);
            byId('pool-burn-fixed-b').value = wallet.formatAtomic(d.burnPolicy.fixedBAtomic, d.assetB.decimals);
        }
        byId('pool-user-liquidity').checked = Boolean(d.allowUserLiquidity);
        byId('pool-asset-a').value = d.assetA.id; byId('pool-asset-b').value = d.assetB.id;
        byId('pool-amount-a').value = wallet.formatAtomic(d.reserveAAtomic, d.assetA.decimals);
        byId('pool-amount-b').value = wallet.formatAtomic(d.reserveBAtomic, d.assetB.decimals);
        assert.equal(byId('pool-asset-a').value, d.assetA.id, 'first asset is selected');
        assert.equal(byId('pool-asset-b').value, d.assetB.id, 'second asset is selected');
        byId('pool-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    } else if (['add', 'remove'].includes(d.operation)) {
        const pool = previousPool, total = BigInt(pool.participation.totalSharesAtomic), shares = BigInt(d.sharesAtomic);
        byId('user-liquidity-pool').value = d.poolId;
        byId('user-liquidity-operation').value = d.operation;
        byId('user-liquidity-amount').value = d.operation === 'add' ? wallet.formatAtomic((BigInt(d.reserveAAtomic) * shares + total - 1n) / total, d.assetA.decimals) : '100';
        byId('user-liquidity-form').dispatchEvent(new w.Event('input'));
        assert.equal(byId('prepare-user-liquidity').disabled, false);
        byId('user-liquidity-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    } else if (d.operation === 'lock' || d.operation === 'close') {
        const pool = fixture.catalogue.pools.find(pool => pool.poolId === d.poolId);
        assert(pool.canManage); assert.equal(pool.locked, d.operation === 'close');
        const button = [...byId('pool-list').querySelectorAll('button')].find(button =>
            button.previousElementSibling.href.includes(d.poolId));
        assert(button); button.click();
    } else {
        byId('swap-pool').value = d.poolId; byId('swap-side').value = String(d.inputSide);
        byId('swap-side').dispatchEvent(new w.Event('input', { bubbles: true }));
        const input = d.inputSide === 0 ? d.assetA : d.assetB, output = d.inputSide === 0 ? d.assetB : d.assetA;
        byId('swap-amount').value = wallet.formatAtomic(d.amountAtomic, input.decimals);
        byId('swap-minimum').value = wallet.formatAtomic(d.minimumReceiveAtomic, output.decimals);
        byId('swap-form').dispatchEvent(new w.Event('change'));
        assert(byId('swap-estimate').textContent.includes(wallet.formatAtomic(d.receiveAtomic, output.decimals)));
        byId('swap-form').dispatchEvent(new w.Event('submit', { cancelable: true }));
    }
    await wait(() => !byId('sign-actions').hidden && !byId('sign-exchange').disabled);
    if (d.operation === 'swap') {
        assert.equal(byId('review-title').textContent, 'Executar troca');
        assert(byId('review-description').textContent.includes('carteira selecionada'));
        assert.equal(byId('sign-exchange').textContent, 'Confirmar troca');
        assert(wallet.isUnlocked(), 'the selected wallet stays open throughout the swap review');
    }
    assert(byId('exchange-review').textContent.includes(d.outputs[0].address));
    assert.equal(byId('sign-exchange').disabled, false, 'connected wallet confirms without a backup');
    assert.equal(byId('download-backup'), null); assert.equal(byId('backup-confirmed'), null);
    await wait(() => !byId('sign-exchange').disabled); byId('sign-exchange').click();
    if (integrated) {
        await wait(() => byId('app-message').textContent.includes('Repita o mesmo envio') && !byId('sign-exchange').disabled);
        assert.equal(preparations, 1); assert.equal(byId('sign-actions').hidden, false);
        byId('sign-exchange').click();
    }
    await wait(() => captured !== null);
    assert.equal(captured.length, ['swap', 'add', 'remove', 'launch-contribute', 'launch-refund'].includes(d.operation) ? d.userInputCount : d.inputs.length);
    if (d.operation === 'lock' || d.operation === 'close') assert.equal(submittedUrl, '/submitReservesUrl');
    if (integrated) {
        assert.equal(submittedUrl, '/submitSwapUrl', 'all swaps submit directly using the buyer wallet');
        await wait(() => byId('exchange-status-state').textContent === 'Pendente' && statusCalls > 0);
        assert.equal(w.location.href, originalLocation, 'the swap stays on Liquidity/Market');
        assert.equal(byId('exchange-status').hidden, false);
        assert.equal(wallet.isUnlocked(), true, 'the selected wallet remains available');
        assert.equal(byId('provider-requests'), null);
        assert.equal(w.sessionStorage.getItem('povix-dex-market-receipt:' + d.poolId), 'a'.repeat(64));
        const refreshStatus = intervals.find(timer => timer.delay === 5000).callback;
        statusPhase = 'validated'; await refreshStatus();
        assert.equal(byId('exchange-status-state').textContent, 'Validada');
        assert.equal(byId('exchange-status-confirmations').textContent, '0');
        statusPhase = 'confirmed'; await refreshStatus();
        assert.equal(byId('exchange-status-title').textContent, 'Troca confirmada.');
        assert.equal(byId('exchange-status-confirmations').textContent, '2');
        assert.equal(byId('exchange-status-block').textContent, '42');
        assert.equal(byId('exchange-status-hash').textContent, 'b'.repeat(64));
        statusFailing = true; await refreshStatus();
        assert.equal(byId('exchange-status-state').textContent, 'Confirmada');
        assert.match(byId('exchange-status-connectivity').textContent, /indisponível/);
        statusFailing = false; statusPhase = 'pending'; await refreshStatus();
        assert.equal(byId('exchange-status-confirmations').textContent, '0', 'reorganization clears old confirmations');
        assert.equal(byId('exchange-status-hash').textContent, '—');
        assert.equal(byId('exchange-status-block').textContent, 'Aguardando confirmação');
        statusPhase = 'rejected'; await refreshStatus(); assert.equal(byId('exchange-status-state').textContent, 'Não confirmada');
        Object.defineProperty(w.document, 'hidden', { value: true, configurable: true });
        const priorCalls = statusCalls; await refreshStatus(); assert.equal(statusCalls, priorCalls, 'hidden pages pause receipt polling');
        Object.defineProperty(w.document, 'hidden', { value: false, configurable: true });
        statusPhase = 'confirmed'; w.document.dispatchEvent(new w.Event('visibilitychange'));
        await wait(() => byId('exchange-status-state').textContent === 'Confirmada');
        const restored = new JSDOM(html, { url: originalLocation, runScripts: 'outside-only', pretendToBeVisual: true });
        restored.window.TextEncoder = TextEncoder; restored.window.TextDecoder = TextDecoder;
        Object.defineProperty(restored.window, 'crypto', { value: webcrypto });
        restored.window.setInterval = () => 1; restored.window.fetch = w.fetch;
        restored.window.sessionStorage.setItem('povix-dex-market-receipt:' + d.poolId, 'a'.repeat(64));
        restored.window.eval(fs.readFileSync('Povix.Dex/Scripts/povix-token-wallet.js', 'utf8'));
        restored.window.document.getElementById('liquidity-app').dataset.statusUrl = '/status/__id__';
        restored.window.document.getElementById('liquidity-app').dataset.receiptUrl = '/liquidez/operacao/__id__';
        for (const key of ['networkUrl', 'catalogueUrl']) restored.window.document.getElementById('liquidity-app').dataset[key] = '/' + key;
        restored.window.eval(fs.readFileSync('Povix.Dex/Scripts/povix-liquidity.js', 'utf8'));
        assert.equal(restored.window.document.getElementById('exchange-status').hidden, true, 'Old receipts stay hidden on reload');
        assert.equal(restored.window.sessionStorage.getItem('povix-dex-market-receipt:' + d.poolId), null);
        assert.equal(restored.window.PovixTokenWallet.isUnlocked(), false);
        restored.window.close();
    }
    fs.writeFileSync(path.join(directory, 'exchange-browser-result.json'), JSON.stringify(captured));
    dom.window.close();
    console.log('PASS Web Crypto liquidity payload, tampering rejection, real form and wallet selection');
})().catch(error => { console.error(error); process.exitCode = 1; });
