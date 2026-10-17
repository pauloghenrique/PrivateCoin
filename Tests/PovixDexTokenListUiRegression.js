'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { JSDOM } = require('../work/liquidity-tools/node_modules/jsdom');

(async () => {
    const markup = fs.readFileSync('Povix.Dex/Views/Tokens/Index.cshtml', 'utf8');
    const html = markup.replace('@Html.AntiForgeryToken()', '<input name="__RequestVerificationToken" value="test-csrf">')
        .replace('data-tokens-url="@Url.Action("WalletTokens", "TokenProfiles")"', 'data-tokens-url="/profiles"')
        .replace('data-edit-url="@Url.Action("Index", "TokenProfiles")"', 'data-edit-url="/tokens/editar"');
    const dom = new JSDOM(html, { url: 'https://dex.test/tokens', runScripts: 'outside-only' });
    const w = dom.window, byId = id => w.document.getElementById(id);
    let unlocked = false, key = 'wallet-a', reject = false, calls = 0, resolvePending = null;
    const first = { tokenId: 'a'.repeat(64), name: '<img src=x onerror=alert(1)>', symbol: 'ABC', supplyAtomic: '12345', decimals: 2, description: '<script>unsafe</script>', renounced: false };
    const second = { ...first, tokenId: 'b'.repeat(64), name: 'Segundo', renounced: true };
    let result = [first, second], defer = false;
    w.PovixTokenWallet = { isUnlocked: () => unlocked, publicKeys: () => [key], formatAtomic: (value, decimals) => { assert.equal(decimals, 2); return value === '12345' ? '123,45' : value; } };
    w.fetch = async (url, options) => {
        calls++; assert.equal(url, '/profiles'); assert.equal(options.credentials, 'same-origin');
        const body = new URLSearchParams(options.body); assert.equal(body.get('__RequestVerificationToken'), 'test-csrf'); assert.equal(body.get('publicKeys'), key);
        if (defer) return new Promise(resolve => { resolvePending = resolve; });
        return { ok: !reject, json: async () => reject ? { error: 'Rede indisponível' } : result };
    };
    w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-token-list.js', 'utf8'));
    const settle = () => new Promise(resolve => setImmediate(resolve));
    const changed = () => w.document.dispatchEvent(new w.Event('povix-wallet-change'));
    assert.equal(calls, 0); assert.equal(byId('token-list').hidden, true); assert.equal(byId('token-list-connect').hidden, false);
    unlocked = true; changed(); await settle();
    assert.equal(byId('token-list').children.length, 2); assert.equal(byId('token-list-state').textContent, '2 tokens criados.');
    assert.equal(byId('token-list').querySelector('img'), null); assert.equal(byId('token-list').querySelector('script'), null);
    assert(byId('token-list').textContent.includes('123,45 ABC')); assert(byId('token-list').textContent.includes('Edição renunciada'));
    const links = byId('token-list').querySelectorAll('a'); assert.equal(links.length, 1);
    assert.equal(new URL(links[0].href).searchParams.get('tokenId'), first.tokenId);
    assert.equal(new URL(links[0].href).origin, 'https://dex.test');
    reject = true; byId('token-list-refresh').click(); await settle();
    assert.equal(byId('token-list-message').textContent, 'Rede indisponível'); assert.equal(byId('token-list').children.length, 2, 'failed refresh preserves current wallet list');
    reject = false; defer = true; byId('token-list-refresh').click(); await settle();
    const resolveOld = resolvePending;
    unlocked = false; changed();
    assert.equal(byId('token-list').children.length, 0); assert.equal(byId('token-list-refresh').disabled, true);
    resolveOld({ ok: true, json: async () => [first] }); await settle();
    assert.equal(byId('token-list').children.length, 0, 'late response cannot restore disconnected wallet data');
    unlocked = true; defer = false; key = 'wallet-b'; result = []; changed(); await settle();
    assert(byId('token-list-state').textContent.includes('ainda não criou')); assert.equal(byId('token-list').hidden, true);
    assert.equal(byId('token-list-connect').hidden, true); assert.equal(byId('token-list-refresh').disabled, false);
    assert.match(markup, /href="@Url.Action\("Create", "Tokens"\)">Criar token/);
    assert.match(markup, /href="@Url.Action\("Index", "TokenProfiles"\)">Editar token/);
    const layout = fs.readFileSync('Povix.Dex/Views/Shared/_Layout.cshtml', 'utf8');
    assert.match(layout, /href="@Url.Action\("Index", "Tokens"\)">Token<\/a>/);
    assert(!layout.includes('@Url.Action("Create", "Tokens")'), 'creation is accessed from Token page');
    assert(!layout.includes('@Url.Action("Index", "TokenProfiles")'), 'editing is accessed from Token page');
    dom.window.close();
    console.log('PASS Token page: wallet list, safe metadata, editing links, renunciation, failed refresh, stale responses, disconnection, empty wallet and menu actions.');
})().catch(error => { console.error(error); process.exitCode = 1; });
