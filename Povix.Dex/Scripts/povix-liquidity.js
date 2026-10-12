(function () {
    'use strict';
    const app = document.getElementById('liquidity-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet, byId = id => document.getElementById(id);
    const csrf = byId('pool-form').querySelector('[name="__RequestVerificationToken"]').value;
    let assets = [], pools = [], network = null, draft = null, expected = null, signatures = null;
    let choices = null, busy = false, refreshing = false, provider = false, backup = false, epoch = 0, requestId = null;
    const message = text => { byId('app-message').textContent = text || ''; byId('app-message').hidden = !text; };
    const asset = id => assets.find(item => item.id === id);
    const selectedPool = () => pools.find(item => item.poolId === byId('swap-pool').value);
    const preferredPool = /^[0-9a-f]{64}$/.test(app.dataset.poolId || '') ? app.dataset.poolId : null;
    const poolStates = { available: 'Disponível', busy: 'Reservas em uso por operação pendente', unavailable: 'Reservas indisponíveis',
        pending: 'Aguardando validação', validated: 'Validada, aguardando bloco', rejected: 'Criação não confirmada' };
    const atomicText = (value, decimals) => wallet.formatAtomic(String(value), decimals);
    function fee() {
        const index = { '1': 0, '2': 1, '4': 2 }[byId('exchange-priority').value];
        if (!network || !Array.isArray(network.Fees) || index == null) throw new Error('Aguarde a consulta da taxa da rede.');
        const value = String(network.Fees[index]);
        if (!/^[0-9]+$/.test(value) || BigInt(value) < 1n || BigInt(value) > 100000000n) throw new Error('Taxa inválida.');
        return value;
    }
    async function post(url, values) {
        const body = new URLSearchParams({ __RequestVerificationToken: csrf });
        Object.entries(values).forEach(([key, value]) => Array.isArray(value) ? value.forEach(item => body.append(key, item)) : body.append(key, value));
        const response = await fetch(url, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
        let result;
        try { result = await response.json(); } catch (_) { throw new Error('Resposta indisponível. Repita o mesmo envio se já tiver assinado.'); }
        if (!response.ok || result.error) {
            const error = new Error(result.error || 'Não foi possível concluir a operação.');
            error.rejected = response.status >= 400 && response.status < 500; throw error;
        }
        return result;
    }
    function reset() {
        draft = null; expected = null; signatures = null; provider = false; backup = false;
        byId('backup-confirmed').checked = false; byId('sign-actions').hidden = true;
        byId('exchange-review').replaceChildren(); byId('review-title').textContent = 'Revise sua operação'; update();
    }
    function options(id, entries, label, empty) {
        const element = byId(id), previous = element.value || (id === 'swap-pool' ? preferredPool : null);
        element.replaceChildren(new Option(empty, ''));
        entries.forEach(item => { const option = new Option(label(item), item.id || item.poolId); option.disabled = item.available === false; element.add(option); });
        if (entries.some(item => (item.id || item.poolId) === previous)) element.value = previous;
    }
    function update() {
        const unlocked = wallet.isUnlocked(), ready = unlocked && network && network.CanCreate, editing = !busy && !draft;
        byId('pool-fields').disabled = !editing || !unlocked;
        byId('swap-fields').disabled = !editing || !unlocked;
        byId('prepare-pool').disabled = !editing || !ready;
        byId('prepare-swap').disabled = !editing || !ready || Boolean(requestId) || !selectedPool()?.available;
        byId('exchange-priority').disabled = !editing;
        byId('sign-exchange').disabled = busy || !draft || !ready || !backup || !byId('backup-confirmed').checked;
        byId('edit-exchange').disabled = busy; byId('download-backup').disabled = busy;
        byId('wallet-connect').disabled = busy || Boolean(choices && !byId('wallet-choice').value);
        byId('wallet-file').disabled = busy; byId('wallet-password').disabled = busy || Boolean(choices);
        byId('wallet-choice').disabled = busy || !choices; byId('wallet-lock').disabled = busy;
        try { byId('exchange-fee').textContent = atomicText(fee(), 8) + ' POVIX'; } catch (_) { byId('exchange-fee').textContent = 'Consultando a taxa…'; }
        for (const side of ['a', 'b']) {
            const token = asset(byId('pool-asset-' + side).value);
            byId('pool-balance-' + side).textContent = token ? 'Saldo livre: ' + atomicText(token.balanceAtomic, token.decimals) + ' ' + token.symbol : 'Escolha um ativo';
        }
        const pool = selectedPool();
        byId('swap-pool-state').textContent = pool ? pool.statusMessage || poolStates[pool.status] || 'Consulte o comprovante da pool.' :
            pools.length ? 'Selecione uma pool. A lista informa a disponibilidade de cada uma.' : 'Crie uma pool e aguarde a confirmação das reservas em bloco.';
        byId('swap-provider').textContent = pool ? 'Provedor: ' + pool.providerAddress : '';
        if (pool) {
            byId('swap-side').options[0].textContent = pool.assetA.symbol + ' → ' + pool.assetB.symbol;
            byId('swap-side').options[1].textContent = pool.assetB.symbol + ' → ' + pool.assetA.symbol;
        }
        try {
            if (!pool) throw new Error();
            const side = Number(byId('swap-side').value), input = side === 0 ? pool.assetA : pool.assetB, output = side === 0 ? pool.assetB : pool.assetA;
            const amount = wallet.parseSupply(byId('swap-amount').value, input.decimals);
            const receive = wallet.quoteExchange(side === 0 ? pool.reserveAAtomic : pool.reserveBAtomic, side === 0 ? pool.reserveBAtomic : pool.reserveAAtomic, amount);
            byId('swap-estimate').textContent = 'Cotação estimada: ' + atomicText(receive, output.decimals) + ' ' + output.symbol + ' · Taxa da pool: 0,30%';
        } catch (_) { byId('swap-estimate').textContent = 'Escolha a pool e uma quantidade compatível com a liquidez.'; }
    }
    function addReview(label, value) {
        const row = document.createElement('div'), term = document.createElement('dt'), detail = document.createElement('dd');
        term.textContent = label; detail.textContent = value; row.className = 'review-destination'; row.append(term, detail); byId('exchange-review').appendChild(row);
    }
    async function review(result, wanted, asProvider) {
        await wallet.verifyExchangeDraft(result, wanted, app.dataset.networkId, asProvider);
        draft = result; expected = wanted; provider = asProvider; signatures = null; backup = false;
        const ownPool = result.operation === 'swap' && !asProvider && wallet.addresses().includes(result.providerAddress);
        byId('backup-confirmed').checked = false; byId('exchange-review').replaceChildren();
        byId('review-title').textContent = result.operation === 'pool' ? 'Criação da pool' : asProvider ? 'Aprovar troca da sua pool' : ownPool ? 'Executar troca' : 'Solicitar troca';
        byId('review-description').textContent = result.operation === 'pool' ? 'A pool ficará disponível após a confirmação das reservas em bloco.' :
            asProvider ? 'Sua assinatura autoriza somente esta troca atômica das reservas.' : ownPool ?
                'A carteira selecionada assina a troca e as reservas da pool. A operação será enviada à rede após sua confirmação.' :
                'A assinatura solicita a troca ao provedor. Aguarde a aprovação e a confirmação em bloco.';
        if (result.operation === 'swap') {
            const input = result.inputSide === 0 ? result.assetA : result.assetB, output = result.inputSide === 0 ? result.assetB : result.assetA;
            addReview('Quantidade entregue pelo comprador', atomicText(result.amountAtomic, input.decimals) + ' ' + input.symbol);
            addReview('Quantidade recebida pelo comprador', atomicText(result.receiveAtomic, output.decimals) + ' ' + output.symbol);
            addReview('Mínimo a receber', atomicText(result.minimumReceiveAtomic, output.decimals) + ' ' + output.symbol);
        }
        addReview('Taxa da rede paga pelo solicitante', atomicText(result.feeAtomic, 8) + ' POVIX');
        result.outputs.forEach((output, index) => {
            const token = output.assetId === 'povix' ? { decimals: 8, symbol: 'POVIX' } : output.assetId === result.assetA.id ? result.assetA : result.assetB;
            addReview(index < 2 ? 'Reserva ' + token.symbol : result.operation === 'swap' && index === 2 ? 'Recebimento do comprador' : 'Troco',
                atomicText(output.amountAtomic, token.decimals) + ' ' + token.symbol);
            addReview('Endereço', output.address);
        });
        byId('sign-exchange').textContent = result.operation === 'pool' ? 'Assinar e criar pool' : asProvider || ownPool ? 'Assinar e executar troca' : 'Assinar solicitação ao provedor';
        byId('sign-actions').hidden = false; update(); byId('review-title').scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
    function ensureFunds(requirements) {
        for (const [id, amount] of requirements) {
            const token = asset(id);
            if (!token || BigInt(token.balanceAtomic) < amount) throw new Error('Saldo livre insuficiente de ' + (token ? token.symbol : id) + '. Reservas da pool e entradas pendentes não estão disponíveis.');
        }
    }
    const wantedFromPool = pool => ({ poolId: pool.poolId, reserveTransactionId: pool.reserveTransactionId, providerAddress: pool.providerAddress,
        assetA: pool.assetA, assetB: pool.assetB, reserveAAtomic: pool.reserveAAtomic, reserveBAtomic: pool.reserveBAtomic });
    byId('pool-form').addEventListener('submit', async event => {
        event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !network || !network.CanCreate) return;
        busy = true; message(''); update();
        try {
            const a = asset(byId('pool-asset-a').value), b = asset(byId('pool-asset-b').value);
            if (!a || !b || a.id === b.id) throw new Error('Escolha dois ativos distintos.');
            const reserveAAtomic = wallet.parseSupply(byId('pool-amount-a').value, a.decimals), reserveBAtomic = wallet.parseSupply(byId('pool-amount-b').value, b.decimals);
            const feeAtomic = fee(), changeAddress = wallet.addresses()[0], funds = new Map([['povix', BigInt(feeAtomic)]]);
            for (const [id, value] of [[a.id, reserveAAtomic], [b.id, reserveBAtomic]]) funds.set(id, (funds.get(id) || 0n) + BigInt(value));
            ensureFunds(funds);
            const wanted = { operation: 'pool', assetA: a, assetB: b, reserveAAtomic, reserveBAtomic, feeAtomic, changeAddress };
            const result = await post(app.dataset.preparePoolUrl, { AssetA: a.id, AssetB: b.id, AmountA: byId('pool-amount-a').value,
                AmountB: byId('pool-amount-b').value, FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress });
            await review(result, wanted, false);
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; update(); }
    });
    byId('swap-form').addEventListener('submit', async event => {
        event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !network || !network.CanCreate) return;
        busy = true; message(''); update();
        try {
            const pool = selectedPool(); if (!pool) throw new Error('Selecione uma pool para a troca.');
            if (!pool.available) throw new Error(pool.statusMessage || 'A pool aguarda reservas confirmadas e disponíveis. Consulte seu comprovante.');
            const inputSide = Number(byId('swap-side').value), input = inputSide === 0 ? pool.assetA : pool.assetB, output = inputSide === 0 ? pool.assetB : pool.assetA;
            const amountAtomic = wallet.parseSupply(byId('swap-amount').value, input.decimals), minimumReceiveAtomic = wallet.parseSupply(byId('swap-minimum').value, output.decimals);
            const receive = wallet.quoteExchange(inputSide === 0 ? pool.reserveAAtomic : pool.reserveBAtomic, inputSide === 0 ? pool.reserveBAtomic : pool.reserveAAtomic, amountAtomic);
            if (BigInt(receive) < BigInt(minimumReceiveAtomic)) throw new Error('O mínimo a receber supera a cotação atual.');
            const feeAtomic = fee(), changeAddress = wallet.addresses()[0], funds = new Map([['povix', BigInt(feeAtomic)]]);
            funds.set(input.id, (funds.get(input.id) || 0n) + BigInt(amountAtomic)); ensureFunds(funds);
            const wanted = { ...wantedFromPool(pool), operation: 'swap', inputSide, amountAtomic, minimumReceiveAtomic, feeAtomic, changeAddress };
            const result = await post(app.dataset.prepareSwapUrl, { PoolId: pool.poolId, InputSide: inputSide, Amount: byId('swap-amount').value,
                MinimumReceive: byId('swap-minimum').value, FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress });
            await review(result, wanted, false);
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; update(); }
    });
    async function approveRequest(result) {
        if (busy || draft) return; busy = true; message(''); update();
        try {
            const pool = pools.find(item => item.poolId === result.poolId && item.available);
            if (!pool || !wallet.addresses().includes(pool.providerAddress)) throw new Error('Esta carteira não mantém reservas disponíveis da pool.');
            await review(result, { ...wantedFromPool(pool), operation: 'swap', amountAtomic: result.amountAtomic, minimumReceiveAtomic: result.minimumReceiveAtomic,
                inputSide: result.inputSide, feeAtomic: result.feeAtomic, changeAddress: result.changeAddress }, true);
        } catch (error) { reset(); message(error.message); } finally { busy = false; update(); }
    }
    function receipt(id) {
        if (!/^[0-9a-f]{64}$/.test(id)) throw new Error('Comprovante inválido.');
        const url = new URL(app.dataset.receiptUrl.replace('__id__', id), location.origin);
        if (url.origin !== location.origin) throw new Error('Comprovante inválido.');
        return url.href;
    }
    async function requestStatus() {
        if (!requestId) return;
        const result = await post(app.dataset.requestStatusUrl, { draftId: requestId });
        if (result.status === 'submitted') {
            const link = document.createElement('a'); link.href = receipt(result.transactionId); link.textContent = 'Consultar comprovante da troca';
            byId('request-wait').replaceChildren('Troca enviada à rede. Aguarde a confirmação em bloco. ', link); requestId = null;
            try { sessionStorage.removeItem('povix-dex-swap-request'); } catch (_) { }
        } else if (result.status === 'expired' || result.status === 'stale') {
            byId('request-wait').textContent = 'A solicitação expirou ou as reservas mudaram. Nenhuma troca foi enviada por esta solicitação. Atualize e prepare outra troca.';
            requestId = null; try { sessionStorage.removeItem('povix-dex-swap-request'); } catch (_) { }
        } else byId('request-wait').textContent = result.status === 'prepared' ? 'A solicitação ainda não foi recebida pelo provedor. Repita o envio da revisão assinada.' :
            'Solicitação ' + requestId + ' · Aguardando assinatura do provedor. O saldo ainda não foi debitado; mantenha as entradas disponíveis. A solicitação expira em até 15 minutos.';
        byId('request-wait').hidden = false;
    }
    byId('sign-exchange').addEventListener('click', async () => {
        if (busy || !draft || !backup || !byId('backup-confirmed').checked) return;
        busy = true; message(''); update();
        try {
            await wallet.verifyExchangeDraft(draft, expected, app.dataset.networkId, provider);
            const ownPool = draft.operation === 'swap' && !provider && wallet.addresses().includes(draft.providerAddress);
            if (!signatures) signatures = await wallet.sign({ signingPayload: draft.signingPayload,
                inputAddresses: draft.inputs.slice(provider ? draft.userInputCount : 0, provider || ownPool ? undefined : draft.userInputCount).map(item => item.address) });
            if (draft.operation === 'swap' && !provider && !ownPool) {
                requestId = draft.draftId; try { sessionStorage.setItem('povix-dex-swap-request', requestId); } catch (_) { }
                await post(app.dataset.proposeUrl, { draftId: draft.draftId, signatures }); reset(); await requestStatus();
            } else {
                const result = await post(provider ? app.dataset.acceptUrl : ownPool ? app.dataset.submitSwapUrl : app.dataset.submitPoolUrl,
                    { draftId: draft.draftId, signatures });
                wallet.lock(); location.assign(receipt(result.transactionId));
            }
        } catch (error) { if (error.rejected) reset(); message(error.message + (error.rejected ? '' : ' Repita o mesmo envio para recuperar o resultado.')); }
        finally { busy = false; update(); }
    });
    byId('download-backup').addEventListener('click', () => { try { wallet.backup(); backup = true; update(); } catch (error) { message(error.message); } });
    byId('backup-confirmed').addEventListener('change', update); byId('edit-exchange').addEventListener('click', reset);
    async function refresh() {
        if (refreshing || busy) return; refreshing = true; const currentEpoch = epoch;
        try {
            const response = await fetch(app.dataset.networkUrl, { cache: 'no-store', credentials: 'same-origin' });
            if (!response.ok) throw new Error('Consulta da rede indisponível.');
            network = await response.json();
            byId('network-state').textContent = network.CanCreate ? 'Rede conectada' : 'Aguardando sincronização';
            byId('network-peers').textContent = String(network.PeerCount); byId('network-height').textContent = String(network.Height);
            const data = await post(app.dataset.catalogueUrl, { publicKeys: wallet.isUnlocked() ? wallet.publicKeys() : [] });
            if (currentEpoch !== epoch || busy) return;
            assets = data.assets; pools = data.pools;
            if (!draft) {
                options('pool-asset-a', assets, item => item.symbol + ' · ' + item.name + ' · ' + (item.id === 'povix' ? 'POVIX' : item.id.slice(0, 12)), 'Selecione o primeiro ativo');
                options('pool-asset-b', assets, item => item.symbol + ' · ' + item.name + ' · ' + (item.id === 'povix' ? 'POVIX' : item.id.slice(0, 12)), 'Selecione o segundo ativo');
                options('swap-pool', pools, item => item.assetA.symbol + '/' + item.assetB.symbol + ' · ' + item.poolId.slice(0, 12) + ' · ' + (poolStates[item.status] || 'Indisponível'), 'Selecione uma pool');
            }
            byId('pool-list').replaceChildren(); byId('pools-empty').hidden = pools.length > 0;
            pools.forEach(pool => {
                const entry = document.createElement('a'); entry.href = receipt(pool.poolId);
                entry.textContent = pool.assetA.symbol + '/' + pool.assetB.symbol + ' · ' + (poolStates[pool.status] || 'Indisponível');
                const detail = document.createElement('small'); detail.textContent = 'Reservas registradas: ' + atomicText(pool.reserveAAtomic, pool.assetA.decimals) + ' ' + pool.assetA.symbol + ' / ' + atomicText(pool.reserveBAtomic, pool.assetB.decimals) + ' ' + pool.assetB.symbol + ' · Pool ' + pool.poolId;
                const state = document.createElement('small'); state.textContent = pool.statusMessage || '';
                entry.append(detail, state); byId('pool-list').appendChild(entry);
            });
            if (wallet.isUnlocked()) {
                const native = asset('povix'); byId('wallet-balance').textContent = native ? atomicText(native.balanceAtomic, 8) + ' POVIX livres' : 'Indisponível';
                const requests = await post(app.dataset.requestsUrl, { publicKeys: wallet.publicKeys() });
                if (currentEpoch !== epoch || busy) return;
                byId('provider-requests').replaceChildren(); byId('requests-empty').hidden = requests.length > 0;
                byId('requests-empty').textContent = 'Nenhuma solicitação disponível para esta carteira.';
                requests.forEach(item => { const button = document.createElement('button'); button.type = 'button'; button.className = 'button button-secondary';
                    button.textContent = 'Revisar troca ' + item.assetA.symbol + '/' + item.assetB.symbol + ' · ' + item.draftId.slice(0, 12);
                    button.disabled = Boolean(draft); button.addEventListener('click', () => approveRequest(item)); byId('provider-requests').appendChild(button); });
            }
            await requestStatus();
        } catch (error) { network = null; byId('network-state').textContent = 'Consulta indisponível'; message(error.message); }
        finally { refreshing = false; update(); }
    }
    function lock() {
        wallet.lock(); epoch++; choices = null; reset();
        byId('wallet-form').hidden = false; byId('wallet-connected').hidden = true; byId('wallet-state').textContent = 'Bloqueada';
        byId('wallet-selection').hidden = true; byId('wallet-password-field').hidden = false; byId('wallet-password').required = true;
        byId('wallet-choice').required = false; byId('wallet-connect').textContent = 'Ler carteiras do arquivo';
        byId('provider-requests').replaceChildren(); byId('requests-empty').textContent = 'Abra sua carteira para consultar.';
        byId('requests-empty').hidden = false; assets = []; update();
    }
    byId('wallet-form').addEventListener('submit', async event => {
        event.preventDefault(); if (busy) return; busy = true; message(''); update();
        const password = byId('wallet-password').value; byId('wallet-password').value = '';
        try {
            if (!choices) {
                choices = byId('wallet-file').files[0] ? await wallet.readFile(byId('wallet-file').files[0], password) : await wallet.readSaved(password);
                byId('wallet-choice').replaceChildren(new Option('Selecione uma carteira', ''));
                choices.forEach(item => { const option = new Option(item.name + ' (' + item.addressCount + ' endereços)', item.id); option.disabled = item.addressCount === 0; byId('wallet-choice').add(option); });
                byId('wallet-choice').required = true; byId('wallet-selection').hidden = false; byId('wallet-password-field').hidden = true;
                byId('wallet-password').required = false; byId('wallet-connect').textContent = 'Abrir carteira selecionada';
            } else {
                const info = await wallet.selectWallet(byId('wallet-choice').value); epoch++; reset();
                byId('wallet-name').textContent = info.name; byId('wallet-state').textContent = 'Assinatura local';
                byId('wallet-form').hidden = true; byId('wallet-connected').hidden = false; byId('wallet-file').value = '';
            }
        } catch (error) { message(error.message); } finally { busy = false; update(); if (wallet.isUnlocked()) refresh(); }
    });
    byId('wallet-lock').addEventListener('click', lock); byId('wallet-file').addEventListener('change', lock); byId('wallet-choice').addEventListener('change', update);
    for (const id of ['pool-form', 'swap-form', 'exchange-priority']) { byId(id).addEventListener('input', update); byId(id).addEventListener('change', update); }
    for (const id of ['pool-form', 'swap-form']) byId(id).addEventListener('invalid', () => message('Preencha os ativos, as quantidades e o mínimo a receber com valores válidos.'), true);
    try { const saved = sessionStorage.getItem('povix-dex-swap-request'); if (/^[0-9a-f]{32}$/.test(saved)) requestId = saved; } catch (_) { }
    if (wallet.hasSavedWallet()) byId('wallet-file-help').textContent = 'Há um backup cifrado neste navegador. Informe a senha ou escolha outro arquivo.';
    update(); refresh(); setInterval(refresh, 10000);
})();
