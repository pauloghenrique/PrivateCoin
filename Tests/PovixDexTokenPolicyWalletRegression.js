'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { webcrypto } = require('node:crypto');
const { JSDOM } = require('../work/liquidity-tools/node_modules/jsdom');
const directory = process.argv[2];
const fixture = JSON.parse(fs.readFileSync(path.join(directory, 'policy-fixture.json'), 'utf8'));
(async () => {
    let html = fs.readFileSync('Povix.Dex/Views/Tokens/' + (fixture.mode === 'create' ? 'Create' : 'Manage') + '.cshtml', 'utf8');
    html = html.replace('@Html.AntiForgeryToken()', '<input name="__RequestVerificationToken" value="test-csrf" type="hidden">')
        .replace(/data-([a-z-]+)="@Url.Action\([\s\S]*?\)"/g, (_, name) => 'data-' + name + '="/' + name + '"').replace('@Model.Network.NetworkId', fixture.networkId).replace('@Model.NetworkId', fixture.networkId);
    // Razor-generated inputs use MVC helpers. Render only those helpers for DOM testing.
    html = html.replace('value="@priority"', 'value="2"').replace('data-fee="@i"', 'data-fee="1"').replace(/@Html.TextBoxFor\(m => m\.(\w+), new \{[^\n]*?\}\)/g, (_, id) => '<input id="' + id + '" name="' + id + '">')
        .replace(/@Html.DropDownListFor\(m => m.Decimals,[^\n]*?\)\)<small/g, '<select id="Decimals"><option value="0">0</option></select><small');
    html += '<span id="wallet-balance"></span><small id="wallet-balance-detail"></small>';
    const dom = new JSDOM(html, { url: 'https://localhost/liquidez/criar?tipo=token', runScripts: 'outside-only' });
    const w = dom.window, byId = id => w.document.getElementById(id);
    Object.defineProperty(w, 'crypto', { value: webcrypto }); w.TextEncoder = TextEncoder; w.TextDecoder = TextDecoder;
    w.HTMLElement.prototype.scrollIntoView = () => {}; w.setInterval = () => 1;
    w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-token-wallet.js', 'utf8'));
    const wallet = w.PovixTokenWallet, bytes = new TextEncoder().encode(JSON.stringify(fixture.wallet));
    await wallet.importFile({ size: bytes.length, arrayBuffer: async () => bytes.buffer }, 'dex-test-password-only');
    let signatures, creatorSignature = null, administratorSignatures = [];
    if (fixture.mode === 'create') {
        await wallet.verifyDraft(fixture.draft, fixture.expected, fixture.networkId, fixture.changeAddress);
        const altered = { ...fixture.draft, policyCanonical: fixture.draft.policyCanonical.replace('|1:1', '|1:0') };
        await assert.rejects(wallet.verifyDraft(altered, fixture.expected, fixture.networkId, fixture.changeAddress));
        await assert.rejects(wallet.verifyDraft(fixture.draft, { ...fixture.expected, PolicyValues: fixture.expected.PolicyValues.map((value, index) => index === 1 ? '201' : value) }, fixture.networkId, fixture.changeAddress));
        const p = fixture.expected.PolicyValues;
        for (const key of ['Name', 'Symbol', 'Supply', 'Decimals', 'DestinationAddress']) { assert(byId(key), key + ' rendered'); byId(key).value = fixture.expected[key]; }
        byId('AutoBurnMode').value = 'percent'; byId('AutoBurnValue').value = '1';
        byId('Mintable').value = 'true'; byId('MaximumSupply').value = p[1]; byId('Burnable').checked = true; byId('Pausable').checked = true;
        byId('AdministratorAddresses').value = p[4].replace(',', '\n'); byId('RequiredSignatures').value = p[5];
        for (const [id, index] of [['Description', 6], ['LogoUrl', 7], ['Website', 8], ['SocialUrl', 9], ['VestingStartHeight', 10], ['VestingIntervalBlocks', 11], ['VestingTranches', 12]]) byId(id).value = p[index];
        byId('vesting-enabled').value = 'true';
        const fees = ['1', fixture.draft.feeAtomic, '4']; let prepared = 0;
        w.fetch = async (url, options) => {
            if (url === '/network-url') return { ok: true, json: async () => ({ CanCreate: true, PeerCount: 2, Height: fixture.networkHeight, Fees: fees }) };
            if (url === '/balance-url') return { ok: true, json: async () => ({ balanceAtomic: '600000000' }) };
            assert.equal(url, '/prepare-url'); const body = new URLSearchParams(options.body); assert.equal(body.get('UsePolicy'), 'true');
            assert.equal(body.get('AutoBurnMode'), 'percent'); assert.equal(body.get('AutoBurnValue'), '1'); assert.equal(body.get('RequiredSignatures'), '2'); assert.equal(body.get('MaximumSupply'), '200'); assert.equal(body.get('Burnable'), 'true');
            prepared++; return { ok: true, json: async () => fixture.draft };
        };
        const verify = wallet.verifyDraft;
        wallet.verifyDraft = async (draft, expected, ...args) => {
            for (const key of ['Name', 'Symbol', 'Supply', 'Decimals', 'DestinationAddress', 'FeeAtomic']) assert.equal(expected[key], fixture.expected[key], 'form field ' + key);
            return verify(draft, expected, ...args);
        };
        w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-token-create.js', 'utf8'));
        const settle = () => new Promise(resolve => setTimeout(resolve, 30)); await settle();
        byId('token-form').dispatchEvent(new w.Event('change', { bubbles: true }));
        assert.equal(byId('AutoBurnValue').disabled, false);
        assert.equal(byId('MaximumSupply').disabled, false); assert.equal(byId('vesting-fields').disabled, false);
        byId('token-form').dispatchEvent(new w.Event('submit', { bubbles: true, cancelable: true })); await settle();
        assert.equal(byId('app-message').textContent, ''); assert.equal(prepared, 1); assert.equal(byId('sign-actions').hidden, false);
        assert(byId('review-policy').textContent.includes('1% sobre cada transferência direta'));
        assert(byId('review-policy').textContent.includes('2')); assert(byId('review-policy').textContent.includes('Descrição <texto>'));
        assert.equal(byId('review-policy').querySelector('texto'), null, 'description is plain text');
        assert.equal(byId('Privacy').options[1].disabled, true);
        signatures = await wallet.sign(fixture.draft);
    } else if (fixture.mode === 'transfer') {
        await wallet.verifyTransferDraft(fixture.draft, fixture.expected, fixture.networkId, fixture.changeAddress);
        await assert.rejects(wallet.verifyTransferDraft({ ...fixture.draft, automaticBurnAtomic: '0' }, fixture.expected, fixture.networkId, fixture.changeAddress));
        await assert.rejects(wallet.verifyTransferDraft(fixture.draft, { ...fixture.expected, AutomaticBurnAtomic: '2' }, fixture.networkId, fixture.changeAddress));
        signatures = await wallet.sign(fixture.draft); creatorSignature = await wallet.authorizeTransfer(fixture.draft);
    } else {
        await wallet.verifyManagementDraft(fixture.draft, fixture.expected, fixture.networkId);
        await assert.rejects(wallet.verifyManagementDraft({ ...fixture.draft, amountAtomic: '11' }, fixture.expected, fixture.networkId));
        const redirected = JSON.parse(JSON.stringify(fixture.draft)); redirected.outputs[0].address = '0'.repeat(64);
        await assert.rejects(wallet.verifyManagementDraft(redirected, fixture.expected, fixture.networkId));
        await assert.rejects(wallet.verifyManagementDraft({ ...fixture.draft, signingPayload: Buffer.from('forged').toString('base64') }, fixture.expected, fixture.networkId));
        signatures = await wallet.sign(fixture.draft); administratorSignatures = await wallet.signManagementAdministrators(fixture.draft);
        assert.equal(administratorSignatures.length, 2);
        let submissions = 0;
        w.fetch = async (url, options) => {
            if (url === '/tokens-url') return { ok: true, json: async () => [fixture.expected.token] };
            if (url === '/network-url') return { ok: true, json: async () => ({ CanCreate: true, Fees: ['1', fixture.draft.feeAtomic, '4'] }) };
            if (url === '/prepare-url') return { ok: true, json: async () => fixture.draft };
            if (url === '/submit-url') { submissions++; const body = new URLSearchParams(options.body); assert.equal(body.getAll('administratorSignatures').length, 2); return { ok: false, status: 503, json: async () => ({ error: 'temporary' }) }; }
            throw new Error('Unexpected URL: ' + url);
        };
        w.eval(fs.readFileSync('Povix.Dex/Scripts/povix-token-management.js', 'utf8'));
        const settle = () => new Promise(resolve => setTimeout(resolve, 50)); await settle();
        byId('management-token').value = fixture.expected.token.tokenId; byId('management-operation').value = 'mint';
        byId('management-amount').value = '110'; byId('management-destination').value = fixture.expected.destination;
        byId('management-form').dispatchEvent(new w.Event('change', { bubbles: true }));
        byId('management-form').dispatchEvent(new w.Event('submit', { bubbles: true, cancelable: true })); await settle();
        assert.equal(byId('management-message').textContent, ''); assert.equal(byId('management-review').hidden, false);
        byId('management-sign').click(); await settle(); assert.equal(submissions, 1); assert.equal(byId('management-review').hidden, false);
        byId('management-sign').click(); await settle(); assert.equal(submissions, 2);
    }
    fs.writeFileSync(path.join(directory, 'policy-result.json'), JSON.stringify({ signatures, administratorSignatures, creatorSignature }));
    dom.window.close(); console.log('PASS browser ' + fixture.mode + ': policy fields, payload verification, local signatures and tamper rejection');
})().catch(error => { console.error(error); process.exitCode = 1; });
