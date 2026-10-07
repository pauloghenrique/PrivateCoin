(function () {
    'use strict';
    const form = document.getElementById('create-token');
    if (!form) return;
    const core = window.DEXPovixWallet;
    let wallet = null, password = '', prepared = null, backupReady = false, busy = false;
    const status = document.getElementById('operation-status');
    const prepareButton = document.getElementById('prepare-token');
    const submitButton = document.getElementById('submit-token');
    function message(text, failed) {
        status.textContent = text;
        status.className = 'alert ' + (failed ? 'alert-danger' : 'alert-info');
        status.hidden = false;
    }
    async function action(work) {
        if (busy) return;
        busy = true;
        const controls = Array.from(document.querySelectorAll('button'));
        controls.forEach(control => { control.disabled = true; });
        try { await work(); } catch (error) { message(error.message, true); }
        finally {
            busy = false;
            controls.forEach(control => { control.disabled = false; });
            prepareButton.disabled = !wallet || !!prepared;
            submitButton.disabled = !prepared || !backupReady;
        }
    }
    function showWallet() {
        document.getElementById('receive-address').textContent = wallet.receiveAddress;
        document.getElementById('wallet-connected').hidden = false;
        document.getElementById('wallet-setup').hidden = true;
    }
    async function downloadBackup() {
        const encrypted = await core.encrypt(wallet, password);
        const url = URL.createObjectURL(new Blob([JSON.stringify(encrypted)], { type: 'application/json' }));
        const link = document.createElement('a');
        link.href = url;
        link.download = 'DEXPovix-carteira-' + Date.now() + '.json';
        document.body.appendChild(link); link.click(); link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    }
    async function post(url, values) {
        const data = new URLSearchParams();
        data.set('__RequestVerificationToken', form.querySelector('[name="__RequestVerificationToken"]').value);
        Object.keys(values).forEach(key => {
            if (Array.isArray(values[key])) values[key].forEach((value, i) => data.append(key + '[' + i + ']', value));
            else data.set(key, values[key]);
        });
        const response = await fetch(url, { method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8' }, body: data.toString() });
        let value;
        try { value = await response.json(); } catch (_) { throw new Error('Resposta inesperada do servidor.'); }
        if (!response.ok) throw new Error(value.Error || 'Falha ao acessar a blockchain.');
        return value;
    }
    function cell(row, text, className) {
        const td = document.createElement('td'); td.textContent = text;
        if (className) td.className = className;
        row.appendChild(td);
    }
    async function refresh() {
        const response = await fetch(form.dataset.listUrl, { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) throw new Error('Não foi possível atualizar a lista de tokens.');
        const data = await response.json();
        document.getElementById('network-status').textContent = data.ConnectedPeers + ' pares · bloco ' + data.Height;
        document.getElementById('fee-estimate').textContent = data.Fee + ' POVIX';
        const tbody = document.getElementById('tokens-body'); tbody.replaceChildren();
        data.Tokens.forEach(token => {
            const row = document.createElement('tr');
            cell(row, token.Name); cell(row, token.Symbol); cell(row, token.Supply);
            cell(row, token.Decimals); cell(row, token.Id, 'identifier');
            cell(row, token.Status + (token.Height === null ? '' : ' · bloco ' + token.Height));
            cell(row, token.TransactionId, 'identifier'); tbody.appendChild(row);
        });
        document.getElementById('empty-tokens').hidden = data.Tokens.length !== 0;
    }
    document.getElementById('new-wallet').addEventListener('click', () => action(async () => {
        password = document.getElementById('backup-password').value;
        if (password.length < 12) throw new Error('Use uma senha de backup com pelo menos 12 caracteres.');
        message('Gerando a carteira e cifrando o backup…');
        const key = await core.newKey(); wallet = { keys: [key], receiveAddress: key.address };
        await downloadBackup(); showWallet();
        document.getElementById('backup-password').value = '';
        message('Guarde o backup e a senha. Envie POVIX para o endereço abaixo e aguarde a confirmação para pagar a taxa.');
    }));
    document.getElementById('open-wallet').addEventListener('click', () => action(async () => {
        const file = document.getElementById('wallet-file').files[0];
        if (!file || file.size > 1000000) throw new Error('Selecione um backup DEXPovix de até 1 MB.');
        const candidatePassword = document.getElementById('backup-password').value;
        const candidateWallet = await core.decrypt(JSON.parse(await file.text()), candidatePassword);
        wallet = candidateWallet; password = candidatePassword; showWallet();
        document.getElementById('backup-password').value = '';
        document.getElementById('wallet-file').value = '';
        message('Carteira aberta neste navegador.');
    }));
    document.getElementById('backup-wallet').addEventListener('click', () => action(async () => {
        if (!wallet) throw new Error('Abra uma carteira primeiro.');
        await downloadBackup(); backupReady = true;
        message('Backup cifrado atualizado baixado. Guarde este arquivo antes de assinar.');
    }));
    form.addEventListener('submit', event => {
        event.preventDefault();
        action(async () => {
            if (!wallet) throw new Error('Crie ou abra uma carteira primeiro.');
            if (prepared) throw new Error('Conclua ou cancele a preparação atual.');
            if (wallet.keys.length > 98) throw new Error('Esta carteira atingiu o limite de 100 endereços. Use uma nova carteira.');
            message('Preparando a criação na blockchain…');
            const recipient = await core.newKey(), change = await core.newKey();
            const request = { Name: form.elements.Name.value, Symbol: form.elements.Symbol.value,
                Decimals: form.elements.Decimals.value, Supply: form.elements.Supply.value,
                PublicKeys: wallet.keys.map(key => key.publicKey), Recipient: recipient.address, ChangeAddress: change.address };
            const result = await post(form.dataset.prepareUrl, request);
            // Persist the new output keys in the user's encrypted backup before any signature is sent.
            wallet.keys.push(recipient, change); wallet.receiveAddress = change.address;
            prepared = result; backupReady = false; showWallet();
            document.getElementById('preview').textContent = request.Name.trim() + ' (' + request.Symbol.toUpperCase() +
                ') · quantidade ' + result.Supply + ' · taxa ' + result.Fee + ' POVIX · ID ' + result.TokenId;
            document.getElementById('prepared-token').hidden = false;
            Array.from(form.querySelectorAll('input,select')).forEach(input => { if (input.type !== 'hidden') input.disabled = true; });
            await downloadBackup(); backupReady = true;
            message('Preparação válida por 5 minutos. Guarde o backup atualizado e confira os dados antes de assinar e enviar.');
        });
    });
    submitButton.addEventListener('click', () => action(async () => {
        if (!prepared || !backupReady) throw new Error('Baixe o backup atualizado antes de assinar.');
        message('Assinando localmente e enviando à rede…');
        const signatures = await core.sign(wallet, prepared.Payload, prepared.InputAddresses);
        const receipt = await post(form.dataset.submitUrl, { DraftId: prepared.DraftId, Signatures: signatures });
        clearPreparation();
        message('Criação enviada. Transação ' + receipt.TransactionId + '. Aguarde a confirmação dos validadores.');
        await refresh();
    }));
    function clearPreparation() {
        prepared = null; backupReady = false;
        document.getElementById('prepared-token').hidden = true;
        Array.from(form.querySelectorAll('input,select')).forEach(input => { input.disabled = false; });
    }
    document.getElementById('cancel-preparation').addEventListener('click', () => {
        clearPreparation(); prepareButton.disabled = !wallet; submitButton.disabled = true;
        message('Preparação cancelada. Nenhuma transação enviada.');
    });
    document.getElementById('refresh-tokens').addEventListener('click', () => action(refresh));
    setInterval(() => refresh().catch(() => {
        document.getElementById('network-status').textContent = 'Atualização indisponível; exibindo última consulta.';
    }), 15000);
}());
