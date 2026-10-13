(function () {
    'use strict';
    const app = document.getElementById('pool-market');
    if (!app) return;
    const byId = id => document.getElementById(id);
    const operations = { pool: 'Criação', swap: 'Troca', lock: 'Bloqueio', close: 'Encerramento' };
    const statuses = { confirmed: 'Confirmada', pending: 'Pendente', validated: 'Validada', rejected: 'Rejeitada' };
    let timer, controller, generation = 0, stopped = false;
    function atomic(value, decimals) {
        let text = String(value), sign = '';
        if (text.startsWith('-')) { sign = '−'; text = text.slice(1); }
        else if (text !== '0') sign = '+';
        text = text.padStart(decimals + 1, '0');
        return sign + (decimals ? text.slice(0, -decimals) + ',' + text.slice(-decimals) : text);
    }
    function svg(tag, attributes, text) {
        const element = document.createElementNS('http://www.w3.org/2000/svg', tag);
        Object.entries(attributes).forEach(([key, value]) => element.setAttribute(key, value));
        if (text !== undefined) element.textContent = text;
        return element;
    }
    function chart(candles, interval) {
        const root = byId('market-candles'); root.replaceChildren();
        byId('market-chart-empty').hidden = candles.length > 0;
        root.hidden = !candles.length;
        byId('market-chart-empty').textContent = 'Ainda não há trocas confirmadas nesta pool. Os candles aparecerão após a primeira troca confirmada.';
        if (!candles.length) return;
        const values = candles.flatMap(c => [Number(c.low), Number(c.high)]);
        const low = Math.min(...values), high = Math.max(...values), pad = (high - low || high * .01 || .01) * .15;
        const min = low - pad, max = high + pad, y = price => 305 - (price - min) / (max - min) * 280;
        const first = candles[0].time, last = candles[candles.length - 1].time;
        const span = Math.max(interval, last - first + interval);
        const x = time => 18 + (time - first + interval / 2) / span * 805;
        const width = Math.max(1, Math.min(14, 805 * interval / span * .65));
        for (let i = 0; i <= 4; i++) {
            const price = min + (max - min) * i / 4, height = y(price);
            root.append(svg('line', { x1: 12, x2: 830, y1: height, y2: height, stroke: '#d5dfdf' }),
                svg('text', { x: 842, y: height + 4, fill: '#526666', 'font-size': 12 }, price.toLocaleString('pt-BR', { maximumSignificantDigits: 7 })));
        }
        candles.forEach(c => {
            const color = c.close >= c.open ? '#12836d' : '#c94450', center = x(c.time);
            const group = svg('g', {});
            group.append(svg('title', {}, new Date(c.time * 1000).toLocaleString('pt-BR') + '\nAbertura: ' + c.open + '\nMáxima: ' + c.high + '\nMínima: ' + c.low + '\nFechamento: ' + c.close),
                svg('line', { x1: center, x2: center, y1: y(c.high), y2: y(c.low), stroke: color }),
                svg('rect', { x: center - width / 2, y: Math.min(y(c.open), y(c.close)), width, height: Math.max(2, Math.abs(y(c.open) - y(c.close))), fill: color }));
            root.append(group);
        });
        [candles[0], candles[candles.length - 1]].forEach((c, i) => root.append(svg('text', { x: i ? 830 : 12, y: 340, 'text-anchor': i ? 'end' : 'start', fill: '#526666', 'font-size': 12 }, new Date(c.time * 1000).toLocaleString('pt-BR'))));
    }
    function render(data) {
        const p = data.pool;
        byId('market-pair').textContent = p.assetA.symbol + ' / ' + p.assetB.symbol;
        byId('market-price').textContent = data.price == null ? '—' : Number(data.price).toLocaleString('pt-BR', { maximumSignificantDigits: 10 }) + ' ' + p.assetA.symbol + ' por ' + p.assetB.symbol;
        byId('market-reserve-a').textContent = atomic(p.reserveAAtomic, p.assetA.decimals).replace(/^\+/, '') + ' ' + p.assetA.symbol;
        byId('market-reserve-b').textContent = atomic(p.reserveBAtomic, p.assetB.decimals).replace(/^\+/, '') + ' ' + p.assetB.symbol;
        if (p.status === 'closed') { byId('market-reserve-a').textContent = '0'; byId('market-reserve-b').textContent = '0'; }
        byId('market-state').textContent = p.statusMessage;
        byId('market-label-a').textContent = 'Reserva de ' + p.assetA.symbol;
        byId('market-label-b').textContent = 'Reserva de ' + p.assetB.symbol;
        byId('market-heading-a').textContent = 'Movimento ' + p.assetA.symbol;
        byId('market-heading-b').textContent = 'Movimento ' + p.assetB.symbol;
        byId('market-ledger-count').textContent = 'Últimas ' + data.ledger.length + ' de ' + data.ledgerTotal + ' movimentações · Horário local';
        const body = byId('market-ledger'); body.replaceChildren();
        if (!data.ledger.length) {
            const row = document.createElement('tr'), cell = document.createElement('td');
            cell.setAttribute('colspan', '7'); cell.className = 'market-empty';
            cell.textContent = 'Nenhuma movimentação registrada nesta pool.'; row.appendChild(cell); body.appendChild(row);
        }
        data.ledger.forEach(item => {
            const row = document.createElement('tr');
            [new Date(item.time * 1000).toLocaleString('pt-BR'), operations[item.operation] || item.operation, statuses[item.status] || item.status,
                atomic(item.deltaAAtomic, p.assetA.decimals), atomic(item.deltaBAtomic, p.assetB.decimals), item.blockHeight == null ? '—' : item.blockHeight].forEach((text, index) => {
                    const cell = document.createElement('td'); cell.textContent = text;
                    if (index === 2) cell.setAttribute('data-status', item.status);
                    if (index === 3 || index === 4) cell.className = 'market-amount' + (item.status === 'confirmed' ? (String(text).startsWith('−') ? ' market-negative' : ' market-positive') : '');
                    row.appendChild(cell);
                });
            const cell = document.createElement('td');
            // Receipts exist only for operations submitted by this installation.
            cell.textContent = item.transactionId; cell.setAttribute('title', item.transactionId); cell.className = 'market-tx'; row.appendChild(cell); body.appendChild(row);
        });
        const info = byId('market-token-info'); info.replaceChildren();
        (data.tokens || [p.assetA, p.assetB]).forEach(token => {
            const card = document.createElement('section'), title = document.createElement('h3');
            title.textContent = (token.name || token.symbol) + ' (' + token.symbol + ')'; card.appendChild(title);
            const fields = [['Identificador', token.id], ['Precisão', token.decimals + ' casas decimais'],
                ['Oferta total', token.supplyAtomic == null ? 'Ativo nativo POVIX' : atomic(token.supplyAtomic, token.decimals).replace(/^\+/, '') + ' ' + token.symbol]];
            const list = document.createElement('dl');
            fields.forEach(([label, value]) => { const term = document.createElement('dt'), detail = document.createElement('dd'); term.textContent = label; detail.textContent = value; list.append(term, detail); });
            card.appendChild(list);
            if (token.description) { const description = document.createElement('p'); description.textContent = token.description; card.appendChild(description); }
            info.appendChild(card);
        });
        chart(data.candles, data.interval);
        byId('market-connection').setAttribute('data-state', 'ready');
        byId('market-connection').textContent = 'Atualizado às ' + new Date(data.updatedUtc).toLocaleTimeString('pt-BR') + ' · ' + data.peerCount + ' pares · Consulta a cada 5 segundos';
    }
    async function refresh() {
        clearTimeout(timer);
        if (controller) controller.abort();
        const current = ++generation;
        if (stopped || document.hidden) return;
        controller = new AbortController();
        const requestController = controller;
        const timeout = setTimeout(() => requestController.abort(), 10000);
        try {
            const url = new URL(app.dataset.url, location.href); url.searchParams.set('interval', byId('market-interval').value);
            const response = await fetch(url, { cache: 'no-store', signal: requestController.signal, headers: { Accept: 'application/json' } });
            if (!response.ok) throw new Error(response.status === 404 ? 'Pool não encontrada.' : 'Não foi possível consultar a rede.');
            const data = await response.json(); if (current === generation) render(data);
        } catch (error) {
            if (current === generation && !stopped && !document.hidden) {
                byId('market-connection').setAttribute('data-state', 'error');
                byId('market-connection').textContent = (error.name === 'AbortError' ? 'Consulta interrompida.' : error.message) + ' Os dados exibidos podem estar desatualizados. Nova tentativa em 5 segundos.';
            }
        } finally {
            clearTimeout(timeout);
            if (current === generation && !stopped && !document.hidden) timer = setTimeout(refresh, 5000);
        }
    }
    byId('market-interval').addEventListener('change', refresh);
    byId('market-refresh').addEventListener('click', refresh);
    document.addEventListener('visibilitychange', refresh);
    document.addEventListener('povix-pool-updated', refresh);
    window.addEventListener('pagehide', () => { stopped = true; clearTimeout(timer); if (controller) controller.abort(); });
    window.addEventListener('pageshow', () => { if (stopped) { stopped = false; refresh(); } });
    refresh();
}());
