'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class Element {
    constructor(id) { this.id = id; this.value = ''; this.hidden = false; this.disabled = false; this.checked = false; this.handlers = {}; this.files = []; this.options = []; this.dataset = {}; }
    addEventListener(name, handler) { this.handlers[name] = handler; }
    async fire(name) { if (this.handlers[name]) await this.handlers[name]({ preventDefault() {} }); }
    replaceChildren(...items) { this.options = items; this.value = items[0]?.value || ''; }
    add(option) { this.options.push(option); }
    querySelector() { return { value: 'csrf-test' }; }
}
async function run() {
    const elements = new Map();
    const get = id => { if (!elements.has(id)) elements.set(id, new Element(id)); return elements.get(id); };
    get('profile-app').dataset = { tokensUrl: '/tokens', prepareUrl: '/prepare', submitUrl: '/submit', networkId: 'network' };
    let unlocked = false, renounced = false, revision = 0, failure = false, submits = 0;
    const token = { tokenId: 'a'.repeat(64), creatorAddress: 'b'.repeat(64), name: '<img onerror=alert(1)>',
        symbol: 'ABC', description: '<script>text</script>', website: 'https://example.com', chainName: 'Original', chainSymbol: 'OLD', decimals: 2, supplyAtomic: '100' };
    const wallet = {
        isUnlocked: () => unlocked, publicKeys: () => ['public'], formatAtomic: () => '1,00', lock: () => { unlocked = false; },
        readSaved: async () => [{ id: '0', name: 'Criador', addressCount: 1 }], selectWallet: async () => { unlocked = true; return { name: 'Criador' }; },
        authorizeProfile: async (draft, expected, networkId) => { assert.equal(networkId, 'network'); assert.equal(draft.name, expected.name); return 'signature'; }
    };
    const requests = [];
    const fetch = async (url, options) => {
        assert.equal(options.body.get('__RequestVerificationToken'), 'csrf-test'); requests.push(url);
        if (url === '/tokens') return { ok: true, json: async () => [{ ...token, renounced, revision }] };
        if (url === '/prepare') return { ok: true, json: async () => ({ draftId: 'draft', name: options.body.get('Name'), renounced: options.body.get('Renounce') === 'true' }) };
        if (url === '/submit') {
            submits++; assert.equal(options.body.get('signature'), 'signature');
            if (failure) return { ok: false, status: 503, json: async () => ({ error: 'Tente novamente' }) };
            renounced = get('Renounce').checked; revision++; return { ok: true, json: async () => ({ saved: true }) };
        }
        throw Error('Unexpected URL');
    };
    vm.runInNewContext(fs.readFileSync('Povix.Dex/Scripts/povix-token-profile.js', 'utf8'), {
        document: { getElementById: get }, window: { PovixTokenWallet: wallet }, fetch, URLSearchParams,
        Option: class { constructor(text, value) { this.text = text; this.value = value; } }
    });
    assert.equal(get('prepare-profile').disabled, true);
    await get('wallet-form').fire('submit'); assert.equal(unlocked, false);
    get('wallet-choice').value = '0'; await get('wallet-form').fire('submit');
    assert.equal(unlocked, true); assert.equal(get('prepare-profile').disabled, true);
    get('TokenId').value = token.tokenId; await get('TokenId').fire('change');
    assert.equal(get('Name').value, token.name); assert.equal(get('prepare-profile').disabled, false);
    get('Name').value = 'Editado'; await get('profile-form').fire('submit');
    assert.equal(get('profile-review').hidden, false); assert.equal(get('review-details').textContent, 'Editado · ABC');
    assert.equal(get('review-description').textContent, '<script>text</script>');
    assert.equal(get('sign-profile').disabled, true); await get('sign-profile').fire('click'); assert.equal(submits, 0);
    get('confirm-profile').checked = true; await get('confirm-profile').fire('change');
    const authorizeProfile = wallet.authorizeProfile;
    delete wallet.authorizeProfile;
    await get('sign-profile').fire('click');
    assert.equal(submits, 0);
    assert.match(get('app-message').textContent, /desatualizada.*Recarregue/);
    assert.equal(get('profile-review').hidden, false);
    wallet.authorizeProfile = authorizeProfile;
    failure = true; await get('sign-profile').fire('click');
    assert.equal(get('profile-review').hidden, false); assert.equal(get('sign-profile').disabled, false);
    failure = false; await get('sign-profile').fire('click'); assert.equal(submits, 2); assert.equal(get('profile-review').hidden, true);
    get('Renounce').checked = true; await get('profile-form').fire('submit');
    assert.match(get('review-permission').textContent, /permanentemente/);
    get('confirm-profile').checked = true; await get('confirm-profile').fire('change'); await get('sign-profile').fire('click');
    assert.equal(renounced, true); assert.equal(get('profile-fields').disabled, true); assert.equal(get('prepare-profile').disabled, true);
    const before = requests.length; await get('profile-form').fire('submit'); assert.equal(requests.length, before);
    await get('wallet-lock').fire('click'); assert.equal(unlocked, false); assert.equal(get('Name').value, '');
    console.log('PASS profile UI: explicit wallet choice, selection, review, confirmation, outdated wallet rejection, retry, renunciation and locking');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
