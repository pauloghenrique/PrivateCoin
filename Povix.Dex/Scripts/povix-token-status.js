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
            const confirmed = data.status === 'confirmed' && data.blockHeight != null, pending = data.status === 'pending', validated = data.status === 'validated' || (data.status === 'confirmed' && data.blockHeight == null);
            const transfer = app.dataset.operation === 'transfer';
            const exchange = app.dataset.operation === 'exchange';
            byId('receipt-title').textContent = confirmed ? (transfer ? 'Movimentação confirmada.' : 'Token registrado.') : validated ? (transfer ? 'Movimentação validada.' : 'Criação validada.') : pending ? (transfer ? 'Transação enviada.' : 'Aguardando validação.') : (transfer ? 'Movimentação não confirmada.' : 'Registro não confirmado.');
            byId('receipt-description').textContent = confirmed ? (transfer ? 'A transferência consta em um bloco validado da rede POVIX.' : 'A criação do token consta em um bloco validado da rede POVIX.') :
                validated ? 'A operação foi aprovada pelo validador. Com validadores elegíveis, o bloco é produzido ao reunir 20 operações aprovadas ou após 10 segundos de espera. Os saldos e a taxa serão efetivados após a confirmação em bloco.' :
                pending ? (data.waitingReason || 'A operação aguarda aprovação de uma carteira com garantia confirmada em bloco.') :
                    'A transação deixou a fila após a atualização da cadeia. Confira o saldo antes de tentar novamente.';
            byId('receipt-status').textContent = confirmed ? 'Confirmado' : validated ? 'Validado' : pending ? (transfer ? 'Pendente' : 'Aguardando validação') : 'Não confirmado';
            if (exchange) {
                if (byId('receipt-pool-state')) byId('receipt-pool-state').textContent = data.poolStatusMessage || 'Aguardando atualização do estado da pool.';
                byId('receipt-title').textContent = confirmed ? 'Operação confirmada.' : validated ? 'Operação validada.' : pending ? 'Aguardando validação.' : 'Operação não confirmada.';
                if (confirmed) byId('receipt-description').textContent = 'A operação consta em um bloco validado da rede POVIX. As reservas e os saldos foram efetivados juntos.';
            }
            byId('receipt-status').classList.toggle('waiting', !confirmed);
            byId('receipt-validations').textContent = String(data.validations || 0);
            byId('receipt-confirmations').textContent = String(confirmed ? data.confirmations || 0 : 0);
            byId('receipt-block').textContent = confirmed ? String(data.blockHeight) : 'Aguardando confirmação';
            byId('receipt-hash').textContent = confirmed ? data.blockHash || '—' : '—';
            byId('receipt-connectivity').textContent = data.peerCount + ' pares conectados · Atualização automática a cada 10 segundos';
        } catch (_) { byId('receipt-connectivity').textContent = 'Consulta indisponível no momento. A confirmação exibida será atualizada ao reconectar.'; }
        finally { refreshing = false; }
    }
    refresh(); setInterval(refresh, 10000);
})();
