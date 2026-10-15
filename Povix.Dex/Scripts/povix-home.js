(function () {
    'use strict';
    const app = document.getElementById('dex-home');
    if (!app) return;
    const byId = id => document.getElementById(id);
    let assets = [], pools = [], filter = 'all', loading = false;
    function element(tag, text, className) {
        const node = document.createElement(tag); node.textContent = text;
        if (className) node.className = className;
        return node;
    }
    function render() {
        const query = byId('dex-search').value.trim().toLocaleLowerCase('pt-BR');
        const rows = byId('dex-token-list'); rows.replaceChildren();
        assets.forEach(asset => {
            const matches = pools.filter(pool => pool.available === true && (pool.assetA.id === asset.id || pool.assetB.id === asset.id));
            if (filter === 'pools' && !matches.length) return;
            if (![asset.name, asset.symbol, asset.id].some(value => String(value).toLocaleLowerCase('pt-BR').includes(query))) return;
            const row = element('tr', '');
            const identity = element('td', ''), group = element('div', '', 'dex-token-identity');
            group.append(element('span', String(asset.symbol).slice(0, 2), 'dex-token-icon'));
            const name = element('div', ''); name.append(element('strong', asset.name), element('small', asset.symbol));
            group.append(name); identity.append(group);
            const id = element('td', '');
            const code = element('code', asset.id === 'povix' ? 'Ativo nativo' : asset.id.slice(0, 10) + '…' + asset.id.slice(-6));
            code.title = asset.id; id.append(code);
            const count = element('td', String(matches.length));
            const action = element('td', '');
            if (matches.length) {
                const link = element('a', matches.length === 1 ? 'Negociar ↗' : 'Ver pools →', 'button button-secondary');
                const url = new URL(matches.length === 1 ? app.dataset.marketUrl : app.dataset.poolsUrl, window.location.href);
                if (matches.length === 1) url.searchParams.set('poolId', matches[0].poolId);
                link.href = url.href; action.append(link);
            } else action.append(element('span', 'Sem pool disponível', 'dex-no-pool'));
            row.append(identity, id, count, action); rows.append(row);
        });
        byId('dex-result-count').textContent = rows.children.length + ' ativo(s) encontrado(s)';
        if (!loading) {
            byId('dex-status').textContent = rows.children.length ? '' : assets.length ? 'Nenhum token corresponde aos filtros.' : 'Nenhum ativo encontrado no catálogo.';
            byId('dex-status').hidden = rows.children.length > 0;
        }
    }
    async function refresh() {
        if (loading) return;
        loading = true; byId('dex-refresh').disabled = true;
        byId('dex-status').hidden = false; byId('dex-status').textContent = 'Consultando o catálogo da rede…';
        try {
            const body = new URLSearchParams({ __RequestVerificationToken: app.querySelector('[name="__RequestVerificationToken"]').value });
            const response = await fetch(app.dataset.catalogueUrl, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
            const data = await response.json();
            if (!response.ok || !Array.isArray(data.assets) || !Array.isArray(data.pools)) throw new Error('catalogue');
            assets = data.assets; pools = data.pools;
            byId('dex-asset-count').textContent = assets.length;
            byId('dex-pool-count').textContent = pools.filter(pool => pool.available === true).length;
            loading = false; render();
        } catch (_) {
            byId('dex-status').hidden = false;
            byId('dex-status').textContent = assets.length ? 'Não foi possível atualizar. Exibindo o último catálogo consultado. Tente novamente.' : 'Não foi possível consultar a rede. Clique em Atualizar para tentar novamente.';
        } finally { loading = false; byId('dex-refresh').disabled = false; }
    }
    byId('dex-search').addEventListener('input', render);
    app.querySelectorAll('[data-filter]').forEach(button => button.addEventListener('click', function () {
        filter = this.dataset.filter;
        app.querySelectorAll('[data-filter]').forEach(tab => tab.setAttribute('aria-pressed', String(tab === this)));
        render();
    }));
    byId('dex-refresh').addEventListener('click', refresh);
    refresh();
}());
