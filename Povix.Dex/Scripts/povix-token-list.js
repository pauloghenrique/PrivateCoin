(function () {
    'use strict';
    const app = document.getElementById('token-list-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet, byId = id => document.getElementById(id);
    const csrf = byId('token-list-csrf').querySelector('[name="__RequestVerificationToken"]').value;
    let epoch = 0, pending = null, hasResult = false;
    function message(value) { byId('token-list-message').textContent = value || ''; byId('token-list-message').hidden = !value; }
    function clear() {
        epoch++; pending?.abort(); pending = null; hasResult = false;
        byId('token-list').replaceChildren(); byId('token-list').hidden = true;
        byId('token-list-connect').hidden = wallet.isUnlocked(); byId('token-list-refresh').disabled = !wallet.isUnlocked();
        byId('token-create').disabled = byId('token-edit').disabled = !wallet.isUnlocked();
        byId('token-list-state').textContent = wallet.isUnlocked() ? 'Consultando tokens…' : 'Conecte sua carteira para consultar os tokens criados.';
        message('');
    }
    function render(tokens) {
        const rows = [];
        for (const token of tokens) {
            if (!/^[0-9a-f]{64}$/.test(token.tokenId)) throw new Error('O servidor retornou um identificador de token inválido.');
            const row = document.createElement('li'); row.className = 'card token-catalogue-item';
            const title = document.createElement('h3'); title.textContent = token.name + ' (' + token.symbol + ')';
            const id = document.createElement('p'); id.className = 'token-catalogue-id'; id.textContent = token.tokenId;
            const quantity = document.createElement('p'); quantity.textContent = 'Quantidade inicial: ' + wallet.formatAtomic(token.supplyAtomic, token.decimals) + ' ' + token.symbol;
            row.append(title, id, quantity);
            if (token.description) { const description = document.createElement('p'); description.textContent = token.description; row.append(description); }
            if (token.renounced) { const status = document.createElement('span'); status.className = 'muted-text'; status.textContent = 'Edição renunciada'; row.append(status); }
            else {
                const link = document.createElement('a'), url = new URL(app.dataset.editUrl, location.origin);
                if (url.origin !== location.origin) throw new Error('Endereço de edição inválido.');
                url.searchParams.set('tokenId', token.tokenId); link.href = url.href; link.className = 'text-button';
                link.textContent = 'Editar token'; link.setAttribute('aria-label', 'Editar token ' + token.name); row.append(link);
            }
            rows.push(row);
        }
        byId('token-list').replaceChildren(...rows); byId('token-list').hidden = rows.length === 0;
        byId('token-list-state').textContent = rows.length ? rows.length + (rows.length === 1 ? ' token criado.' : ' tokens criados.') : 'Esta carteira ainda não criou tokens confirmados. Use Criar token para começar.';
        hasResult = true;
    }
    async function refresh() {
        if (!wallet.isUnlocked() || pending) return;
        const requestEpoch = epoch, controller = new AbortController(); pending = controller;
        byId('token-list-refresh').disabled = true; message('');
        if (!hasResult) byId('token-list-state').textContent = 'Consultando tokens…';
        try {
            const body = new URLSearchParams({ __RequestVerificationToken: csrf });
            wallet.publicKeys().forEach(key => body.append('publicKeys', key));
            const response = await fetch(app.dataset.tokensUrl, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            const result = await response.json();
            if (requestEpoch !== epoch || !wallet.isUnlocked()) return;
            if (!response.ok || result.error || !Array.isArray(result)) throw new Error(result.error || 'Não foi possível consultar os tokens. Tente atualizar a lista.');
            render(result);
        } catch (error) {
            if (requestEpoch !== epoch || error.name === 'AbortError') return;
            message(error.message || 'Não foi possível consultar os tokens.');
            if (!hasResult) byId('token-list-state').textContent = 'Consulta indisponível. Tente atualizar a lista.';
        } finally {
            if (pending === controller) { pending = null; byId('token-list-refresh').disabled = !wallet.isUnlocked(); }
        }
    }
    for (const id of ['token-create', 'token-edit']) {
        byId(id).addEventListener('click', () => {
            if (!wallet.isUnlocked()) return;
            location.assign(byId(id).dataset.url);
        });
    }
    byId('token-list-refresh').addEventListener('click', refresh);
    document.addEventListener('povix-wallet-change', () => { clear(); refresh(); });
    clear(); refresh();
})();
