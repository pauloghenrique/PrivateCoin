'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class Element {
    constructor() { this.children = []; this.textContent = ''; this.listeners = {}; this.value = '300'; this.hidden = false; }
    append(...nodes) { this.children.push(...nodes); }
    appendChild(node) { this.append(node); }
    replaceChildren(...nodes) { this.children = nodes; }
    setAttribute(key, value) { this[key] = value; }
    removeAttribute(key) { delete this[key]; }
    addEventListener(event, callback) { this.listeners[event] = callback; }
}
(async () => {
    const ids = {}, timers = new Map(); let sequence = 0, calls = 0, failing = false;
    const element = id => ids[id] || (ids[id] = new Element());
    // Model SVG visibility as an attribute, independent from an HTML hidden property.
    element('market-candles').setAttribute('hidden', '');
    element('pool-market').dataset = { url: '/Liquidity/MarketData?poolId=test' };
    const data = { pool: { assetA: { id: 'povix', symbol: 'POVIX', decimals: 8 }, assetB: { id: 'tkn', symbol: 'TKN', decimals: 2 }, reserveAAtomic: '100000000', reserveBAtomic: '10000', status: 'available', statusMessage: 'Disponível' }, price: 0.01, interval: 300, updatedUtc: new Date().toISOString(), peerCount: 2, ledgerTotal: 1,
        candles: [{ time: 1700000000, open: 0.01, high: 0.0105, low: 0.0095, close: 0.0101 }], ledger: [{ time: 1700000000, operation: 'swap', status: 'pending', deltaAAtomic: '123456789012345678', deltaBAtomic: '-100', transactionId: '<script>bad</script>', blockHeight: null }] };
    const document = { hidden: false, getElementById: element, createElement: () => new Element(), createElementNS: () => new Element(), addEventListener: () => {} };
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-pool-market.js', 'utf8'), { document, window: { addEventListener: () => {} }, location: { href: 'https://example.test/' }, URL, AbortController, setTimeout: callback => { timers.set(++sequence, callback); return sequence; }, clearTimeout: key => timers.delete(key), fetch: async url => { calls++; assert.equal(new URL(url).searchParams.get('interval'), element('market-interval').value); return { ok: !failing, status: 503, json: async () => data }; } });
    const settle = () => new Promise(resolve => setImmediate(resolve)); await settle();
    assert.equal(element('market-pair').textContent, 'POVIX / TKN');
    assert.equal(element('market-price').textContent, '1 POVIX = 0,01 TKN');
    assert.equal(element('market-reserve-b').textContent, '100,00 TKN');
    assert.equal(element('market-token-info').children.length, 1);
    assert.equal(element('market-label-a').textContent, 'Reserva de POVIX');
    assert.equal(element('market-label-b').textContent, 'Reserva de TKN');
    assert.equal(element('market-connection')['data-state'], 'ready');
    assert.equal(element('market-token-info').children[0].children[0].textContent, 'POVIX (POVIX)');
    const tokenFields = () => element('market-token-info').children[0].children[1].children.map(item => item.textContent);
    assert.deepEqual(tokenFields().filter((_, index) => index % 2 === 1), Array(4).fill('Não disponível'));
    data.tokens = [
        { id: 'tkn', name: 'Token B', symbol: 'TKN', decimals: 2, supplyAtomic: '999' },
        { id: 'povix', name: 'POVIX', symbol: 'POVIX', decimals: 8, supplyAtomic: '123456789012345678', circulatingSupplyAtomic: '100000000', holderCount: 0, createdUtc: '2026-10-01T12:00:00Z' }
    ];
    await element('market-refresh').listeners.click();
    assert.equal(element('market-token-info').children.length, 1, 'Only token A is displayed even when tokens arrive reordered');
    assert.deepEqual(tokenFields().slice(0, 6), ['Supply em circulação', '1,00000000 POVIX', 'Supply total', '1234567890,12345678 POVIX', 'Número de holders', '0']);
    assert(!tokenFields().includes('Token B'));
    assert.notEqual(tokenFields()[7], 'Não disponível');
    data.tokens[1].createdUtc = 'invalid';
    data.tokens[1].circulatingSupplyAtomic = '0';
    await element('market-refresh').listeners.click();
    assert.equal(tokenFields()[1], '0,00000000 POVIX');
    assert.equal(tokenFields()[3], '1234567890,12345678 POVIX', 'Total retains locked amounts when circulating supply is zero');
    assert.equal(tokenFields()[7], 'Não disponível');

    const trading = fs.readFileSync('Povix.Dex/Views/Liquidity/_PoolTrading.cshtml', 'utf8');
    const market = fs.readFileSync('Povix.Dex/Views/Liquidity/Market.cshtml', 'utf8');
    assert.match(market, /Html.Partial\("_PoolTrading", Model\)/);
    assert.match(trading, /data-integrated="true"/);
    assert.match(trading, /id="swap-form"/);
    const markupIds = [...(trading + market).matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
    assert.equal(new Set(markupIds).size, markupIds.length, 'market and trading IDs must be unique');
    const row = element('market-ledger').children[0];
    assert.equal(row.children[1].textContent, 'Troca · POVIX → TKN');
    assert.equal(element('market-heading-a').textContent, 'Carteira POVIX');
    assert.equal(row.children[3].textContent, '−1234567890,12345678');
    assert.equal(row.children[4].textContent, '+1,00');
    assert.equal(row.children[6].textContent, '<script>bad</script>');
    assert.equal(row.children[6].title, '<script>bad</script>');
    assert.equal(row.children[2]['data-status'], 'pending');
    assert.equal(row.children[3].className, 'market-amount', 'pending movements must not imply confirmed reserve changes');
    assert.ok(element('market-candles').children.length > 0);
    assert.equal(Object.hasOwn(element('market-candles'), 'hidden'), false, 'loaded candles must remove the SVG hidden attribute');
    assert.equal(element('market-chart-empty').hidden, true);
    // Numeric strings must compare by price, and invalid records must not poison SVG coordinates.
    data.candles = [
        { time: 1700000300, open: '9', high: '10', low: '9', close: '10' },
        { time: 1700000000, open: '10', high: '10', low: '10', close: '10' },
        { time: 1700000600, open: 'bad', high: 'bad', low: 'bad', close: 'bad' }
    ];
    await element('market-refresh').listeners.click();
    const groups = element('market-candles').children.filter(node => node.children.length === 3);
    assert.equal(groups.length, 2);
    assert.equal(groups[1].children[2].fill, '#12836d', '10 is greater than 9 when prices arrive as strings');
    for (const group of groups) {
        for (const shape of group.children.slice(1)) {
            for (const key of ['x', 'y', 'x1', 'x2', 'y1', 'y2', 'width', 'height']) {
                if (shape[key] !== undefined) assert.ok(Number.isFinite(Number(shape[key])), 'SVG coordinates must be finite');
            }
        }
    }
    data.candles = [{ time: 1700000000, open: 0, high: 0, low: 0, close: 0 }];
    await element('market-refresh').listeners.click();
    assert.equal(element('market-candles').hidden, '', 'empty history must set the SVG hidden attribute');
    assert.match(element('market-chart-empty').textContent, /preços inválidos/);
    data.ledger[0].deltaAAtomic = '-14042055324596';
    data.ledger[0].deltaBAtomic = '20000000000000';
    data.pool.assetA.symbol = 'AUR'; data.pool.assetB.symbol = 'KIK'; data.pool.assetB.decimals = 8;
    await element('market-refresh').listeners.click();
    assert.equal(element('market-ledger').children[0].children[1].textContent, 'Troca · KIK → AUR');
    assert.equal(element('market-ledger').children[0].children[3].textContent, '+140420,55324596');
    assert.equal(element('market-ledger').children[0].children[4].textContent, '−200000,00000000');
    data.candles = []; data.ledger = []; data.pool.status = 'closed'; data.price = null;
    element('market-interval').value = '3600'; await element('market-interval').listeners.change();
    assert.equal(element('market-time-3600')['aria-pressed'], 'true');
    assert.equal(element('market-time-300')['aria-pressed'], 'false');
    await element('market-time-900').listeners.click();
    assert.equal(element('market-interval').value, '900');
    assert.equal(element('market-time-900')['aria-pressed'], 'true');
    assert.equal(element('market-candles').hidden, '', 'empty history must set the SVG hidden attribute');
    assert.equal(element('market-chart-empty').hidden, false);
    assert.match(element('market-chart-empty').textContent, /primeira troca confirmada/);
    assert.equal(element('market-ledger').children[0].children[0].colspan, '7');
    assert.match(element('market-ledger').children[0].children[0].textContent, /Nenhuma movimentação/);
    assert.equal(element('market-reserve-a').textContent, '0');
    failing = true; await element('market-refresh').listeners.click();
    assert.match(element('market-connection').textContent, /desatualizados/);
    assert.equal(element('market-connection')['data-state'], 'error');
    const previous = calls; document.hidden = true; await element('market-refresh').listeners.click(); assert.equal(calls, previous);
    console.log('PASS: candles, empty history, exact atomic ledger values, safe text, interval refresh, closed pool, network errors and hidden-page polling.');
})().catch(error => { console.error(error); process.exitCode = 1; });
