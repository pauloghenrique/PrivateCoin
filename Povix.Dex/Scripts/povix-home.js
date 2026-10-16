(function () {
    'use strict';
    const app = document.getElementById('dex-home');
    if (!app) return;
    const byId = id => document.getElementById(id);
    let pools = [], loading = false, recent = [];
    try { const saved = JSON.parse(sessionStorage.getItem('povi-dex-recent') || '[]'); if (Array.isArray(saved)) recent = saved.filter(id => typeof id === 'string').slice(0, 5); } catch (_) { }
    function element(tag, text, className) {
        const node = document.createElement(tag); node.textContent = text;
        if (className) node.className = className;
        return node;
    }
    function render() {
        const query = byId('dex-search').value.trim().toLocaleLowerCase('pt-BR');
        const rows = byId('dex-token-list'); rows.replaceChildren();
        pools.filter(pool => pool.available === true).forEach(pool => {
            const pair = pool.assetA.symbol + '/' + pool.assetB.symbol;
            if (![pair, pool.assetA.name, pool.assetB.name, pool.assetA.id, pool.assetB.id, pool.poolId]
                .some(value => String(value).toLocaleLowerCase('pt-BR').includes(query))) return;
            const row = element('tr', '');
            const identity = element('td', ''), group = element('div', '', 'dex-token-identity');
            group.append(element('span', String(pool.assetA.symbol).slice(0, 2), 'dex-token-icon'),
                element('span', String(pool.assetB.symbol).slice(0, 2), 'dex-token-icon'));
            const name = element('div', '');
            name.append(element('strong', pair), element('small', pool.assetA.name + ' / ' + pool.assetB.name));
            group.append(name); identity.append(group);
            const id = element('td', '', 'dex-home-reserves');
            id.append(element('span', atomic(pool.reserveAAtomic, pool.assetA.decimals) + ' ' + pool.assetA.symbol),
                element('small', atomic(pool.reserveBAtomic, pool.assetB.decimals) + ' ' + pool.assetB.symbol));
            const action = element('td', '');
            const link = element('a', 'Negociar ↗', 'button button-secondary');
            const url = new URL(app.dataset.marketUrl, window.location.href);
            url.searchParams.set('poolId', pool.poolId);
            link.href = url.href; remember(link, pool); action.append(link);
            row.append(identity, id, action); rows.append(row);
        });
        renderPanels();
        byId('dex-result-count').textContent = rows.children.length + ' pool(s) encontrada(s)';
        if (!loading) {
            byId('dex-status').textContent = rows.children.length ? '' : pools.some(pool => pool.available === true) ? 'Nenhuma pool corresponde à busca.' : 'Nenhuma pool disponível para negociação.';
            byId('dex-status').hidden = rows.children.length > 0;
        }
    }
    function atomic(value, decimals) {
        if (!/^\d+$/.test(String(value)) || !Number.isInteger(decimals) || decimals < 0 || decimals > 8) return '—';
        const digits = String(value).padStart(decimals + 1, '0');
        return decimals ? digits.slice(0, -decimals) + ',' + digits.slice(-decimals) : digits;
    }
    function remember(link, pool) {
        link.addEventListener('click', () => {
            recent = [pool.poolId].concat(recent.filter(id => id !== pool.poolId)).slice(0, 5);
            try { sessionStorage.setItem('povi-dex-recent', JSON.stringify(recent)); } catch (_) { }
        });
    }
    function renderPanels() {
        const available = pools.filter(pool => pool.available === true);
        function list(id, items, empty) {
            const root = byId(id); root.replaceChildren();
            if (!items.length) { root.append(element('p', empty, 'dex-panel-empty')); return; }
            items.forEach(pool => {
                const url = new URL(app.dataset.marketUrl, window.location.href); url.searchParams.set('poolId', pool.poolId);
                const link = element('a', '', 'dex-panel-row'); link.href = url.href;
                const label = element('span', ''); label.append(element('strong', pool.assetA.symbol + '/' + pool.assetB.symbol), element('small', pool.assetA.name + ' / ' + pool.assetB.name));
                link.append(element('span', String(pool.assetB.symbol).slice(0, 2), 'dex-token-icon'), label, element('span', '↗', 'dex-panel-arrow'));
                remember(link, pool); root.append(link);
            });
        }
        list('dex-catalogue-pools', available.slice(0, 6), 'Nenhuma pool disponível.');
        list('dex-recent-pools', recent.map(id => available.find(pool => pool.poolId === id)).filter(Boolean), 'Os pares que você abrir aparecerão aqui.');
        const assets = new Map(); available.forEach(pool => [pool.assetA, pool.assetB].forEach(asset => assets.set(asset.id, asset)));
        const root = byId('dex-catalogue-assets'); root.replaceChildren();
        if (!assets.size) root.append(element('p', 'Nenhum ativo em pools disponíveis.', 'dex-panel-empty'));
        [...assets.values()].slice(0, 5).forEach(asset => {
            const row = element('div', '', 'dex-panel-row'), name = element('span', '');
            name.append(element('strong', asset.symbol), element('small', asset.name));
            row.append(element('span', String(asset.symbol).slice(0, 2), 'dex-token-icon'), name); root.append(row);
        });
    }
    async function refresh() {
        if (loading) return;
        loading = true; byId('dex-refresh').disabled = true;
        byId('dex-status').hidden = false; byId('dex-status').textContent = 'Consultando o catálogo da rede…';
        try {
            const body = new URLSearchParams({ __RequestVerificationToken: app.querySelector('[name="__RequestVerificationToken"]').value });
            const response = await fetch(app.dataset.catalogueUrl, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
            const data = await response.json();
            if (!response.ok || !Array.isArray(data.pools)) throw new Error('catalogue');
            pools = data.pools;
            const pairedAssets = new Set();
            pools.filter(pool => pool.available === true).forEach(pool => {
                pairedAssets.add(pool.assetA.id); pairedAssets.add(pool.assetB.id);
            });
            byId('dex-asset-count').textContent = pairedAssets.size;
            byId('dex-pool-count').textContent = pools.filter(pool => pool.available === true).length;
            loading = false; render();
        } catch (_) {
            byId('dex-status').hidden = false;
            byId('dex-status').textContent = pools.length ? 'Não foi possível atualizar. Exibindo o último catálogo consultado. Tente novamente.' : 'Não foi possível consultar a rede. Clique em Atualizar para tentar novamente.';
        } finally { loading = false; byId('dex-refresh').disabled = false; }
    }
    byId('dex-search').addEventListener('input', render);
    byId('dex-refresh').addEventListener('click', refresh);
    refresh();
}());
