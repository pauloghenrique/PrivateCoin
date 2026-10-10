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
            const confirmed = data.status === 'confirmed', pending = data.status === 'pending', validated = data.status === 'validated';
            const transfer = app.dataset.operation === 'transfer';
            const confirmedWithoutBlock = confirmed && !transfer && data.blockHeight == null;
            byId('receipt-title').textContent = confirmed ? (transfer ? 'Movimentação confirmada.' : 'Token registrado.') : validated ? (transfer ? 'Movimentação validada.' : 'Criação validada.') : pending ? (transfer ? 'Transação enviada.' : 'Aguardando validação.') : (transfer ? 'Movimentação não confirmada.' : 'Registro não confirmado.');
            byId('receipt-description').textContent = confirmed ? (transfer ? 'A transferência consta em um bloco validado da rede POVIX.' : (confirmedWithoutBlock ? 'A criação do token foi confirmada por uma carteira com tokens bloqueados, sem criar bloco. A taxa já foi paga ao validador e o token está disponível.' : 'A criação do token consta em um bloco validado da rede POVIX.')) :
                validated ? (transfer ? 'A movimentação foi validada e a taxa já foi transferida ao validador. O envio aguarda o lote de 20 operações.' : 'Uma carteira com tokens bloqueados confirmou a criação e recebeu a taxa. A confirmação da criação não depende de bloco.') :
                pending ? (transfer ? 'O envio aguarda aprovação de uma carteira com tokens bloqueados, sem criar bloco nessa etapa.' : (data.waitingReason || 'A criação aguarda aprovação de uma carteira com tokens bloqueados. Essa validação não cria um bloco.')) :
                    'A transação deixou a fila após a atualização da cadeia. Confira o saldo antes de tentar novamente.';
            byId('receipt-status').textContent = confirmed ? 'Confirmado' : validated ? 'Validado' : pending ? (transfer ? 'Pendente' : 'Aguardando validação') : 'Não confirmado';
            byId('receipt-status').classList.toggle('waiting', !confirmed);
            byId('receipt-confirmations').textContent = String(transfer ? data.confirmations : (data.validations || 0));
            byId('receipt-block').textContent = data.blockHeight == null ? (confirmedWithoutBlock ? 'Sem bloco: confirmação por validação' : 'Aguardando confirmação') : String(data.blockHeight);
            byId('receipt-hash').textContent = data.blockHash || '—';
            byId('receipt-connectivity').textContent = data.peerCount + ' pares conectados · Atualização automática a cada 10 segundos';
        } catch (_) { byId('receipt-connectivity').textContent = 'Consulta indisponível no momento. A confirmação exibida será atualizada ao reconectar.'; }
        finally { refreshing = false; }
    }
    refresh(); setInterval(refresh, 10000);
})();
