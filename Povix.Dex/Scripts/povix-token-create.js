(function () {
    'use strict';
    const app = document.getElementById('token-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet;
    const byId = id => document.getElementById(id);
    const form = byId('token-form');
    const csrf = form.querySelector('[name="__RequestVerificationToken"]').value;
    let network = null, draft = null, expected = null, changeAddress = null;
    let busy = false, refreshing = false, balance = null;
    let signedSignatures = null;
    let walletDestination = null;

    function message(text) { byId('app-message').textContent = text || ''; byId('app-message').hidden = !text; }
    async function post(url, values) {
        const body = new URLSearchParams({ __RequestVerificationToken: csrf });
        Object.entries(values).forEach(([key, value]) => {
            if (Array.isArray(value)) value.forEach(item => body.append(key, item));
            else body.append(key, value);
        });
        const response = await fetch(url, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
        let result;
        try { result = await response.json(); }
        catch (_) { throw new Error('O servidor não respondeu como esperado. Recarregue a página se a sessão expirou.'); }
        if (!response.ok || result.error) {
            const code = typeof result.code === 'string' && /^[a-z_]{1,40}$/.test(result.code) ? ' (código: ' + result.code + ')' : '';
            const error = new Error((result.error || 'Não foi possível concluir a operação.') + code);
            error.rejected = response.status >= 400 && response.status < 500;
            throw error;
        }
        return result;
    }
    function fields() {
        return { Name: byId('Name').value, Symbol: byId('Symbol').value, Supply: byId('Supply').value,
            Decimals: byId('Decimals').value, DestinationAddress: byId('DestinationAddress').value,
            FeePriority: form.querySelector('[name="FeePriority"]:checked').value };
    }
    function selectedFee() {
        if (!network) return '0';
        return String(network.Fees[{ '1': 0, '2': 1, '4': 2 }[form.querySelector('[name="FeePriority"]:checked').value]]);
    }
    function updateControls() {
        const unlocked = wallet.isUnlocked();
        window.PovixWalletMenu?.setBusy(busy || Boolean(draft));
        const ready = Boolean(network && network.CanCreate);
        const enough = balance === null || BigInt(balance) >= BigInt(selectedFee());
        byId('prepare-token').disabled = busy || Boolean(draft) || !unlocked || !ready || !enough;
        byId('own-address').disabled = busy || Boolean(draft) || !unlocked;
        byId('token-fields').disabled = busy || Boolean(draft);
        byId('sign-token').disabled = busy || !draft || !unlocked || !ready;
        byId('edit-token').disabled = busy;

        byId('create-hint').textContent = busy ? 'Processando no navegador…' : draft ? 'Revise o resumo e assine ao lado.' :
            !unlocked ? 'Desbloqueie sua carteira para continuar.' : !ready ? 'Aguarde a conexão e a sincronização com a rede.' :
                !enough ? 'A carteira precisa de POVIX confirmado para pagar a taxa.' : 'A próxima etapa permite revisar antes de assinar.';
    }
    function updateReview() {
        const data = draft || fields();
        const name = draft ? draft.name : data.Name, symbol = draft ? draft.symbol : data.Symbol;
        const decimals = draft ? draft.decimals : Number(data.Decimals);
        byId('review-name').textContent = name || 'Seu novo token';
        byId('review-symbol').textContent = symbol || 'SÍMBOLO';
        byId('review-avatar').textContent = symbol ? symbol.charAt(0) : '◇';
        byId('review-decimals').textContent = String(decimals);
        byId('review-destination').textContent = (draft ? draft.destinationAddress : data.DestinationAddress) || 'Informe o endereço de recebimento';
        const destination = draft ? draft.destinationAddress : data.DestinationAddress;
        const owned = wallet.isUnlocked() && wallet.addresses().includes(destination);
        const destinationHelp = owned ? 'Os tokens serão recebidos em um endereço já salvo na carteira aberta.' :
            /^[0-9a-f]{64}$/.test(destination) && wallet.isUnlocked() ?
                'Este endereço não pertence à carteira aberta. Para movimentar os tokens pelo DEX, ela precisará também da chave desse endereço.' :
                'Use um endereço da sua carteira para receber e depois movimentar o token.';
        byId('destination-help').textContent = destinationHelp;
        byId('review-destination-help').textContent = destinationHelp;
        try { byId('review-supply').textContent = wallet.formatAtomic(draft ? draft.supplyAtomic : wallet.parseSupply(data.Supply, decimals), decimals); }
        catch (_) { byId('review-supply').textContent = '—'; }
        byId('review-fee').textContent = wallet.formatAtomic(draft ? draft.feeAtomic : selectedFee(), 8) + ' POVIX';
        byId('review-change').textContent = draft ? wallet.formatAtomic(draft.changeAtomic, 8) + ' POVIX' : 'Calculado na revisão';
        byId('precision-example').textContent = 'Menor unidade: ' + wallet.formatAtomic('1', decimals);
    }
    function resetDraft() {
        draft = null; expected = null; signedSignatures = null;
        byId('sign-actions').hidden = true;
        byId('prepare-token').textContent = 'Revisar criação →';
        updateControls(); updateReview();
    }
    async function refreshBalance() {
        if (!wallet.isUnlocked()) return;
        try {
            const result = await post(app.dataset.balanceUrl, { addresses: wallet.addresses() });
            if (!wallet.isUnlocked()) return;
            balance = result.balanceAtomic;
            byId('wallet-balance').textContent = wallet.formatAtomic(balance, 8) + ' POVIX';
            const detail = byId('wallet-balance-detail');
            detail.hidden = result.confirmedAtomic == null;
            detail.textContent = detail.hidden ? '' : 'Confirmado: ' + wallet.formatAtomic(result.confirmedAtomic, 8) +
                ' POVIX · Taxas das operações pendentes: ' + wallet.formatAtomic(result.pendingFeesAtomic || '0', 8) +
                ' POVIX · Reservado (não é taxa): ' + wallet.formatAtomic(result.reservedAtomic, 8) +
                ' POVIX · A receber após confirmação: ' + wallet.formatAtomic(result.pendingIncomingAtomic, 8) + ' POVIX';
            if (BigInt(result.poolReservedAtomic || '0') > 0n) detail.textContent += ' · Em pools de liquidez: ' + wallet.formatAtomic(result.poolReservedAtomic, 8) + ' POVIX';
        } catch (_) {
            balance = null; byId('wallet-balance').textContent = 'Consulta indisponível';
            byId('wallet-balance-detail').hidden = true;
        }
        updateControls();
    }
    async function refreshNetwork() {
        if (refreshing) return;
        refreshing = true;
        try {
            const response = await fetch(app.dataset.networkUrl, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) throw new Error('Rede indisponível');
            network = await response.json();
            byId('network-peers').textContent = String(network.PeerCount);
            byId('network-height').textContent = String(network.Height);
            byId('network-state').textContent = network.CanCreate ? 'Rede conectada' : network.PeerCount ? 'Sincronizando' : 'Sem pares conectados';
            byId('network-state').classList.toggle('waiting', !network.CanCreate);
            document.querySelectorAll('[data-fee]').forEach(element => { element.textContent = wallet.formatAtomic(String(network.Fees[Number(element.dataset.fee)]), 8) + ' POVIX'; });
            if (network.Error) message(network.Error);
            await refreshBalance();
        } catch (_) {
            network = null;
            byId('network-state').textContent = 'Conexão indisponível';
            byId('network-state').classList.add('waiting');
        } finally { refreshing = false; updateControls(); updateReview(); }
    }
    document.addEventListener('povix-wallet-change', async () => {
        try {
            changeAddress = null; balance = null; resetDraft();
            if (wallet.isUnlocked()) {
                if (!byId('DestinationAddress').value || byId('DestinationAddress').value === walletDestination) useOwnDestination();
                await refreshBalance();
            }
            updateControls(); updateReview();
        } catch (error) { message(error.message); }
    });
    function useOwnDestination() {
        const address = wallet.addresses()[0];
        if (!address) throw new Error('A carteira precisa ter um endereço de recebimento salvo.');
        // Reuse a key in the imported file so Desktop and DEX recognize the issued balance.
        walletDestination = address;
        byId('DestinationAddress').value = address;
    }
    byId('own-address').addEventListener('click', () => {
        if (busy || draft || !wallet.isUnlocked()) return;
        message('');
        try { useOwnDestination(); updateReview(); }
        catch (error) { message(error.message); }
        updateControls();
    });
    form.addEventListener('input', () => { if (!draft) { updateReview(); updateControls(); } });
    byId('Symbol').addEventListener('input', () => { byId('Symbol').value = byId('Symbol').value.toUpperCase(); updateReview(); });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || draft || !form.reportValidity()) return;
        busy = true; message(''); updateControls();
        try {
            expected = Object.freeze({ ...fields(), FeeAtomic: selectedFee() });
            wallet.parseSupply(expected.Supply, Number(expected.Decimals));
            // Keep POVIX change under a key already present in the imported wallet.
            // A new browser-only key would hide the remaining funds from Desktop.
            if (!changeAddress) changeAddress = wallet.addresses()[0];
            if (!changeAddress) throw new Error('A carteira precisa ter um endereço para receber o troco.');
            const result = await post(app.dataset.prepareUrl, { ...expected, publicKeys: wallet.publicKeys(), changeAddress });
            await wallet.verifyDraft(result, expected, app.dataset.networkId, changeAddress);
            draft = Object.freeze(result);
            byId('sign-actions').hidden = false;
            byId('prepare-token').textContent = 'Criação preparada';
            updateReview();
            byId('review-title').scrollIntoView({ behavior: 'smooth', block: 'center' });
        } catch (error) { resetDraft(); message(error.message); }
        finally { busy = false; updateControls(); }
    });
    byId('edit-token').addEventListener('click', () => { resetDraft(); message(''); });
    byId('sign-token').addEventListener('click', async () => {
        if (busy || !draft || !wallet.isUnlocked() || !network?.CanCreate) return;
        busy = true; message(''); updateControls(); byId('sign-token').textContent = 'Assinando e enviando…';
        try {
            if (!signedSignatures) {
                await wallet.verifyDraft(draft, expected, app.dataset.networkId, changeAddress);
                signedSignatures = await wallet.sign(draft);
            }
            const result = await post(app.dataset.submitUrl, { draftId: draft.draftId, signatures: signedSignatures });
            // Only follow an application-local receipt URL.
            const receipt = new URL(result.receiptUrl, location.origin);
            if (receipt.origin !== location.origin) throw new Error('Endereço de comprovante inválido.');
            location.assign(receipt.href);
        } catch (error) {
            if (error.rejected) resetDraft();
            message(error.message + (error.rejected ? '' : ' Se o envio foi interrompido, repita o mesmo envio para recuperar o comprovante.'));
        } finally { busy = false; byId('sign-token').textContent = 'Confirmar transação'; updateControls(); }
    });
    refreshNetwork();
    setInterval(refreshNetwork, 10000);
})();
