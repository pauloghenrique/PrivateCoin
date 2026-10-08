(function () {
    'use strict';
    const app = document.getElementById('token-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet;
    const byId = id => document.getElementById(id);
    const form = byId('token-form');
    const csrf = form.querySelector('[name="__RequestVerificationToken"]').value;
    let network = null, draft = null, expected = null, changeAddress = null;
    let busy = false, refreshing = false, backupDownloaded = false, balance = null;
    let signedSignatures = null;
    let walletChoices = null;

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
            const error = new Error(result.error || 'Não foi possível concluir a operação.');
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
        const ready = Boolean(network && network.CanCreate);
        const enough = balance === null || BigInt(balance) >= BigInt(selectedFee());
        byId('prepare-token').disabled = busy || Boolean(draft) || !unlocked || !ready || !enough;
        byId('own-address').disabled = busy || Boolean(draft) || !unlocked;
        byId('wallet-connect').disabled = busy || (Boolean(walletChoices) && !byId('wallet-choice').value);
        byId('wallet-file').disabled = busy;
        byId('wallet-password').disabled = busy || Boolean(walletChoices);
        byId('wallet-choice').disabled = busy || !walletChoices;
        byId('wallet-lock').disabled = busy;
        byId('token-fields').disabled = busy || Boolean(draft);
        byId('sign-token').disabled = busy || !draft || !unlocked || !ready || !backupDownloaded || !byId('backup-confirmed').checked;
        byId('edit-token').disabled = busy;
        byId('download-backup').disabled = busy;
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
        try { byId('review-supply').textContent = wallet.formatAtomic(draft ? draft.supplyAtomic : wallet.parseSupply(data.Supply, decimals), decimals); }
        catch (_) { byId('review-supply').textContent = '—'; }
        byId('review-fee').textContent = wallet.formatAtomic(draft ? draft.feeAtomic : selectedFee(), 8) + ' POVIX';
        byId('precision-example').textContent = 'Menor unidade: ' + wallet.formatAtomic('1', decimals);
    }
    function resetDraft() {
        draft = null; expected = null; backupDownloaded = false; signedSignatures = null;
        byId('backup-confirmed').checked = false;
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
        } catch (_) { balance = null; byId('wallet-balance').textContent = 'Consulta indisponível'; }
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
    byId('wallet-form').addEventListener('submit', async event => {
        event.preventDefault();
        if (busy) return;
        busy = true; message(''); updateControls();
        const password = byId('wallet-password').value;
        byId('wallet-password').value = '';
        try {
            if (!walletChoices) {
                const file = byId('wallet-file').files[0];
                walletChoices = file ? await wallet.readFile(file, password) : await wallet.readSaved(password);
                const select = byId('wallet-choice');
                select.replaceChildren(new Option('Selecione uma carteira', ''));
                walletChoices.forEach((item, index) => select.add(new Option((index + 1) + ' · ' + item.name + ' (' + item.addressCount + ' endereços)', item.id)));
                select.required = true;
                byId('wallet-selection').hidden = false;
                byId('wallet-password-field').hidden = true;
                byId('wallet-password').required = false;
                byId('wallet-connect').textContent = 'Abrir carteira selecionada';
                byId('wallet-choice-help').textContent = walletChoices.length + (walletChoices.length === 1 ? ' carteira encontrada neste arquivo.' : ' carteiras encontradas neste arquivo.');
                return;
            }
            const info = await wallet.selectWallet(byId('wallet-choice').value);
            resetDraft(); changeAddress = null; balance = null;
            byId('wallet-name').textContent = info.name;
            byId('wallet-state').textContent = 'Assinatura local';
            byId('wallet-form').hidden = true;
            byId('wallet-connected').hidden = false;
            byId('wallet-file').value = '';
            await refreshBalance();
        } catch (error) { message(error.message); }
        finally { busy = false; updateControls(); }
    });
    function resetWalletSelection() {
        walletChoices = null;
        byId('wallet-selection').hidden = true;
        byId('wallet-choice').replaceChildren(new Option('Selecione uma carteira', ''));
        byId('wallet-choice').required = false;
        byId('wallet-password-field').hidden = false;
        byId('wallet-password').required = true;
        byId('wallet-connect').textContent = 'Ler carteiras do arquivo';
        updateControls();
    }
    byId('wallet-file').addEventListener('change', () => { wallet.lock(); resetWalletSelection(); message(''); });
    byId('wallet-choice').addEventListener('change', updateControls);
    byId('wallet-lock').addEventListener('click', () => {
        wallet.lock(); changeAddress = null; balance = null; resetDraft();
        resetWalletSelection();
        byId('wallet-state').textContent = 'Bloqueada';
        byId('wallet-form').hidden = false; byId('wallet-connected').hidden = true;
    });
    byId('own-address').addEventListener('click', async () => {
        if (busy) return;
        busy = true; message(''); updateControls();
        try { byId('DestinationAddress').value = await wallet.createAddress(); updateReview(); }
        catch (error) { message(error.message); }
        finally { busy = false; updateControls(); }
    });
    form.addEventListener('input', () => { if (!draft) { updateReview(); updateControls(); } });
    byId('Symbol').addEventListener('input', () => { byId('Symbol').value = byId('Symbol').value.toUpperCase(); updateReview(); });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || draft || !form.reportValidity()) return;
        busy = true; message(''); updateControls();
        try {
            expected = Object.freeze(fields());
            wallet.parseSupply(expected.Supply, Number(expected.Decimals));
            if (!changeAddress) changeAddress = await wallet.createAddress();
            const result = await post(app.dataset.prepareUrl, { ...expected, publicKeys: wallet.publicKeys(), changeAddress });
            await wallet.verifyDraft(result, expected, app.dataset.networkId, changeAddress);
            draft = Object.freeze(result);
            byId('sign-actions').hidden = false;
            byId('backup-confirmed').checked = false; backupDownloaded = false;
            byId('prepare-token').textContent = 'Criação preparada';
            updateReview();
            byId('review-title').scrollIntoView({ behavior: 'smooth', block: 'center' });
        } catch (error) { resetDraft(); message(error.message); }
        finally { busy = false; updateControls(); }
    });
    byId('download-backup').addEventListener('click', () => {
        try { wallet.backup(); backupDownloaded = true; updateControls(); }
        catch (error) { message(error.message); }
    });
    byId('backup-confirmed').addEventListener('change', updateControls);
    byId('edit-token').addEventListener('click', () => { resetDraft(); message(''); });
    byId('sign-token').addEventListener('click', async () => {
        if (busy || !draft || !backupDownloaded || !byId('backup-confirmed').checked) return;
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
            wallet.lock(); location.assign(receipt.href);
        } catch (error) {
            if (error.rejected) resetDraft();
            message(error.message + (error.rejected ? '' : ' Se o envio foi interrompido, repita o mesmo envio para recuperar o comprovante.'));
        } finally { busy = false; byId('sign-token').textContent = 'Assinar e enviar à rede'; updateControls(); }
    });
    if (wallet.hasSavedWallet()) byId('wallet-file-help').textContent = 'Há um arquivo cifrado salvo neste navegador. Informe a senha para listar suas carteiras ou escolha outro arquivo.';
    refreshNetwork();
    setInterval(refreshNetwork, 10000);
})();
