(function () {
    'use strict';
    const app = document.getElementById('transfer-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet, byId = id => document.getElementById(id), form = byId('transfer-form');
    const csrf = form.querySelector('[name="__RequestVerificationToken"]').value;
    let network = null, tokens = [], balance = null, draft = null, expected = null, signed = null;
    let busy = false, refreshing = false, choices = null, backupDownloaded = false, walletEpoch = 0;
    function message(text) { byId('app-message').textContent = text || ''; byId('app-message').hidden = !text; }
    async function post(url, values) {
        const body = new URLSearchParams({ __RequestVerificationToken: csrf });
        Object.entries(values).forEach(([key, value]) => {
            if (Array.isArray(value)) value.forEach(item => body.append(key, item));
            else body.append(key, value);
        });
        const response = await fetch(url, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
        let result;
        try { result = await response.json(); } catch (_) { throw new Error('O servidor não respondeu como esperado. Recarregue a página.'); }
        if (!response.ok || result.error) {
            const code = typeof result.code === 'string' && /^[a-z_]{1,40}$/.test(result.code) ? ' (código: ' + result.code + ')' : '';
            const error = new Error((result.error || 'Não foi possível concluir a operação.') + code);
            error.rejected = response.status >= 400 && response.status < 500;
            throw error;
        }
        return result;
    }
    const selected = () => tokens.find(token => token.tokenId === byId('TokenId').value);
    function fee() {
        const option = form.querySelector('[name="FeePriority"]:checked');
        const index = option && { '1': 0, '2': 1, '4': 2 }[option.value];
        if (!network || !Array.isArray(network.Fees) || index == null) return null;
        const value = String(network.Fees[index]);
        return /^[0-9]+$/.test(value) && BigInt(value) >= 1n && BigInt(value) <= 100000000n ? value : null;
    }
    function tokenBalanceIssue(token) {
        if (BigInt(token.confirmedAtomic) > 0n) return 'O saldo confirmado deste token está reservado por um envio pendente. Aguarde a confirmação ou a atualização da cadeia.';
        if (Array.isArray(token.receivingAddresses)) {
            const missing = token.receivingAddresses.some(item => !wallet.addresses().includes(item.address) && BigInt(item.confirmedAtomic) > 0n);
            if (missing) return 'Há saldo do token no endereço que recebeu a emissão, mas a chave desse endereço não está na carteira aberta. Se ele foi gerado no DEX, abra o backup .povixwallet atualizado daquela criação e selecione a carteira do criador. O wallets.dat do Desktop não contém as chaves geradas no navegador.';
            return 'Não há saldo disponível deste token nas chaves da carteira aberta. Confira os endereços que receberam a emissão e os envios já realizados.';
        }
        return 'Não há saldo disponível deste token nas chaves da carteira aberta. Se o endereço de recebimento foi gerado no DEX, abra o backup .povixwallet atualizado que contém essa chave.';
    }
    function reviewIssue() {
        if (!wallet.isUnlocked()) return 'Abra a carteira do criador para continuar.';
        if (!network || !network.CanCreate) return 'Aguarde a conexão e a sincronização da rede.';
        if (balance === null) return 'Não foi possível consultar os saldos. Aguarde a atualização da carteira.';
        if (!tokens.length) return 'Nenhum token confirmado foi criado com as chaves desta carteira.';
        const token = selected();
        if (!token) return 'Selecione um token criado por esta carteira.';
        if (BigInt(token.balanceAtomic) === 0n) return tokenBalanceIssue(token);
        if (!byId('Amount').value) return 'Informe a quantidade do token que deseja enviar.';
        let amount;
        try { amount = wallet.parseSupply(byId('Amount').value, token.decimals); } catch (error) { return error.message; }
        if (BigInt(amount) > BigInt(token.balanceAtomic)) return 'A quantidade ultrapassa o saldo disponível do token: ' + wallet.formatAtomic(token.balanceAtomic, token.decimals) + ' ' + token.symbol + '.';
        if (!/^[0-9a-f]{64}$/.test(byId('DestinationAddress').value)) return 'Informe um endereço de recebimento válido com 64 caracteres.';
        const selectedFee = fee();
        if (selectedFee === null) return 'Selecione uma taxa disponível e aguarde sua atualização pela rede.';
        if (BigInt(balance) < BigInt(selectedFee)) return 'POVIX disponível para taxas: ' + wallet.formatAtomic(balance, 8) +
            '. A taxa selecionada exige ' + wallet.formatAtomic(selectedFee, 8) + ' POVIX. Valores reservados ou bloqueados como garantia não ficam disponíveis para a taxa.';
        return null;
    }
    function update() {
        const unlocked = wallet.isUnlocked(), token = selected(), ready = Boolean(network && network.CanCreate);
        byId('transfer-fields').disabled = busy || Boolean(draft) || !unlocked || !tokens.length;
        // A click can explain field/balance problems; preparation still checks every requirement.
        byId('prepare-transfer').disabled = busy || Boolean(draft) || !unlocked || !ready;
        byId('wallet-connect').disabled = busy || Boolean(choices && !byId('wallet-choice').value);
        byId('wallet-file').disabled = busy; byId('wallet-password').disabled = busy || Boolean(choices);
        byId('wallet-choice').disabled = busy || !choices; byId('wallet-lock').disabled = busy;
        byId('sign-transfer').disabled = busy || !draft || !unlocked || !ready || !backupDownloaded || !byId('backup-confirmed').checked;
        byId('edit-transfer').disabled = busy; byId('download-backup').disabled = busy;
        byId('transfer-hint').textContent = busy ? 'Processando no seu dispositivo…' : draft ? 'Confira o resumo e assine.' : reviewIssue() || 'Revise a movimentação antes de assinar.';
        byId('review-name').textContent = draft ? draft.name : token ? token.name : 'Seu token';
        byId('review-symbol').textContent = draft ? draft.symbol : token ? token.symbol : 'SÍMBOLO';
        byId('review-avatar').textContent = (draft ? draft.symbol : token ? token.symbol : '◇').charAt(0);
        let amount = '—';
        try {
            if (draft) amount = wallet.formatAtomic(draft.amountAtomic, draft.decimals) + ' ' + draft.symbol;
            else if (token) amount = wallet.formatAtomic(wallet.parseSupply(byId('Amount').value, token.decimals), token.decimals) + ' ' + token.symbol;
        } catch (_) { }
        byId('review-amount').textContent = amount;
        byId('review-fee').textContent = draft ? wallet.formatAtomic(draft.feeAtomic, 8) + ' POVIX' : fee() === null ? 'Consultando a taxa…' : wallet.formatAtomic(fee(), 8) + ' POVIX';
        byId('review-token-change').textContent = draft ? wallet.formatAtomic(draft.tokenChangeAtomic, draft.decimals) + ' ' + draft.symbol : 'Calculado na revisão';
        byId('review-povix-change').textContent = draft ? wallet.formatAtomic(draft.povixChangeAtomic, 8) + ' POVIX' : 'Calculado na revisão';
        byId('review-destination').textContent = draft ? draft.destinationAddress : byId('DestinationAddress').value || 'Informe o endereço';
        byId('review-change-address').textContent = draft ? draft.changeAddress : 'Da carteira aberta';
        byId('token-balance').textContent = token ? 'Disponível: ' + wallet.formatAtomic(token.balanceAtomic, token.decimals) + ' ' + token.symbol +
            ' · Confirmado: ' + wallet.formatAtomic(token.confirmedAtomic, token.decimals) + ' ' + token.symbol : 'Somente tokens confirmados são listados.';
        byId('token-identifier').textContent = token ? token.tokenId : '';
        const receiving = byId('token-receiving-addresses');
        receiving.replaceChildren();
        if (unlocked && token && Array.isArray(token.receivingAddresses)) token.receivingAddresses.forEach(item => {
            const entry = document.createElement('small');
            entry.textContent = 'Endereço que recebeu a emissão: ' + item.address + ' · Saldo confirmado nesse endereço: ' +
                wallet.formatAtomic(item.confirmedAtomic, token.decimals) + ' ' + token.symbol + ' · ' +
                (wallet.addresses().includes(item.address) ? 'Chave presente na carteira aberta.' : 'Chave ausente na carteira aberta.');
            receiving.appendChild(entry);
        });
        byId('amount-help').textContent = token ? token.decimals + ' casas decimais. Use vírgula ou ponto, sem separador de milhar.' : 'A precisão segue o token selecionado.';
    }
    function resetDraft() {
        draft = null; expected = null; signed = null; backupDownloaded = false;
        byId('backup-confirmed').checked = false; byId('sign-actions').hidden = true; update();
    }
    async function history() {
        const token = selected(), epoch = walletEpoch;
        byId('transfer-history').replaceChildren();
        if (!token || !wallet.isUnlocked()) return;
        try {
            const entries = await post(app.dataset.historyUrl, { publicKeys: wallet.publicKeys(), tokenId: token.tokenId });
            if (epoch !== walletEpoch || !selected() || selected().tokenId !== token.tokenId) return;
            const links = entries.map(entry => {
                if (!/^[0-9a-f]{64}$/.test(entry.transactionId)) throw new Error('Comprovante inválido.');
                const url = new URL(app.dataset.receiptUrl.replace('__id__', entry.transactionId), location.origin);
                if (url.origin !== location.origin) throw new Error('Comprovante inválido.');
                const link = document.createElement('a'); link.href = url.href;
                const status = entry.status === 'confirmed' ? 'Confirmado' : entry.status === 'pending' ? 'Pendente' : 'Não confirmado';
                link.textContent = wallet.formatAtomic(entry.amountAtomic, token.decimals) + ' ' + token.symbol + ' · ' + status;
                const address = document.createElement('small'); address.textContent = entry.destinationAddress; link.appendChild(address);
                return link;
            });
            byId('transfer-history').replaceChildren(...links);
            byId('history-empty').hidden = links.length > 0;
            byId('history-empty').textContent = 'Nenhum envio deste token foi registrado neste DEX.';
        } catch (_) { if (epoch === walletEpoch) { byId('history-empty').hidden = false; byId('history-empty').textContent = 'Consulta dos envios indisponível no momento.'; } }
    }
    async function refreshWallet() {
        if (!wallet.isUnlocked()) return;
        const epoch = walletEpoch, addresses = wallet.addresses(), publicKeys = wallet.publicKeys();
        try {
            const [funds, list] = await Promise.all([post(app.dataset.balanceUrl, { addresses }), post(app.dataset.tokensUrl, { publicKeys })]);
            if (epoch !== walletEpoch || !wallet.isUnlocked()) return;
            balance = funds.balanceAtomic;
            byId('wallet-balance').textContent = wallet.formatAtomic(balance, 8) + ' POVIX';
            byId('wallet-balance-detail').hidden = funds.confirmedAtomic == null;
            byId('wallet-balance-detail').textContent = funds.confirmedAtomic == null ? '' : 'Confirmado: ' + wallet.formatAtomic(funds.confirmedAtomic, 8) +
                ' POVIX · Reservado: ' + wallet.formatAtomic(funds.reservedAtomic, 8) + ' POVIX · A receber após confirmação: ' + wallet.formatAtomic(funds.pendingIncomingAtomic, 8) + ' POVIX';
            const current = byId('TokenId').value || app.dataset.tokenId;
            tokens = list.filter(token => /^[0-9a-f]{64}$/.test(token.tokenId) && addresses.includes(token.creatorAddress));
            if (!draft) {
                const select = byId('TokenId'); select.replaceChildren(new Option('Selecione um token', ''));
                tokens.forEach(token => select.add(new Option(token.symbol + ' · ' + token.name + ' · ' + token.tokenId.slice(0, 8), token.tokenId)));
                select.value = tokens.some(token => token.tokenId === current) ? current : '';
            }
            await history();
        } catch (error) {
            if (epoch !== walletEpoch) return;
            balance = null; tokens = [];
            byId('wallet-balance').textContent = 'Consulta indisponível'; byId('wallet-balance-detail').hidden = true;
            message(error.message);
        }
        update();
    }
    async function refreshNetwork() {
        if (refreshing) return;
        refreshing = true;
        try {
            const response = await fetch(app.dataset.networkUrl, { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) throw new Error('Rede indisponível');
            network = await response.json();
            byId('network-state').textContent = network.CanCreate ? 'Rede conectada' : network.PeerCount ? 'Sincronizando' : 'Sem pares conectados';
            byId('network-state').classList.toggle('waiting', !network.CanCreate);
            byId('network-peers').textContent = String(network.PeerCount); byId('network-height').textContent = String(network.Height);
            document.querySelectorAll('[data-fee]').forEach(element => { element.textContent = wallet.formatAtomic(String(network.Fees[Number(element.dataset.fee)]), 8) + ' POVIX'; });
            if (!busy) await refreshWallet();
        } catch (_) { network = null; byId('network-state').textContent = 'Conexão indisponível'; }
        finally { refreshing = false; update(); }
    }
    function resetSelection() {
        choices = null; byId('wallet-selection').hidden = true;
        byId('wallet-choice').replaceChildren(new Option('Selecione uma carteira', '')); byId('wallet-choice').required = false;
        byId('wallet-password-field').hidden = false; byId('wallet-password').required = true;
        byId('wallet-connect').textContent = 'Ler carteiras do arquivo';
    }
    function lock() {
        wallet.lock(); walletEpoch++; balance = null; tokens = []; resetDraft(); resetSelection();
        byId('wallet-state').textContent = 'Bloqueada'; byId('wallet-form').hidden = false; byId('wallet-connected').hidden = true;
        byId('TokenId').replaceChildren(new Option('Abra a carteira para consultar', ''));
        byId('transfer-history').replaceChildren(); byId('history-empty').hidden = false;
        byId('history-empty').textContent = 'Selecione um token para consultar os últimos 20 envios registrados neste DEX.'; update();
    }
    byId('wallet-form').addEventListener('submit', async event => {
        event.preventDefault(); if (busy) return;
        busy = true; message(''); update();
        const password = byId('wallet-password').value; byId('wallet-password').value = '';
        try {
            if (!choices) {
                const file = byId('wallet-file').files[0];
                choices = file ? await wallet.readFile(file, password) : await wallet.readSaved(password);
                byId('wallet-choice').replaceChildren(new Option('Selecione uma carteira', ''));
                choices.forEach(item => {
                    const option = new Option(item.name + (item.addressCount ? ' (' + item.addressCount + ' endereços)' : ' (sem endereços)'), item.id);
                    option.disabled = item.addressCount === 0; byId('wallet-choice').add(option);
                });
                byId('wallet-choice').required = true; byId('wallet-selection').hidden = false;
                byId('wallet-password-field').hidden = true; byId('wallet-password').required = false;
                byId('wallet-connect').textContent = 'Abrir carteira selecionada';
                byId('wallet-choice-help').textContent = 'Escolha a carteira que criou o token. Carteiras sem endereços não podem assinar.';
                return;
            }
            const info = await wallet.selectWallet(byId('wallet-choice').value);
            walletEpoch++; resetDraft(); balance = null; tokens = [];
            byId('wallet-name').textContent = info.name; byId('wallet-state').textContent = 'Assinatura local';
            byId('wallet-form').hidden = true; byId('wallet-connected').hidden = false; byId('wallet-file').value = '';
            await refreshWallet();
        } catch (error) { message(error.message); }
        finally { busy = false; update(); }
    });
    byId('wallet-file').addEventListener('change', () => { lock(); message(''); });
    byId('wallet-choice').addEventListener('change', update); byId('wallet-lock').addEventListener('click', lock);
    byId('TokenId').addEventListener('change', () => { resetDraft(); update(); history(); });
    function fieldsChanged() { if (!draft) { message(''); update(); } }
    form.addEventListener('input', fieldsChanged);
    form.addEventListener('change', fieldsChanged);
    // Native HTML validation runs before submit; expose its reason in the page too.
    form.addEventListener('invalid', () => { if (!draft) message(reviewIssue() || 'Confira os campos da movimentação.'); }, true);
    form.addEventListener('submit', async event => {
        event.preventDefault(); if (busy || draft) return;
        const issue = reviewIssue();
        if (issue) { message(issue); form.reportValidity(); return; }
        if (!form.reportValidity()) return;
        const token = selected(); if (!token || !wallet.isUnlocked()) return;
        busy = true; message(''); update();
        try {
            expected = Object.freeze({ TokenId: token.tokenId, Amount: byId('Amount').value, DestinationAddress: byId('DestinationAddress').value,
                FeePriority: form.querySelector('[name="FeePriority"]:checked').value, FeeAtomic: fee(), Name: token.name, Symbol: token.symbol,
                Decimals: token.decimals, CreatorAddress: token.creatorAddress });
            wallet.parseSupply(expected.Amount, expected.Decimals);
            const changeAddress = wallet.addresses()[0];
            const result = await post(app.dataset.prepareUrl, { TokenId: expected.TokenId, Amount: expected.Amount,
                DestinationAddress: expected.DestinationAddress, FeePriority: expected.FeePriority, FeeAtomic: expected.FeeAtomic, publicKeys: wallet.publicKeys(), changeAddress });
            await wallet.verifyTransferDraft(result, expected, app.dataset.networkId, changeAddress);
            draft = Object.freeze(result); byId('sign-actions').hidden = false; update();
            byId('review-title').scrollIntoView({ behavior: 'smooth', block: 'center' });
        } catch (error) { resetDraft(); message(error.message); }
        finally { busy = false; update(); }
    });
    byId('download-backup').addEventListener('click', () => { try { wallet.backup(); backupDownloaded = true; update(); } catch (error) { message(error.message); } });
    byId('backup-confirmed').addEventListener('change', update);
    byId('edit-transfer').addEventListener('click', () => { resetDraft(); message(''); });
    byId('sign-transfer').addEventListener('click', async () => {
        if (busy || !draft || !backupDownloaded || !byId('backup-confirmed').checked) return;
        busy = true; message(''); update();
        try {
            if (!signed) {
                await wallet.verifyTransferDraft(draft, expected, app.dataset.networkId, draft.changeAddress);
                signed = { signatures: await wallet.sign(draft), creatorSignature: await wallet.authorizeTransfer(draft) };
            }
            const result = await post(app.dataset.submitUrl, { draftId: draft.draftId, ...signed });
            const url = new URL(result.receiptUrl, location.origin);
            if (url.origin !== location.origin) throw new Error('Comprovante inválido.');
            wallet.lock(); location.assign(url.href);
        } catch (error) {
            if (error.rejected) resetDraft();
            message(error.message + (error.rejected ? '' : ' Se o envio foi interrompido, repita o mesmo envio para recuperar o comprovante.'));
        } finally { busy = false; update(); }
    });
    if (wallet.hasSavedWallet()) byId('wallet-file-help').textContent = 'Há um arquivo cifrado salvo neste navegador. Informe a senha ou escolha outro arquivo.';
    update(); refreshNetwork(); setInterval(refreshNetwork, 10000);
})();
