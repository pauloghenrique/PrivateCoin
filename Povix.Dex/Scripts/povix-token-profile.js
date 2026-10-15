(() => {
    'use strict';
    const app = document.getElementById('profile-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet, el = id => document.getElementById(id);
    let tokens = [], draft = null, expected = null, signature = null, busy = false, walletEpoch = 0;
    const selected = () => tokens.find(token => token.tokenId === el('TokenId').value);
    function message(text) { el('app-message').textContent = text; el('app-message').hidden = !text; }
    function update() {
        const token = selected(), unlocked = wallet.isUnlocked(), editable = unlocked && token && !token.renounced;
        window.PovixWalletMenu?.setBusy(busy || Boolean(draft));
        el('TokenId').disabled = busy || !!draft || !unlocked;
        el('profile-fields').disabled = busy || !!draft || !editable;
        el('prepare-profile').disabled = busy || !!draft || !editable;
        el('sign-profile').disabled = busy || !draft || !unlocked || !el('confirm-profile').checked;
        ['cancel-profile'].forEach(id => { el(id).disabled = busy; });
    }
    function resetDraft() { draft = null; expected = null; signature = null; el('profile-review').hidden = true; el('confirm-profile').checked = false; }
    function showToken() {
        resetDraft();
        const token = selected();
        ['Name', 'Symbol', 'Description', 'Website'].forEach(id => { el(id).value = token ? token[id.toLowerCase()] : ''; });
        el('Renounce').checked = false;
        el('profile-status').textContent = token ? token.renounced ? 'Renúncia permanente neste DEX: edição e encerramento das pools do criador bloqueados.' : 'Edição permitida mediante assinatura do criador.' : 'Selecione um token confirmado.';
        el('chain-details').textContent = token ? token.chainName + ' · ' + token.chainSymbol + ' · Quantidade: ' + wallet.formatAtomic(token.supplyAtomic, token.decimals) + ' · Precisão: ' + token.decimals + ' · ID: ' + token.tokenId : 'Selecione um token.';
        update();
    }
    async function post(url, values) {
        const body = new URLSearchParams();
        body.set('__RequestVerificationToken', el('profile-form').querySelector('[name="__RequestVerificationToken"]').value);
        Object.entries(values).forEach(([key, value]) => Array.isArray(value) ? value.forEach(item => body.append(key, item)) : body.set(key, String(value)));
        const response = await fetch(url, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
        const result = await response.json();
        if (!response.ok || result.error) { const error = new Error(result.error || 'Não foi possível concluir.'); error.rejected = response.status < 500; throw error; }
        return result;
    }
    async function loadTokens() {
        const epoch = walletEpoch;
        const result = await post(app.dataset.tokensUrl, { publicKeys: wallet.publicKeys() });
        if (epoch !== walletEpoch || !wallet.isUnlocked()) return;
        tokens = result;
        const current = el('TokenId').value || app.dataset.tokenId;
        el('TokenId').replaceChildren(new Option('Selecione um token', ''));
        tokens.forEach(token => el('TokenId').add(new Option(token.symbol + ' · ' + token.name, token.tokenId)));
        el('TokenId').value = tokens.some(token => token.tokenId === current) ? current : '';
        showToken();
        if (!tokens.length) message('Nenhum token confirmado foi criado pelas chaves desta carteira.');
    }
    document.addEventListener('povix-wallet-change', async () => {
        try {
            walletEpoch++; tokens = []; resetDraft();
            el('wallet-balance').textContent = 'Sem taxa para editar o perfil';
            el('wallet-balance-detail').hidden = true;
            el('TokenId').replaceChildren(new Option('Conecte a carteira no menu', ''));
            if (wallet.isUnlocked()) await loadTokens();
            else showToken();
            update();
        } catch (error) { message(error.message); }
    });
    el('TokenId').addEventListener('change', showToken);
    el('cancel-profile').addEventListener('click', () => { resetDraft(); update(); });
    el('confirm-profile').addEventListener('change', update);
    el('profile-form').addEventListener('submit', async event => {
        event.preventDefault(); const token = selected();
        if (busy || draft || !wallet.isUnlocked() || !token || token.renounced) return;
        busy = true; message(''); update();
        try {
            expected = Object.freeze({ tokenId: token.tokenId, creatorAddress: token.creatorAddress, revision: token.revision,
                name: el('Name').value.trim(), symbol: el('Symbol').value, description: el('Description').value,
                website: el('Website').value, renounced: el('Renounce').checked });
            const result = await post(app.dataset.prepareUrl, { TokenId: expected.tokenId, Name: expected.name, Symbol: expected.symbol,
                Description: expected.description, Website: expected.website, Renounce: expected.renounced, publicKeys: wallet.publicKeys() });
            // The wallet reconstructs and verifies this authorization immediately before signing.
            draft = Object.freeze(result);
            el('review-details').textContent = expected.name + ' · ' + expected.symbol;
            el('review-description').textContent = expected.description; el('review-website').textContent = expected.website;
            el('review-permission').textContent = expected.renounced ? 'Ao salvar, você perde permanentemente a permissão de editar este perfil e encerrar as pools deste token que administra neste DEX. As reservas permanecem destinadas às trocas e não podem ser retiradas como saldo livre.' : 'A permissão de edição será mantida.';
            el('profile-review').hidden = false;
        } catch (error) { resetDraft(); message(error.message); }
        finally { busy = false; update(); }
    });
    el('sign-profile').addEventListener('click', async () => {
        if (busy || !draft || !el('confirm-profile').checked) return;
        busy = true; message(''); update();
        try {
            if (typeof wallet.authorizeProfile !== 'function')
                throw new Error('A carteira carregada está desatualizada. Recarregue a página para continuar.');
            if (!signature) signature = await wallet.authorizeProfile(draft, expected, app.dataset.networkId);
            await post(app.dataset.submitUrl, { draftId: draft.draftId, signature });
            resetDraft(); await loadTokens(); message('Perfil salvo com autorização do criador.');
        } catch (error) { if (error.rejected) resetDraft(); message(error.message); }
        finally { busy = false; update(); }
    });
    update();
})();
