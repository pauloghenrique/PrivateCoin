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
    addEventListener(event, callback) { this.listeners[event] = callback; }
}
(async () => {
    const ids = {}, timers = new Map(); let sequence = 0, calls = 0, failing = false;
    const element = id => ids[id] || (ids[id] = new Element());
    element('pool-market').dataset = { url: '/Liquidity/MarketData?poolId=test' };
    const data = { pool: { assetA: { symbol: 'POVIX', decimals: 8 }, assetB: { symbol: 'TKN', decimals: 2 }, reserveAAtomic: '100000000', reserveBAtomic: '10000', status: 'available', statusMessage: 'Disponível' }, price: 100, interval: 300, updatedUtc: new Date().toISOString(), peerCount: 2, ledgerTotal: 1,
        candles: [{ time: 1700000000, open: 100, high: 105, low: 95, close: 101 }], ledger: [{ time: 1700000000, operation: 'swap', status: 'pending', deltaAAtomic: '123456789012345678', deltaBAtomic: '-100', transactionId: '<script>bad</script>', blockHeight: null }] };
    const document = { hidden: false, getElementById: element, createElement: () => new Element(), createElementNS: () => new Element(), addEventListener: () => {} };
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-pool-market.js', 'utf8'), { document, window: { addEventListener: () => {} }, location: { href: 'https://example.test/' }, URL, AbortController, setTimeout: callback => { timers.set(++sequence, callback); return sequence; }, clearTimeout: key => timers.delete(key), fetch: async url => { calls++; assert.equal(new URL(url).searchParams.get('interval'), element('market-interval').value); return { ok: !failing, status: 503, json: async () => data }; } });
    const settle = () => new Promise(resolve => setImmediate(resolve)); await settle();
    assert.equal(element('market-pair').textContent, 'POVIX / TKN');
    assert.equal(element('market-price').textContent, '100 TKN por POVIX');
    assert.equal(element('market-reserve-b').textContent, '100,00 TKN');
    assert.equal(element('market-token-info').children.length, 2);
    assert.equal(element('market-label-a').textContent, 'Reserva de POVIX');
    assert.equal(element('market-label-b').textContent, 'Reserva de TKN');
    assert.equal(element('market-connection')['data-state'], 'ready');
    assert.equal(element('market-token-info').children[0].children[0].textContent, 'POVIX (POVIX)');
    const trading = fs.readFileSync('Povix.Dex/Views/Liquidity/_PoolTrading.cshtml', 'utf8');
    const market = fs.readFileSync('Povix.Dex/Views/Liquidity/Market.cshtml', 'utf8');
    assert.match(market, /Html.Partial\("_PoolTrading", Model\)/);
    assert.match(trading, /data-integrated="true"/);
    assert.match(trading, /id="swap-form"/);
    const markupIds = [...(trading + market).matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
    assert.equal(new Set(markupIds).size, markupIds.length, 'market and trading IDs must be unique');
    const row = element('market-ledger').children[0];
    assert.equal(row.children[3].textContent, '+1234567890,12345678');
    assert.equal(row.children[4].textContent, '−1,00');
    assert.equal(row.children[6].textContent, '<script>bad</script>');
    assert.equal(row.children[6].title, '<script>bad</script>');
    assert.equal(row.children[2]['data-status'], 'pending');
    assert.equal(row.children[3].className, 'market-amount', 'pending movements must not imply confirmed reserve changes');
    assert.ok(element('market-candles').children.length > 0);
    data.candles = []; data.ledger = []; data.pool.status = 'closed'; data.price = null;
    element('market-interval').value = '3600'; await element('market-interval').listeners.change();
    assert.equal(element('market-time-3600')['aria-pressed'], 'true');
    assert.equal(element('market-time-300')['aria-pressed'], 'false');
    await element('market-time-900').listeners.click();
    assert.equal(element('market-interval').value, '900');
    assert.equal(element('market-time-900')['aria-pressed'], 'true');
    assert.equal(element('market-candles').hidden, true);
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
