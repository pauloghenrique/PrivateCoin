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
            FeePriority: form.querySelector('[name="FeePriority"]:checked').value,
            UsePolicy: true, Mintable: byId('Mintable').value, MaximumSupply: byId('MaximumSupply').value,
            Burnable: byId('Burnable').checked, Pausable: byId('Pausable').checked,
            AdministratorAddresses: byId('AdministratorAddresses').value, RequiredSignatures: byId('RequiredSignatures').value,
            Description: byId('Description').value, LogoUrl: byId('LogoUrl').value, Website: byId('Website').value, SocialUrl: byId('SocialUrl').value,
            VestingStartHeight: byId('vesting-enabled').value === 'true' ? byId('VestingStartHeight').value : '0',
            VestingIntervalBlocks: byId('vesting-enabled').value === 'true' ? byId('VestingIntervalBlocks').value : '0',
            VestingTranches: byId('vesting-enabled').value === 'true' ? byId('VestingTranches').value : '0', Privacy: byId('Privacy').value };
    }
    function policyValues(data) {
        const admins = data.AdministratorAddresses.trim() ? data.AdministratorAddresses.split(/[,;\s]+/).filter(Boolean) : [wallet.addresses()[0]];
        if (admins.length < 1 || admins.length > 16 || new Set(admins).size !== admins.length || admins.some(address => !/^[0-9a-f]{64}$/.test(address)) ||
            Number(data.RequiredSignatures) > admins.length) throw new Error('Confira os administradores e o número de assinaturas.');
        const supply = wallet.parseSupply(data.Supply, Number(data.Decimals));
        const maximum = data.Mintable === 'true' ? wallet.parseSupply(data.MaximumSupply, Number(data.Decimals)) : supply;
        if (BigInt(maximum) < BigInt(supply)) throw new Error('O limite máximo deve incluir a quantidade inicial.');
        if (Number(data.VestingTranches) > 0 && (Number(data.VestingStartHeight) <= Number(network?.Height || 0) + 1 || BigInt(data.VestingTranches) > BigInt(supply) ||
            Number(data.VestingStartHeight) + (Number(data.VestingTranches) - 1) * Number(data.VestingIntervalBlocks) > 2147483647)) throw new Error('Confira o bloco inicial e as parcelas da liberação.');
        return [data.Mintable === 'true' ? '1' : '0', maximum, data.Burnable ? '1' : '0', data.Pausable ? '1' : '0', admins.join(','),
            String(Number(data.RequiredSignatures)), data.Description, data.LogoUrl, data.Website, data.SocialUrl,
            String(Number(data.VestingStartHeight)), String(Number(data.VestingIntervalBlocks)), String(Number(data.VestingTranches)), data.Privacy];
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
        const values = expected?.PolicyValues || null;
        const source = fields();
        byId('review-policy-badge').textContent = (values ? values[0] === '1' : source.Mintable === 'true') ? 'Emissão adicional' : 'Oferta fixa';
        const review = byId('review-policy'); review.replaceChildren();
        const rows = [
            ['Descrição', source.Description || 'Não informada'], ['Logo', source.LogoUrl || 'Não informado'], ['Site', source.Website || 'Não informado'], ['Redes sociais', source.SocialUrl || 'Não informadas'],
            ['Limite de emissão', source.Mintable === 'true' ? source.MaximumSupply : source.Supply],
            ['Queima pelo titular', source.Burnable ? 'Permitida' : 'Desabilitada'], ['Pausa administrativa', source.Pausable ? 'Permitida' : 'Desabilitada'],
            ['Administradores', source.AdministratorAddresses || (wallet.isUnlocked() ? wallet.addresses()[0] : 'Carteira conectada')],
            ['Assinaturas necessárias', source.RequiredSignatures], ['Liberação inicial', Number(source.VestingTranches) > 0 ? source.VestingTranches + ' parcelas a partir do bloco ' + source.VestingStartHeight + ', intervalo ' + source.VestingIntervalBlocks + ' blocos' : 'Após confirmação'],
            ['Privacidade', 'Valores e endereços públicos'], ['Regras permanentes', 'Oferta máxima, permissões, administradores e distribuição não poderão ser alterados.']];
        for (const [label, value] of rows) { const row = document.createElement('div'), term = document.createElement('dt'), detail = document.createElement('dd'); term.textContent = label; detail.textContent = value || '—'; row.append(term, detail); review.append(row); }
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
    function conditionalFields() {
        const mintable = byId('Mintable').value === 'true', vesting = byId('vesting-enabled').value === 'true';
        byId('maximum-supply-field').hidden = !mintable; byId('MaximumSupply').disabled = !mintable; byId('MaximumSupply').required = mintable;
        byId('vesting-fields').hidden = !vesting; byId('vesting-fields').disabled = !vesting;
    }
    form.addEventListener('change', () => { conditionalFields(); if (!draft) { updateReview(); updateControls(); } });
    conditionalFields();
    form.addEventListener('input', () => { if (!draft) { updateReview(); updateControls(); } });
    byId('Symbol').addEventListener('input', () => { byId('Symbol').value = byId('Symbol').value.toUpperCase(); updateReview(); });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || draft || !form.reportValidity()) return;
        busy = true; message(''); updateControls();
        try {
            const values = fields();
            expected = Object.freeze({ ...values, FeeAtomic: selectedFee(), PolicyValues: Object.freeze(policyValues(values)) });
            wallet.parseSupply(expected.Supply, Number(expected.Decimals));
            // Keep POVIX change under a key already present in the imported wallet.
            // A new browser-only key would hide the remaining funds from Desktop.
            if (!changeAddress) changeAddress = wallet.addresses()[0];
            if (!changeAddress) throw new Error('A carteira precisa ter um endereço para receber o troco.');
            const { PolicyValues, ...request } = expected;
            const result = await post(app.dataset.prepareUrl, { ...request, publicKeys: wallet.publicKeys(), changeAddress });
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
