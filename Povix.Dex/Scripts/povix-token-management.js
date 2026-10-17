(function () {
    'use strict';
    const app = document.getElementById('token-management-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet, byId = id => document.getElementById(id), form = byId('management-form');
    const csrf = form.querySelector('[name="__RequestVerificationToken"]').value;
    let tokens = [], draft = null, expected = null, signatures = null, administratorSignatures = null, busy = false, receipt = null, polling = false;
    function message(value) { byId('management-message').textContent = value || ''; byId('management-message').hidden = !value; }
    async function request(url, values) {
        const options = { credentials: 'same-origin', cache: 'no-store' };
        if (values) {
            options.method = 'POST'; options.body = new URLSearchParams({ __RequestVerificationToken: csrf });
            for (const [key, value] of Object.entries(values)) {
                if (Array.isArray(value)) value.forEach(item => options.body.append(key, item));
                else options.body.append(key, value);
            }
        }
        const response = await fetch(url, options); let result;
        try { result = await response.json(); } catch (_) { throw new Error('O servidor não respondeu como esperado.'); }
        if (!response.ok || result.error) { const error = new Error(result.error || 'Consulta indisponível.'); error.rejected = response.status >= 400 && response.status < 500; throw error; }
        return result;
    }
    function selected() { return tokens.find(token => token.tokenId === byId('management-token').value); }
    function controls() {
        const token = selected(), operation = byId('management-operation').value, quantity = operation === 'mint' || operation === 'burn';
        byId('management-amount-field').hidden = !quantity; byId('management-amount').required = quantity; byId('management-amount').disabled = !quantity;
        byId('management-destination-field').hidden = operation !== 'mint'; byId('management-destination').required = operation === 'mint'; byId('management-destination').disabled = operation !== 'mint';
        byId('management-fields').disabled = busy || Boolean(draft); byId('management-prepare').disabled = busy || Boolean(draft) || !wallet.isUnlocked() || !token;
        byId('management-sign').disabled = busy || !draft || !wallet.isUnlocked(); byId('management-edit').disabled = busy;
        byId('management-review').hidden = !draft; window.PovixWalletMenu?.setBusy(busy || Boolean(draft));
        const options = byId('management-operation').options;
        if (token) {
            options[0].disabled = !token.mintable || token.paused; options[1].disabled = !token.burnable || token.paused;
            options[2].disabled = !token.pausable || token.paused; options[3].disabled = !token.pausable || !token.paused;
        }
    }
    function reset() { draft = null; expected = null; signatures = null; administratorSignatures = null; controls(); }
    function policy() {
        const token = selected();
        byId('management-policy').textContent = token ? 'Oferta atual: ' + wallet.formatAtomic(token.supplyAtomic, token.decimals) + ' ' + token.symbol +
            ' · Emissão acumulada: ' + wallet.formatAtomic(token.issuedAtomic, token.decimals) + ' · Limite: ' + wallet.formatAtomic(token.maximumSupplyAtomic, token.decimals) +
            ' · Total queimado: ' + wallet.formatAtomic(token.burnedAtomic || '0', token.decimals) +
            ' · Queima por transferência direta: ' + (token.automaticBurnBasisPoints ? wallet.formatAtomic(String(token.automaticBurnBasisPoints), 2) + '%' : BigInt(token.automaticBurnFixedAtomic || '0') > 0n ? wallet.formatAtomic(token.automaticBurnFixedAtomic, token.decimals) + ' tokens' : 'desabilitada') +
            ' · ' + (token.paused ? 'Pausado' : 'Ativo') + ' · ' + token.requiredSignatures + ' de ' + token.administrators.length + ' assinaturas administrativas · Administradores: ' + token.administrators.join(', ') : '';
        controls();
    }
    async function catalogue() {
        const value = byId('management-token').value;
        tokens = await request(app.dataset.tokensUrl); byId('management-token').replaceChildren();
        const placeholder = document.createElement('option'); placeholder.value = ''; placeholder.textContent = tokens.length ? 'Selecione o token' : 'Nenhum token com política confirmado'; byId('management-token').append(placeholder);
        for (const token of tokens) { const option = document.createElement('option'); option.value = token.tokenId; option.textContent = token.name + ' (' + token.symbol + ') · ' + token.tokenId; byId('management-token').append(option); }
        if (tokens.some(token => token.tokenId === value)) byId('management-token').value = value;
        policy();
    }
    form.addEventListener('change', policy);
    form.addEventListener('submit', async event => {
        event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !form.reportValidity()) return;
        busy = true; message(''); controls();
        try {
            const token = selected(), operation = byId('management-operation').value, priority = byId('management-priority').value;
            if (!token || Array.from(byId('management-operation').options).find(option => option.value === operation)?.disabled) throw new Error('Esta operação não é permitida pelo estado do token.');
            const network = await request(app.dataset.networkUrl);
            if (!network.CanCreate) throw new Error('Aguarde a conexão e a sincronização com a rede.');
            expected = Object.freeze({ token: JSON.parse(JSON.stringify(token)), operation,
                amountAtomic: operation === 'mint' || operation === 'burn' ? wallet.parseSupply(byId('management-amount').value, token.decimals) : '0',
                destination: operation === 'mint' ? byId('management-destination').value : '', changeAddress: wallet.addresses()[0],
                feeAtomic: String(network.Fees[{ '1': 0, '2': 1, '4': 2 }[priority]]) });
            const result = await request(app.dataset.prepareUrl, { tokenId: token.tokenId, operation, amount: byId('management-amount').value,
                destination: expected.destination, feePriority: priority, publicKeys: wallet.publicKeys(), changeAddress: expected.changeAddress });
            await wallet.verifyManagementDraft(result, expected, app.dataset.networkId); draft = Object.freeze(result);
            const summary = byId('management-summary'); summary.replaceChildren();
            const rows = [['Token', token.name + ' (' + token.symbol + ') · ' + token.tokenId], ['Operação', { mint: 'Emissão adicional', burn: 'Queima permanente', pause: 'Pausar transferências e pools', resume: 'Retomar transferências e pools' }[operation]],
                ['Quantidade', wallet.formatAtomic(expected.amountAtomic, token.decimals)], ['Destino', expected.destination || 'Não se aplica'], ['Taxa', wallet.formatAtomic(draft.feeAtomic, 8) + ' POVIX'],
                ['Assinantes administrativos', draft.administratorAddresses.join(', ') || 'Queima autorizada pelo titular']];
            for (const [label, value] of rows) { const row = document.createElement('div'), dt = document.createElement('dt'), dd = document.createElement('dd'); dt.textContent = label; dd.textContent = value; row.append(dt, dd); summary.append(row); }
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; controls(); }
    });
    byId('management-edit').addEventListener('click', reset);
    byId('management-sign').addEventListener('click', async () => {
        if (busy || !draft || !wallet.isUnlocked()) return;
        busy = true; controls(); message('');
        try {
            await wallet.verifyManagementDraft(draft, expected, app.dataset.networkId);
            if (!signatures) signatures = await wallet.sign(draft);
            if (!administratorSignatures) administratorSignatures = await wallet.signManagementAdministrators(draft);
            const result = await request(app.dataset.submitUrl, { draftId: draft.draftId, signatures, administratorSignatures });
            if (!/^[0-9a-f]{64}$/.test(result.transactionId)) throw new Error('Identificador de operação inválido.');
            receipt = result.transactionId;
            try { sessionStorage.setItem('povix-token-management-receipt', receipt); } catch (_) { /* Status remains available in this tab. */ }
            reset(); await status();
        } catch (error) { if (error.rejected) reset(); message(error.message); }
        finally { busy = false; controls(); }
    });
    async function status() {
        if (!receipt || polling) return; polling = true;
        try {
            const url = new URL(app.dataset.statusUrl, location.origin); url.searchParams.set('id', receipt);
            const result = await request(url.href), node = byId('management-status'); node.hidden = false;
            node.textContent = 'Transação ' + receipt + ' · ' + ({ pending: 'Pendente', validated: 'Validada, aguardando bloco', confirmed: 'Confirmada', removed: 'Retirada da fila, não confirmada' }[result.status] || 'Desconhecida') +
                ' · Confirmações: ' + result.confirmations + (result.blockHash ? ' · Bloco ' + result.blockHeight + ' · ' + result.blockHash : '');
            if (result.status === 'confirmed' || result.status === 'removed') await catalogue();
        } catch (error) { message(error.message); } finally { polling = false; }
    }
    document.addEventListener('povix-wallet-change', () => { reset(); if (wallet.isUnlocked() && !byId('management-destination').value) byId('management-destination').value = wallet.addresses()[0]; });
    try { const saved = sessionStorage.getItem('povix-token-management-receipt'); if (/^[0-9a-f]{64}$/.test(saved || '')) receipt = saved; } catch (_) { /* Session persistence is optional. */ }
    catalogue().catch(error => message(error.message)); controls(); status(); setInterval(status, 5000);
})();
