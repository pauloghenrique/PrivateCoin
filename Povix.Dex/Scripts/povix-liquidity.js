(function () {
    'use strict';
    const app = document.getElementById('liquidity-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet, byId = id => document.getElementById(id);
    const csrf = byId('pool-form').querySelector('[name="__RequestVerificationToken"]').value;
    let assets = [], pools = [], network = null, draft = null, expected = null, signatures = null;
    let choices = null, busy = false, refreshing = false, backup = false, epoch = 0;
    const message = text => { byId('app-message').textContent = text || ''; byId('app-message').hidden = !text; };
    const asset = id => assets.find(item => item.id === id);
    const selectedPool = () => pools.find(item => item.poolId === byId('swap-pool').value);
    const integrated = app.dataset.integrated === 'true';
    const preferredPool = /^[0-9a-f]{64}$/.test(app.dataset.poolId || '') ? app.dataset.poolId : null;
    const poolStates = { available: 'Disponível', busy: 'Reservas em uso por operação pendente', unavailable: 'Reservas indisponíveis',
        pending: 'Aguardando validação', validated: 'Validada, aguardando bloco', rejected: 'Criação não confirmada',
        unlocked: 'Reservas antigas sem bloqueio', closed: 'Encerrada' };
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
        draft = null; expected = null; signatures = null; backup = false;
        byId('backup-confirmed').checked = false; byId('sign-actions').hidden = true;
        byId('exchange-review').replaceChildren(); byId('review-title').textContent = 'Revise sua operação'; update();
    }
    function options(id, entries, label, empty) {
        const element = byId(id), previous = element.value || (id === 'swap-pool' ? preferredPool : null);
        element.replaceChildren(new Option(empty, ''));
        if (integrated && id === 'swap-pool') entries = entries.filter(item => item.poolId === preferredPool);
        entries.forEach(item => { const option = new Option(label(item), item.id || item.poolId); option.disabled = item.available === false; element.add(option); });
        if (entries.some(item => (item.id || item.poolId) === previous)) element.value = previous;
    }
    function update() {
        const unlocked = wallet.isUnlocked(), ready = unlocked && network && network.CanCreate, editing = !busy && !draft;
        byId('pool-fields').disabled = !editing || !unlocked;
        byId('swap-fields').disabled = !editing || !unlocked;
        if (integrated) byId('swap-pool').disabled = true;
        byId('prepare-pool').disabled = !editing || !ready;
        byId('prepare-swap').disabled = !editing || !ready || !selectedPool()?.available;
        byId('exchange-priority').disabled = !editing;
        byId('sign-exchange').disabled = busy || !draft || !ready || !backup || !byId('backup-confirmed').checked;
        byId('edit-exchange').disabled = busy; byId('download-backup').disabled = busy;
        byId('wallet-connect').disabled = busy || Boolean(choices && !byId('wallet-choice').value);
        byId('wallet-file').disabled = busy; byId('wallet-password').disabled = busy || Boolean(choices);
        byId('wallet-choice').disabled = busy || !choices; byId('wallet-lock').disabled = busy;
        byId('pool-list').querySelectorAll('button').forEach(button => { button.disabled = !editing || !ready; });
        try { byId('exchange-fee').textContent = atomicText(fee(), 8) + ' POVIX'; } catch (_) { byId('exchange-fee').textContent = 'Consultando a taxa…'; }
        for (const side of ['a', 'b']) {
            const token = asset(byId('pool-asset-' + side).value);
            byId('pool-balance-' + side).textContent = token ? 'Saldo livre: ' + atomicText(token.balanceAtomic, token.decimals) + ' ' + token.symbol : 'Escolha um ativo';
        }
        const pool = selectedPool();
        if (integrated) {
            for (const [id, side] of [['swap-buy', '1'], ['swap-sell', '0']]) {
                const button = byId(id);
                if (!button) continue;
                button.disabled = !editing || !ready || !pool?.available;
                button.setAttribute('aria-pressed', byId('swap-side').value === side ? 'true' : 'false');
                button.setAttribute('title', pool ? (side === '1' ? pool.assetB.symbol + ' → ' + pool.assetA.symbol : pool.assetA.symbol + ' → ' + pool.assetB.symbol) : 'Aguarde a pool');
            }
        }
        byId('swap-pool-state').textContent = pool ? pool.statusMessage || poolStates[pool.status] || 'Consulte o comprovante da pool.' :
            pools.length ? 'Selecione uma pool. A lista informa a disponibilidade de cada uma.' : 'Crie uma pool e aguarde a confirmação das reservas em bloco.';
        byId('swap-provider').textContent = pool ? 'Provedor: ' + pool.providerAddress : '';
        if (pool) {
            const selector = byId('swap-side'), selectedSide = selector.value;
            for (const option of Array.from(selector.options)) {
                const label = option.value === '0' ? pool.assetA.symbol + ' → ' + pool.assetB.symbol : pool.assetB.symbol + ' → ' + pool.assetA.symbol;
                if (option.textContent !== label) option.textContent = label;
            }
            selector.value = selectedSide;
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
    async function review(result, wanted) {
        await wallet.verifyExchangeDraft(result, wanted, app.dataset.networkId);
        draft = result; expected = wanted; signatures = null; backup = false;
        byId('backup-confirmed').checked = false; byId('exchange-review').replaceChildren();
        byId('review-title').textContent = result.operation === 'pool' ? 'Criação da pool' : 'Executar troca';
        byId('review-description').textContent = result.operation === 'pool' ? 'A pool ficará disponível após a confirmação das reservas em bloco.' :
            'A carteira selecionada assina a troca e a envia à rede, sem aprovação do criador da pool. Aguarde a confirmação em bloco.';
        if (result.operation === 'lock' || result.operation === 'close') {
            byId('review-title').textContent = result.operation === 'lock' ? 'Bloquear reservas da pool' : 'Encerrar pool e liberar reservas';
            byId('review-description').textContent = result.operation === 'lock' ?
                'Após a confirmação, as reservas só poderão ser usadas em trocas ou no encerramento assinado da pool.' :
                'Após a confirmação, a pool ficará encerrada e os dois valores abaixo voltarão ao saldo livre da carteira provedora.';
        }
        if (result.operation === 'swap') {
            const input = result.inputSide === 0 ? result.assetA : result.assetB, output = result.inputSide === 0 ? result.assetB : result.assetA;
            addReview('Quantidade entregue pelo comprador', atomicText(result.amountAtomic, input.decimals) + ' ' + input.symbol);
            addReview('Quantidade recebida pelo comprador', atomicText(result.receiveAtomic, output.decimals) + ' ' + output.symbol);
            addReview('Mínimo a receber', atomicText(result.minimumReceiveAtomic, output.decimals) + ' ' + output.symbol);
        }
        addReview('Taxa da rede paga pelo solicitante', atomicText(result.feeAtomic, 8) + ' POVIX');
        result.outputs.forEach((output, index) => {
            const token = output.assetId === 'povix' ? { decimals: 8, symbol: 'POVIX' } : output.assetId === result.assetA.id ? result.assetA : result.assetB;
            addReview(index < 2 ? (result.operation === 'close' ? 'Devolução ' : 'Reserva bloqueada ') + token.symbol : result.operation === 'swap' && index === 2 ? 'Recebimento do comprador' : 'Troco',
                atomicText(output.amountAtomic, token.decimals) + ' ' + token.symbol);
            addReview('Endereço', output.address);
        });
        byId('sign-exchange').textContent = result.operation === 'lock' ? 'Assinar bloqueio das reservas' : result.operation === 'close' ? 'Assinar encerramento da pool' :
            result.operation === 'pool' ? 'Assinar e criar pool' : 'Assinar e executar troca';
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
    async function manageReserves(pool) {
        if (busy || draft || !wallet.isUnlocked() || !network?.CanCreate || !pool.canManage) return;
        busy = true; message(''); update();
        try {
            const operation = pool.locked ? 'close' : 'lock', feeAtomic = fee();
            if (!wallet.addresses().includes(pool.providerAddress)) throw new Error('Selecione a carteira provedora desta pool.');
            ensureFunds(new Map([['povix', BigInt(feeAtomic)]]));
            const wanted = { ...wantedFromPool(pool), operation, feeAtomic, changeAddress: pool.providerAddress };
            const result = await post(app.dataset.prepareReservesUrl, { poolId: pool.poolId, operation,
                feePriority: byId('exchange-priority').value, feeAtomic, publicKeys: wallet.publicKeys() });
            await review(result, wanted);
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; update(); }
    }
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
            await review(result, wanted);
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; update(); }
    });
    byId('swap-form').addEventListener('submit', async event => {
        event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !network || !network.CanCreate) return;
        const selectedSide = byId('swap-side').value;
        if (selectedSide !== '0' && selectedSide !== '1') { message('Selecione um sentido válido para a troca.'); return; }
        busy = true; message(''); update();
        try {
            const pool = selectedPool(); if (!pool) throw new Error('Selecione uma pool para a troca.');
            if (!pool.available) throw new Error(pool.statusMessage || 'A pool aguarda reservas confirmadas e disponíveis. Consulte seu comprovante.');
            const inputSide = Number(selectedSide), input = inputSide === 0 ? pool.assetA : pool.assetB, output = inputSide === 0 ? pool.assetB : pool.assetA;
            const amountAtomic = wallet.parseSupply(byId('swap-amount').value, input.decimals), minimumReceiveAtomic = wallet.parseSupply(byId('swap-minimum').value, output.decimals);
            const receive = wallet.quoteExchange(inputSide === 0 ? pool.reserveAAtomic : pool.reserveBAtomic, inputSide === 0 ? pool.reserveBAtomic : pool.reserveAAtomic, amountAtomic);
            if (BigInt(receive) < BigInt(minimumReceiveAtomic)) throw new Error('O mínimo a receber supera a cotação atual.');
            const feeAtomic = fee(), changeAddress = wallet.addresses()[0], funds = new Map([['povix', BigInt(feeAtomic)]]);
            funds.set(input.id, (funds.get(input.id) || 0n) + BigInt(amountAtomic)); ensureFunds(funds);
            const wanted = { ...wantedFromPool(pool), operation: 'swap', inputSide, amountAtomic, minimumReceiveAtomic, feeAtomic, changeAddress };
            const result = await post(app.dataset.prepareSwapUrl, { PoolId: pool.poolId, InputSide: inputSide, Amount: byId('swap-amount').value,
                MinimumReceive: byId('swap-minimum').value, FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress });
            await review(result, wanted);
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; update(); }
    });
    function receipt(id) {
        if (!/^[0-9a-f]{64}$/.test(id)) throw new Error('Comprovante inválido.');
        const url = new URL(app.dataset.receiptUrl.replace('__id__', id), location.origin);
        if (url.origin !== location.origin) throw new Error('Comprovante inválido.');
        return url.href;
    }
    let transactionId = null, receiptLoading = false, receiptVersion = null;
    const receiptStorageKey = 'povix-dex-market-receipt:' + preferredPool;
    function renderReceipt(data) {
        const confirmed = data.status === 'confirmed' && data.blockHeight != null;
        const validated = data.status === 'validated' || data.status === 'confirmed' && !confirmed;
        const pending = data.status === 'pending';
        byId('exchange-status-title').textContent = confirmed ? 'Troca confirmada.' : validated ? 'Troca validada. Aguardando bloco.' : pending ? 'Troca enviada à rede.' : 'Troca não confirmada.';
        byId('exchange-status-description').textContent = confirmed ? 'A troca consta em um bloco validado da rede POVIX. Os saldos e as reservas foram atualizados juntos.' :
            validated ? 'A rede validou a troca. Os saldos e as reservas serão atualizados após a confirmação em bloco.' :
            pending ? 'Aguardando validação da rede e confirmação em bloco.' : 'A transação deixou a fila após a atualização da cadeia. Confira seu saldo antes de preparar outra troca.';
        byId('exchange-status-state').textContent = confirmed ? 'Confirmada' : validated ? 'Validada' : pending ? 'Pendente' : 'Não confirmada';
        byId('exchange-status-confirmations').textContent = String(confirmed ? data.confirmations || 0 : 0);
        byId('exchange-status-block').textContent = confirmed ? String(data.blockHeight) : 'Aguardando confirmação';
        byId('exchange-status-hash').textContent = confirmed ? data.blockHash || '—' : '—';
        byId('exchange-status-receive').textContent = data.receive ? atomicText(data.receive.amountAtomic, data.receive.decimals) + ' ' + data.receive.symbol : '—';
        byId('exchange-status-fee').textContent = data.feeAtomic ? atomicText(data.feeAtomic, 8) : '—';
    }
    function showReceipt(id) {
        const url = receipt(id);
        transactionId = id; receiptVersion = null;
        byId('exchange-status').hidden = false;
        byId('exchange-status-transaction').textContent = id;
        byId('exchange-status-receipt').href = url;
        renderReceipt({ status: 'pending' });
        byId('exchange-status-connectivity').textContent = 'Consultando a rede…';
        try { sessionStorage.setItem(receiptStorageKey, id); } catch (_) { }
    }
    async function refreshReceipt() {
        if (!integrated || !transactionId || receiptLoading || document.hidden) return;
        receiptLoading = true; const currentId = transactionId;
        try {
            const url = new URL(app.dataset.statusUrl.replace('__id__', currentId), location.origin);
            if (url.origin !== location.origin) throw new Error('Status inválido.');
            const response = await fetch(url.href, { cache: 'no-store', credentials: 'same-origin' });
            if (!response.ok) throw new Error('Status indisponível.');
            const data = await response.json();
            if (currentId !== transactionId) return;
            if (data.poolId !== preferredPool) throw new Error('Pool inválida.');
            renderReceipt(data);
            byId('exchange-status-connectivity').textContent = (data.peerCount || 0) + ' pares conectados · Atualização a cada 5 segundos';
            const version = data.status + ':' + data.blockHash;
            if (receiptVersion !== version) { receiptVersion = version; document.dispatchEvent(new Event('povix-pool-updated')); }
        } catch (_) {
            if (currentId === transactionId) byId('exchange-status-connectivity').textContent = 'Consulta indisponível. O último status será atualizado ao reconectar.';
        } finally { receiptLoading = false; }
    }
    byId('sign-exchange').addEventListener('click', async () => {
        if (busy || !draft || !backup || !byId('backup-confirmed').checked) return;
        busy = true; message(''); update();
        try {
            await wallet.verifyExchangeDraft(draft, expected, app.dataset.networkId);
            if (!signatures) signatures = await wallet.sign({ signingPayload: draft.signingPayload,
                inputAddresses: draft.inputs.slice(0, draft.operation === 'swap' ? draft.userInputCount : undefined).map(item => item.address) });
            const result = await post(draft.operation === 'lock' || draft.operation === 'close' ? app.dataset.submitReservesUrl :
                draft.operation === 'swap' ? app.dataset.submitSwapUrl : app.dataset.submitPoolUrl,
                { draftId: draft.draftId, signatures });
            const receiptUrl = receipt(result.transactionId);
            if (integrated) {
                showReceipt(result.transactionId);
                reset(); document.dispatchEvent(new Event('povix-pool-updated'));
                refreshReceipt();
            } else { wallet.lock(); location.assign(receiptUrl); }
        } catch (error) { if (error.rejected) reset(); message(error.message + (error.rejected ? '' : ' Repita o mesmo envio para recuperar o resultado.')); }
        finally { busy = false; update(); if (integrated) refresh(); }
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
                if (pool.managementMessage) {
                    const management = document.createElement('small'); management.textContent = pool.managementMessage;
                    byId('pool-list').appendChild(management);
                }
                const marketLink = document.createElement('a');
                marketLink.href = app.dataset.marketUrl.replace('__id__', encodeURIComponent(pool.poolId));
                marketLink.textContent = 'Ver candles e livro caixa →'; byId('pool-list').appendChild(marketLink);
                if (pool.canManage && wallet.isUnlocked() && wallet.addresses().includes(pool.providerAddress)) {
                    const button = document.createElement('button'); button.type = 'button'; button.className = 'button button-secondary';
                    button.textContent = pool.locked ? 'Revisar encerramento da pool' : 'Bloquear reservas desta pool';
                    button.addEventListener('click', () => manageReserves(pool)); byId('pool-list').appendChild(button);
                }
            });
            if (wallet.isUnlocked()) {
                const native = asset('povix'); byId('wallet-balance').textContent = native ? atomicText(native.balanceAtomic, 8) + ' POVIX livres' : 'Indisponível';
            }
        } catch (error) { network = null; byId('network-state').textContent = 'Consulta indisponível'; message(error.message); }
        finally { refreshing = false; update(); }
    }
    function lock() {
        wallet.lock(); epoch++; choices = null; reset();
        byId('wallet-form').hidden = false; byId('wallet-connected').hidden = true; byId('wallet-state').textContent = 'Bloqueada';
        byId('wallet-selection').hidden = true; byId('wallet-password-field').hidden = false; byId('wallet-password').required = true;
        byId('wallet-choice').required = false; byId('wallet-connect').textContent = 'Ler carteiras do arquivo';
        assets = []; update();
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
    if (integrated && byId('swap-buy') && byId('swap-sell')) {
        byId('swap-side').value = '1';
        for (const [id, side] of [['swap-buy', '1'], ['swap-sell', '0']]) {
            byId(id).addEventListener('click', () => {
                if (byId(id).disabled || busy || draft || byId('swap-side').value === side) return;
                byId('swap-side').value = side;
                byId('swap-amount').value = '';
                byId('swap-minimum').value = '';
                update();
            });
        }
    }
    for (const id of ['pool-form', 'swap-form', 'exchange-priority']) { byId(id).addEventListener('input', update); byId(id).addEventListener('change', update); }
    for (const id of ['pool-form', 'swap-form']) byId(id).addEventListener('invalid', () => message('Preencha os ativos, as quantidades e o mínimo a receber com valores válidos.'), true);
    if (wallet.hasSavedWallet()) byId('wallet-file-help').textContent = 'Há um backup cifrado neste navegador. Informe a senha ou escolha outro arquivo.';
    if (integrated) {
        try { const saved = sessionStorage.getItem(receiptStorageKey); if (/^[0-9a-f]{64}$/.test(saved || '')) showReceipt(saved); } catch (_) { }
        refreshReceipt(); setInterval(refreshReceipt, 5000);
        document.addEventListener('visibilitychange', () => { if (!document.hidden) refreshReceipt(); });
    }
    update(); refresh(); setInterval(refresh, 10000);
})();
