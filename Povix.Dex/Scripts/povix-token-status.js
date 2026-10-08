(function () {
    'use strict';
    const app = document.getElementById('registration-app');
    if (!app) return;
    const byId = id => document.getElementById(id);
    let refreshing = false;
    async function refresh() {
        if (refreshing) return;
        refreshing = true;
        try {
            const response = await fetch(app.dataset.statusUrl, { cache: 'no-store', credentials: 'same-origin' });
            if (!response.ok) throw new Error('Status indisponível');
            const data = await response.json();
            const confirmed = data.status === 'confirmed', pending = data.status === 'pending';
            byId('receipt-title').textContent = confirmed ? 'Token registrado.' : pending ? 'Transação enviada.' : 'Registro não confirmado.';
            byId('receipt-description').textContent = confirmed ? 'A criação do token consta em um bloco validado da rede POVIX.' :
                pending ? 'A criação está na fila e aguarda confirmação em um bloco validado.' :
                    'A transação deixou a fila após a atualização da cadeia. Confira o saldo antes de tentar novamente.';
            byId('receipt-status').textContent = confirmed ? 'Confirmado' : pending ? 'Pendente' : 'Não confirmado';
            byId('receipt-status').classList.toggle('waiting', !confirmed);
            byId('receipt-confirmations').textContent = String(data.confirmations);
            byId('receipt-block').textContent = data.blockHeight == null ? 'Aguardando confirmação' : String(data.blockHeight);
            byId('receipt-hash').textContent = data.blockHash || '—';
            byId('receipt-connectivity').textContent = data.peerCount + ' pares conectados · Atualização automática a cada 10 segundos';
        } catch (_) { byId('receipt-connectivity').textContent = 'Consulta indisponível no momento. A confirmação exibida será atualizada ao reconectar.'; }
        finally { refreshing = false; }
    }
    refresh(); setInterval(refresh, 10000);
})();
