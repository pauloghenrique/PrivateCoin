(function () {
    'use strict';
    const app = document.getElementById('liquidity-app');
    if (!app) return;
    const wallet = window.PovixTokenWallet, byId = id => document.getElementById(id);
    const csrf = byId('pool-form').querySelector('[name="__RequestVerificationToken"]').value;
    let assets = [], pools = [], network = null, draft = null, expected = null, signatures = null;
    let busy = false, refreshing = false, epoch = 0;
    const message = text => { byId('app-message').textContent = text || ''; byId('app-message').hidden = !text; };
    const asset = id => assets.find(item => item.id === id);
    const selectedPool = () => pools.find(item => item.poolId === byId('swap-pool').value);
    const integrated = app.dataset.integrated === 'true';
    let swapDirection = integrated ? '1' : null;
    const swapSide = () => integrated ? swapDirection : byId('swap-side').value;
    const preferredPool = /^[0-9a-f]{64}$/.test(app.dataset.poolId || '') ? app.dataset.poolId : null;
    const poolStates = { available: 'Disponível', busy: 'Reservas em uso por operação pendente', unavailable: 'Reservas indisponíveis',
        pending: 'Aguardando validação', validated: 'Validada, aguardando bloco', rejected: 'Criação não confirmada',
        unlocked: 'Reservas antigas sem bloqueio', closed: 'Encerrada' };
    const atomicText = (value, decimals) => wallet.formatAtomic(String(value), decimals);
    function reservePrice(a, b, reserveA, reserveB) {
        if (!a || !b || a.id === b.id) throw new Error('Ativos inválidos.');
        if (![a.decimals, b.decimals].every(value => Number.isInteger(value) && value >= 0 && value <= 8)) throw new Error('Precisão inválida.');
        const numerator = BigInt(reserveB) * 10n ** BigInt(a.decimals);
        const denominator = BigInt(reserveA) * 10n ** BigInt(b.decimals);
        if (numerator <= 0n || denominator <= 0n) throw new Error('Reservas inválidas.');
        const scaled = numerator * 10n ** 12n / denominator;
        const value = scaled === 0n ? '< 0,000000000001' : atomicText(scaled, 12).replace(/(,[0-9]*?)0+$/, '$1').replace(/,$/, '');
        return '1 ' + a.symbol + ' = ' + value + ' ' + b.symbol;
    }
    function priceText(a, b, reserveA, reserveB) {
        return 'Preço ' + a.symbol + ' / ' + b.symbol + ': ' + reservePrice(a, b, reserveA, reserveB) + ' · ' + reservePrice(b, a, reserveB, reserveA);
    }
    function updatePrices(pool) {
        const initial = byId('pool-price');
        if (initial) {
            try {
                const a = asset(byId('pool-asset-a').value), b = asset(byId('pool-asset-b').value);
                initial.textContent = priceText(a, b, wallet.parseSupply(byId('pool-amount-a').value, a.decimals), wallet.parseSupply(byId('pool-amount-b').value, b.decimals));
            } catch (_) { initial.textContent = 'Preço A / B: selecione dois ativos distintos e informe as reservas.'; }
        }
        const current = byId('swap-price');
        if (current) {
            try { current.textContent = priceText(pool.assetA, pool.assetB, pool.reserveAAtomic, pool.reserveBAtomic); }
            catch (_) { current.textContent = 'Preço A / B: indisponível. Selecione uma pool com reservas positivas.'; }
        }
    }
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
        draft = null; expected = null; signatures = null; byId('sign-actions').hidden = true;
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
        updateUserLiquidity();
        if (byId('pool-reward-mode')) {
            const loyalty = byId('pool-reward-mode').value === 'loyalty';
            byId('pool-reward-title').textContent = loyalty ? 'Bônus por permanência' : 'Participação nas taxas';
            byId('pool-reward-summary').textContent = loyalty ? 'Incentiva liquidez estável.' : 'Destinar aos provedores uma parcela das taxas geradas pelas negociações.';
            byId('pool-reward-detail').textContent = loyalty ? 'O criador define a parcela das taxas, a reserva mínima e a regra de saída. O provedor escolherá dias, meses ou anos. Este cadastro ainda não gera pagamentos de bônus.' :
                'Recompensa acompanha o uso do pool. Retorno varia conforme o volume.';
            if (byId('pool-loyalty-policy')) {
                byId('pool-loyalty-policy').hidden = !loyalty;
                byId('pool-loyalty-policy').disabled = !loyalty || busy || Boolean(draft) || !wallet.isUnlocked();
                for (const id of ['pool-loyalty-fee', 'pool-loyalty-minimum']) byId(id).required = loyalty;
            }
        }
        const unlocked = wallet.isUnlocked(), ready = unlocked && network && network.CanCreate && assets.length > 0, editing = !busy && !draft;
        window.PovixWalletMenu?.setBusy(busy || Boolean(draft));
        byId('pool-fields').disabled = !editing || !unlocked;
        byId('swap-fields').disabled = !editing || !unlocked;
        if (integrated) byId('swap-pool').disabled = true;
        byId('prepare-pool').disabled = !editing || !ready;
        byId('prepare-swap').disabled = !editing || !ready || !selectedPool()?.available;
        byId('exchange-priority').disabled = !editing;
        byId('sign-exchange').disabled = busy || !draft || !ready;
        byId('edit-exchange').disabled = busy;
        byId('pool-list').querySelectorAll('button').forEach(button => { button.disabled = !editing || !ready; });
        try { byId('exchange-fee').textContent = atomicText(fee(), 8) + ' POVIX'; } catch (_) { byId('exchange-fee').textContent = 'Consultando a taxa…'; }
        for (const side of ['a', 'b']) {
            const token = asset(byId('pool-asset-' + side).value);
            byId('pool-balance-' + side).textContent = token ? 'Saldo livre: ' + atomicText(token.balanceAtomic, token.decimals) + ' ' + token.symbol : 'Escolha um ativo';
        }
        const pool = selectedPool();
        updatePrices(pool);
        if (integrated) {
            byId('swap-side').value = swapDirection;
            for (const [id, side] of [['swap-buy', '1'], ['swap-sell', '0']]) {
                const button = byId(id);
                if (!button) continue;
                button.disabled = !editing || !ready || !pool?.available;
                button.setAttribute('aria-pressed', swapSide() === side ? 'true' : 'false');
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
            const side = Number(swapSide()), input = side === 0 ? pool.assetA : pool.assetB, output = side === 0 ? pool.assetB : pool.assetA;
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
        draft = result; expected = wanted; signatures = null; byId('exchange-review').replaceChildren();
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
        if (result.operation === 'pool') {
            addReview('Opção de recompensa', result.rewardMode === 'loyalty' ? 'Bônus por permanência · configuração, sem pagamento de bônus ativo' : 'Participação nas taxas · retorno varia conforme o volume');
        }
        if (result.operation === 'pool' && result.loyaltyPolicy) {
            addReview('Parcela das taxas para bônus', atomicText(result.loyaltyPolicy.feeBasisPoints, 2) + '%');
            addReview('Reserva mínima planejada (não debitada)', atomicText(result.loyaltyPolicy.minimumReserveAtomic, 8) + ' POVIX');
            addReview('Saída antecipada planejada', result.loyaltyPolicy.earlyExit === 'locked' ? 'Bloquear retirada até o fim do prazo' : 'Permitir retirar o aporte com perda do bônus');
            addReview('Prazo', 'O provedor escolherá dias, meses ou anos; bloqueio e pagamentos ainda não estão ativos.');
        }
        if (result.operation === 'pool') addReview('Liquidez de usuários', result.allowUserLiquidity ? 'Habilitada: adicionar e remover a própria participação' : 'Desabilitada');
        if (['add', 'remove'].includes(result.operation)) {
            byId('review-title').textContent = result.operation === 'add' ? 'Adicionar liquidez' : 'Remover minha liquidez';
            byId('review-description').textContent = 'Confira os dois ativos. A participação será atualizada após a confirmação em bloco.';
        }
        addReview('Taxa da rede paga pelo solicitante', atomicText(result.feeAtomic, 8) + ' POVIX');
        result.outputs.forEach((output, index) => {
            const token = output.assetId === 'povix' ? { decimals: 8, symbol: 'POVIX' } : output.assetId === result.assetA.id ? result.assetA : result.assetB;
            addReview(index < 2 ? (result.operation === 'close' ? 'Devolução ' : 'Reserva bloqueada ') + token.symbol : result.operation === 'remove' && index < 4 ? 'Liquidez recebida' : result.operation === 'swap' && index === 2 ? 'Recebimento do comprador' : 'Troco',
                atomicText(output.amountAtomic, token.decimals) + ' ' + token.symbol);
            addReview('Endereço', output.address);
        });
        byId('sign-exchange').textContent = result.operation === 'lock' ? 'Confirmar bloqueio das reservas' : result.operation === 'close' ? 'Confirmar encerramento da pool' :
            result.operation === 'add' ? 'Confirmar e adicionar liquidez' : result.operation === 'remove' ? 'Confirmar e remover liquidez' :
            result.operation === 'pool' ? 'Confirmar e criar pool' : 'Confirmar troca';
        byId('sign-actions').hidden = false; update(); byId('review-title').scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
    function ensureFunds(requirements) {
        for (const [id, amount] of requirements) {
            const token = asset(id);
            if (!token || BigInt(token.balanceAtomic) < amount) throw new Error('Saldo livre insuficiente de ' + (token ? token.symbol : id) + '. Reservas da pool e entradas pendentes não estão disponíveis.');
        }
    }
    const wantedFromPool = pool => ({ poolId: pool.poolId, reserveTransactionId: pool.reserveTransactionId, providerAddress: pool.providerAddress,
        participation: pool.participation, assetA: pool.assetA, assetB: pool.assetB, reserveAAtomic: pool.reserveAAtomic, reserveBAtomic: pool.reserveBAtomic });
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
            const allowUserLiquidity = Boolean(byId('pool-user-liquidity')?.checked);
            const rewardMode = byId('pool-reward-mode')?.value || 'fees';
            if (!['fees', 'loyalty'].includes(rewardMode)) throw new Error('Escolha uma opção de recompensa válida.');
            let loyaltyPolicy = null;
            if (rewardMode === 'loyalty') {
                const feeBasisPoints = Number(wallet.parseSupply(byId('pool-loyalty-fee').value, 2));
                const minimumReserveAtomic = wallet.parseSupply(byId('pool-loyalty-minimum').value, 8), earlyExit = byId('pool-loyalty-exit').value;
                if (feeBasisPoints > 10000 || !['forfeit', 'locked'].includes(earlyExit)) throw new Error('Defina uma parcela de até 100% das taxas e uma regra de saída válida.');
                loyaltyPolicy = { feeBasisPoints, minimumReserveAtomic, earlyExit };
            }
            const wanted = { operation: 'pool', rewardMode, loyaltyPolicy, allowUserLiquidity, assetA: a, assetB: b, reserveAAtomic, reserveBAtomic, feeAtomic, changeAddress };
            const result = await post(app.dataset.preparePoolUrl, { AssetA: a.id, AssetB: b.id, AmountA: byId('pool-amount-a').value,
                AmountB: byId('pool-amount-b').value, AllowUserLiquidity: allowUserLiquidity, RewardMode: rewardMode, LoyaltyFeePercent: rewardMode === 'loyalty' ? byId('pool-loyalty-fee').value : '',
                LoyaltyMinimumReserve: rewardMode === 'loyalty' ? byId('pool-loyalty-minimum').value : '', LoyaltyEarlyExit: loyaltyPolicy?.earlyExit || '', FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress });
            await review(result, wanted);
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; update(); }
    });
    function liquidityRequest() {
        const pool = pools.find(item => item.poolId === byId('user-liquidity-pool').value);
        if (!pool || !pool.allowUserLiquidity || !pool.available) throw new Error('Selecione uma pool habilitada com reservas disponíveis.');
        const operation = byId('user-liquidity-operation').value, add = operation === 'add';
        const address = wallet.addresses()[0], total = BigInt(pool.participation.totalSharesAtomic);
        const owned = BigInt(pool.participation.holders.find(holder => holder.address === address)?.sharesAtomic || '0');
        const value = byId('user-liquidity-amount').value;
        const quantity = BigInt(wallet.parseSupply(value, add ? pool.assetA.decimals : 2));
        if (!add && quantity > 10000n) throw new Error('Informe até 100% da sua participação.');
        const shares = add ? quantity * total / BigInt(pool.reserveAAtomic) : owned * quantity / 10000n;
        if (shares <= 0n) throw new Error(add ? 'Quantidade muito pequena.' : 'Esta carteira não tem participação suficiente.');
        if (!add && (shares >= total || address === pool.providerAddress && shares === owned)) throw new Error('O criador deve manter uma participação até o encerramento da pool. Retire uma porcentagem menor ou encerre quando os demais provedores saírem.');
        const amountA = (BigInt(pool.reserveAAtomic) * shares + (add ? total - 1n : 0n)) / total;
        const amountB = (BigInt(pool.reserveBAtomic) * shares + (add ? total - 1n : 0n)) / total;
        if (amountA <= 0n || amountB <= 0n) throw new Error('Quantidade muito pequena para os dois ativos.');
        return { pool, operation, address, shares, amountA, amountB };
    }
    function updateUserLiquidity() {
        if (!byId('user-liquidity-form')) return;
        const ready = !busy && !draft && wallet.isUnlocked() && network?.CanCreate;
        byId('user-liquidity-fields').disabled = !ready;
        byId('prepare-user-liquidity').disabled = !ready;
        byId('user-liquidity-amount-label').textContent = byId('user-liquidity-operation').value === 'add' ? 'Quantidade máxima do primeiro ativo' : 'Porcentagem da minha participação';
        try {
            const request = liquidityRequest(), pool = request.pool;
            byId('user-liquidity-estimate').textContent = (request.operation === 'add' ? 'Você adicionará: ' : 'Você receberá: ') +
                atomicText(request.amountA, pool.assetA.decimals) + ' ' + pool.assetA.symbol + ' + ' + atomicText(request.amountB, pool.assetB.decimals) + ' ' + pool.assetB.symbol;
        } catch (error) { byId('user-liquidity-estimate').textContent = error.message; byId('prepare-user-liquidity').disabled = true; }
    }
    if (byId('user-liquidity-form')) {
        byId('user-liquidity-form').addEventListener('input', updateUserLiquidity);
        byId('user-liquidity-form').addEventListener('change', updateUserLiquidity);
        byId('user-liquidity-form').addEventListener('submit', async event => {
            event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !network?.CanCreate) return;
            busy = true; message(''); update(); updateUserLiquidity();
            try {
                const request = liquidityRequest(), feeAtomic = fee();
                const funds = new Map([['povix', BigInt(feeAtomic)]]);
                if (request.operation === 'add') for (const [id, value] of [[request.pool.assetA.id, request.amountA], [request.pool.assetB.id, request.amountB]]) funds.set(id, (funds.get(id) || 0n) + value);
                ensureFunds(funds);
                const wanted = { ...wantedFromPool(request.pool), operation: request.operation, sharesAtomic: String(request.shares), feeAtomic, changeAddress: request.address };
                const result = await post(app.dataset.prepareLiquidityUrl, { PoolId: request.pool.poolId, Operation: request.operation, Shares: String(request.shares),
                    FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress: request.address });
                await review(result, wanted);
            } catch (error) { reset(); message(error.message); }
            finally { busy = false; update(); updateUserLiquidity(); }
        });
    }
    byId('swap-form').addEventListener('submit', async event => {
        event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !network || !network.CanCreate) return;
        const selectedSide = swapSide();
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
            const result = await post(app.dataset.prepareSwapUrl, { PoolId: pool.poolId, InputSide: inputSide, InputAssetId: input.id, OutputAssetId: output.id, Amount: byId('swap-amount').value,
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
        if (busy || !draft || !wallet.isUnlocked() || !network?.CanCreate) return;
        busy = true; message(''); update();
        try {
            await wallet.verifyExchangeDraft(draft, expected, app.dataset.networkId);
            if (!signatures) signatures = await wallet.sign({ signingPayload: draft.signingPayload,
                inputAddresses: draft.inputs.slice(0, ['swap', 'add', 'remove'].includes(draft.operation) ? draft.userInputCount : undefined).map(item => item.address) });
            const result = await post(['add', 'remove'].includes(draft.operation) ? app.dataset.submitLiquidityUrl : draft.operation === 'lock' || draft.operation === 'close' ? app.dataset.submitReservesUrl :
                draft.operation === 'swap' ? app.dataset.submitSwapUrl : app.dataset.submitPoolUrl,
                { draftId: draft.draftId, signatures });
            const receiptUrl = receipt(result.transactionId);
            if (integrated) {
                showReceipt(result.transactionId);
                reset(); document.dispatchEvent(new Event('povix-pool-updated'));
                refreshReceipt();
            } else { location.assign(receiptUrl); }
        } catch (error) { if (error.rejected) reset(); message(error.message + (error.rejected ? '' : ' Repita o mesmo envio para recuperar o resultado.')); }
        finally { busy = false; update(); if (integrated) refresh(); }
    });
    byId('edit-exchange').addEventListener('click', reset);
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
                const detail = document.createElement('small'); detail.textContent = 'Reservas registradas: ' + atomicText(pool.reserveAAtomic, pool.assetA.decimals) + ' ' + pool.assetA.symbol + ' / ' + atomicText(pool.reserveBAtomic, pool.assetB.decimals) + ' ' + pool.assetB.symbol + ' · Recompensa: ' + (pool.rewardMode === 'loyalty' ? 'Bônus por permanência (configuração, sem pagamento ativo)' : 'Participação nas taxas') + ' · Liquidez de usuários: ' + (pool.allowUserLiquidity ? 'habilitada' : 'desabilitada') + ' · Pool ' + pool.poolId;
                const state = document.createElement('small'); state.textContent = pool.statusMessage || '';
                const price = document.createElement('small');
                try { price.textContent = priceText(pool.assetA, pool.assetB, pool.reserveAAtomic, pool.reserveBAtomic); }
                catch (_) { price.textContent = 'Preço A / B: indisponível.'; }
                entry.append(detail, price, state); byId('pool-list').appendChild(entry);
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
            if (byId('user-liquidity-pool') && !draft) options('user-liquidity-pool', pools.filter(pool => pool.allowUserLiquidity), pool => pool.assetA.symbol + '/' + pool.assetB.symbol + ' · ' + pool.poolId.slice(0, 12), 'Selecione uma pool');
            updateUserLiquidity();
            if (wallet.isUnlocked()) {
                const native = asset('povix'); byId('wallet-balance').textContent = native ? atomicText(native.balanceAtomic, 8) + ' POVIX livres' : 'Indisponível';
            }
        } catch (error) { network = null; byId('network-state').textContent = 'Consulta indisponível'; message(error.message); }
        finally { refreshing = false; update(); if (currentEpoch !== epoch) refresh(); }
    }
    document.addEventListener('povix-wallet-change', async () => {
        try {
            epoch++; assets = []; reset();
            await refresh(); update();
        } catch (error) { message(error.message); }
    });
    if (integrated && byId('swap-buy') && byId('swap-sell')) {
        byId('swap-side').value = '1';
        for (const [id, side] of [['swap-buy', '1'], ['swap-sell', '0']]) {
            byId(id).addEventListener('click', () => {
                if (byId(id).disabled || busy || draft || swapDirection === side) return;
                swapDirection = side;
                byId('swap-side').value = side;
                byId('swap-amount').value = '';
                byId('swap-minimum').value = '';
                update();
            });
        }
    }
    if (integrated) {
        const selectDirection = () => {
            if (busy || draft) { update(); return; }
            const direction = byId('swap-side').value;
            if (direction !== swapDirection) {
                swapDirection = direction;
                byId('swap-amount').value = '';
                byId('swap-minimum').value = '';
            }
            update();
        };
        // The select emits input before change. Capture it before the input
        // bubbles to the form, whose update restores the stored direction.
        byId('swap-side').addEventListener('input', selectDirection);
        byId('swap-side').addEventListener('change', selectDirection);
    }
    for (const id of ['pool-form', 'swap-form', 'exchange-priority']) { byId(id).addEventListener('input', update); byId(id).addEventListener('change', update); }
    for (const id of ['pool-form', 'swap-form']) byId(id).addEventListener('invalid', () => message('Preencha os ativos, as quantidades e o mínimo a receber com valores válidos.'), true);
    if (integrated) {
        try { const saved = sessionStorage.getItem(receiptStorageKey); if (/^[0-9a-f]{64}$/.test(saved || '')) showReceipt(saved); } catch (_) { }
        refreshReceipt(); setInterval(refreshReceipt, 5000);
        document.addEventListener('visibilitychange', () => { if (!document.hidden) refreshReceipt(); });
    }
    update(); refresh(); setInterval(refresh, 10000);
})();
