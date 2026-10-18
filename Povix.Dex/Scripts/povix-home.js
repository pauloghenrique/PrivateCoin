(function () {
    'use strict';
    const app = document.getElementById('dex-home');
    if (!app) return;
    const byId = id => document.getElementById(id);
    let pools = [], loading = false;
    function element(tag, text, className) {
        const node = document.createElement(tag); node.textContent = text;
        if (className) node.className = className;
        return node;
    }
    const visiblePool = pool => pool.available === true || pool.nativeInitialLaunch === true;
    function reserveText(value, asset) {
        const amount = BigInt(value), scale = 10n ** BigInt(asset.decimals);
        const fraction = (amount % scale).toString().padStart(asset.decimals, '0').replace(/0+$/, '');
        return (amount / scale).toLocaleString('pt-BR') + (fraction ? ',' + fraction : '') + ' ' + asset.symbol;
    }
    function render() {
        const query = byId('dex-search').value.trim().toLocaleLowerCase('pt-BR');
        const rows = byId('dex-token-list'); rows.replaceChildren();
        pools.filter(visiblePool).forEach(pool => {
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
            const id = element('td', '');
            const code = element('code', pool.poolId.slice(0, 10) + '…' + pool.poolId.slice(-6));
            code.title = pool.poolId; id.append(code);
            const reserves = element('td', '');
            reserves.append(element('div', reserveText(pool.reserveAAtomic, pool.assetA)),
                element('small', reserveText(pool.reserveBAtomic, pool.assetB)));
            const action = element('td', '');
            if (pool.nativeInitialLaunch) {
                action.append(element('div', pool.available ? 'Negociação aberta' : pool.status === 'collecting' ? 'Em captação · 6 POVIX por 1 USDC local' : pool.statusMessage || 'Aguardando disponibilidade'));
            }
            const link = element('a', pool.available ? 'Negociar ↗' : 'Ver pool ↗', 'button button-secondary');
            const url = new URL(app.dataset.marketUrl, window.location.href);
            url.searchParams.set('poolId', pool.poolId);
            link.href = url.href; action.append(link);
            row.append(identity, id, reserves, action); rows.append(row);
        });
        byId('dex-result-count').textContent = rows.children.length + ' pool(s) encontrada(s)';
        if (!loading) {
            byId('dex-status').textContent = rows.children.length ? '' : pools.some(visiblePool) ? 'Nenhuma pool corresponde à busca.' : 'Nenhuma pool encontrada na rede.';
            byId('dex-status').hidden = rows.children.length > 0;
        }
    }
    function updateCatalogue(data) {
        if (!data || !Array.isArray(data.pools)) return;
        pools = data.pools;
        const pairedAssets = new Set();
        pools.filter(visiblePool).forEach(pool => {
            pairedAssets.add(pool.assetA.id); pairedAssets.add(pool.assetB.id);
        });
        byId('dex-asset-count').textContent = pairedAssets.size;
        byId('dex-pool-count').textContent = pools.filter(visiblePool).length;
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
            updateCatalogue(data);
            loading = false; render();
        } catch (_) {
            byId('dex-status').hidden = false;
            byId('dex-status').textContent = pools.length ? 'Não foi possível atualizar. Exibindo o último catálogo consultado. Tente novamente.' : 'Não foi possível consultar a rede. Clique em Atualizar para tentar novamente.';
        } finally { loading = false; byId('dex-refresh').disabled = false; }
    }
    byId('dex-search').addEventListener('input', render);
    byId('dex-refresh').addEventListener('click', refresh);
    try { updateCatalogue(JSON.parse(app.dataset.initialCatalogue || 'null')); render(); }
    catch (_) { /* The live catalogue request can recover an unavailable initial snapshot. */ }
    refresh();
    const refreshTimer = window.setInterval(refresh, 15000);
    window.addEventListener('pagehide', () => window.clearInterval(refreshTimer), { once: true });
}());
