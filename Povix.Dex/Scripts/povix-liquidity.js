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
    const nativeCollecting = pool => pool?.nativeInitialLaunch === true && pool.status === 'collecting';
    const selectedPool = () => pools.find(item => item.poolId === byId('swap-pool').value);
    const integrated = app.dataset.integrated === 'true';
    const managing = app.dataset.mode === 'manage';
    function visiblePool(pool) {
        if (pool.nativeInitialLaunch) return true;
        if (managing) return wallet.isUnlocked() && wallet.addresses().includes(pool.providerAddress);
        return app.dataset.mode !== 'launch' || Boolean(pool.launch);
    }

    let swapDirection = integrated ? '1' : null;
    const swapSide = () => integrated ? swapDirection : byId('swap-side').value;
    const preferredPool = /^[0-9a-f]{64}$/.test(app.dataset.poolId || '') ? app.dataset.poolId : null;
    const poolStates = { collecting: 'Em captação', launch_expired: 'Prazo encerrado: devolução disponível', available: 'Disponível', busy: 'Reservas em uso por operação pendente', unavailable: 'Reservas indisponíveis',
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
            } catch (_) { initial.textContent = 'Preço: selecione dois ativos distintos e informe as reservas.'; }
        }
        const current = byId('swap-price');
        if (current) {
            try { current.textContent = priceText(pool.assetA, pool.assetB, pool.reserveAAtomic, pool.reserveBAtomic); }
            catch (_) { current.textContent = 'Preço: indisponível. Selecione uma pool com reservas positivas.'; }
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
        if (managing) { byId('pool-management-review').hidden = true; byId('pool-management-fee').hidden = true; }
        draft = null; expected = null; signatures = null; byId('sign-actions').hidden = true;
        byId('exchange-review').replaceChildren(); byId('review-title').textContent = 'Revise sua operação'; update();
    }
    function options(id, entries, label, empty) {
        const element = byId(id), previous = element.value || (id === 'swap-pool' ? preferredPool : id === 'launch-token' ? app.dataset.launchTokenId : null);
        element.replaceChildren(new Option(empty, ''));
        if (integrated && id === 'swap-pool') entries = entries.filter(item => item.poolId === preferredPool);
        entries.forEach(item => { const option = new Option(label(item), item.id || item.poolId); option.disabled = item.available === false && !nativeCollecting(item); element.add(option); });
        if (entries.some(item => (item.id || item.poolId) === previous)) element.value = previous;
    }
    function selectedBurnPolicy(a, b) {
        if (!byId('pool-burn-enabled')?.checked) return null;
        if (byId('pool-burn-mode').value === 'percent') {
            const rate = Number(wallet.parseSupply(byId('pool-burn-percent').value, 2));
            if (rate > 10000) throw new Error('A porcentagem máxima de queima é 100%.');
            return { basisPoints: rate, fixedAAtomic: '0', fixedBAtomic: '0' };
        }
        const fixed = (id, item) => item.id === 'povix' || byId(id).value === '0' ? '0' : wallet.parseSupply(byId(id).value, item.decimals);
        const fixedAAtomic = fixed('pool-burn-fixed-a', a), fixedBAtomic = fixed('pool-burn-fixed-b', b);
        if (fixedAAtomic === '0' && fixedBAtomic === '0') throw new Error('Defina uma queima fixa positiva para ao menos um token.');
        return { basisPoints: 0, fixedAAtomic, fixedBAtomic };
    }
    function update() {
        updateUserLiquidity();
        if (byId('pool-burn-fields')) {
            const enabled = Boolean(byId('pool-burn-enabled')?.checked), percent = byId('pool-burn-mode').value === 'percent';
            byId('pool-burn-fields').hidden = !enabled; byId('pool-burn-fields').disabled = !enabled;
            byId('pool-burn-percent-field').hidden = !percent; byId('pool-burn-percent').disabled = !percent; byId('pool-burn-percent').required = enabled && percent;
            byId('pool-burn-fixed-fields').hidden = percent;
            byId('pool-burn-fixed-a').disabled = percent || asset(byId('pool-asset-a').value)?.id === 'povix';
            byId('pool-burn-fixed-b').disabled = percent || asset(byId('pool-asset-b').value)?.id === 'povix';
        }
        if (app.dataset.prepareLaunchPoolUrl) updateLaunch();
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
        if (byId('create-pool')) byId('create-pool').disabled = !unlocked;
        window.PovixWalletMenu?.setBusy(busy || Boolean(draft));
        byId('pool-fields').disabled = !editing || !unlocked;
        byId('swap-fields').disabled = !editing || !unlocked;
        if (integrated) byId('swap-pool').disabled = true;
        byId('prepare-pool').disabled = !editing || !ready;
        byId('prepare-swap').disabled = !editing || !ready || !(selectedPool()?.available || nativeCollecting(selectedPool()));
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
        const collecting = nativeCollecting(pool);
        if (collecting && !draft) { swapDirection = '1'; byId('swap-side').value = '1'; byId('swap-amount').value = '1'; byId('swap-minimum').value = atomicText(String(600000000n - BigInt(fee())), 8); }
        if (byId('market-fee-help')) byId('market-fee-help').textContent = collecting ? 'A taxa da rede é descontada dos 6 POVIX brutos. O valor líquido fica bloqueado até a meta. Não exige saldo POVIX anterior.' : 'Paga em POVIX. A taxa da pool de 0,30% permanece nas reservas.';
        byId('swap-amount').readOnly = collecting;
        byId('swap-minimum').readOnly = collecting;
        byId('swap-side').disabled = collecting || !editing || !unlocked;
        if (integrated) {
            byId('swap-side').value = swapDirection;
            for (const [id, side] of [['swap-buy', '1'], ['swap-sell', '0']]) {
                const button = byId(id);
                if (!button) continue;
                button.disabled = !editing || !ready || !(pool?.available || collecting) || collecting && side === '0';
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
            if (nativeCollecting(pool)) { byId('swap-estimate').textContent = '1 USDC local → 6 POVIX bloqueados até a meta. Uma participação por identidade. A taxa da rede é descontada dos 6 POVIX; não exige saldo POVIX anterior.'; return; }
            const side = Number(swapSide()), input = side === 0 ? pool.assetA : pool.assetB, output = side === 0 ? pool.assetB : pool.assetA;
            const amount = wallet.parseSupply(byId('swap-amount').value, input.decimals);
            const quote = wallet.quotePoolSwap(side === 0 ? pool.reserveAAtomic : pool.reserveBAtomic, side === 0 ? pool.reserveBAtomic : pool.reserveAAtomic, amount, pool.assetA, pool.assetB, side, pool.burnPolicy);
            const receive = quote.receiveAtomic;
            byId('swap-estimate').textContent = 'Cotação estimada: ' + atomicText(receive, output.decimals) + ' ' + output.symbol + ' · Queima na entrada: ' + atomicText(quote.inputBurnAtomic, input.decimals) + ' ' + input.symbol + ' · Queima na saída: ' + atomicText(quote.outputBurnAtomic, output.decimals) + ' ' + output.symbol + ' · Taxa da pool: 0,30%';
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
        addReview('Queima própria da pool', result.burnEnabled ? result.burnPolicy.basisPoints > 0 ? atomicText(result.burnPolicy.basisPoints, 2) + '% em tokens; POVIX isento' : 'Fixa: ' + atomicText(result.burnPolicy.fixedAAtomic, result.assetA.decimals) + ' ' + result.assetA.symbol + ' / ' + atomicText(result.burnPolicy.fixedBAtomic, result.assetB.decimals) + ' ' + result.assetB.symbol : 'Desabilitada');
        if (result.operation === 'lock' || result.operation === 'close') {
            byId('review-title').textContent = result.operation === 'lock' ? 'Bloquear reservas da pool' : 'Encerrar pool e liberar reservas';
            byId('review-description').textContent = result.operation === 'lock' ?
                'Após a confirmação, as reservas só poderão ser usadas em trocas ou no encerramento assinado da pool.' :
                'Após a confirmação, a pool ficará encerrada e os dois valores abaixo voltarão ao saldo livre da carteira provedora.';
        }
        if (result.operation === 'swap') {
            const input = result.inputSide === 0 ? result.assetA : result.assetB, output = result.inputSide === 0 ? result.assetB : result.assetA;
            addReview('Quantidade entregue pelo comprador', atomicText(result.amountAtomic, input.decimals) + ' ' + input.symbol);
            addReview('Queima na entrada', atomicText(result.inputBurnAtomic || '0', input.decimals) + ' ' + input.symbol);
            addReview('Queima na saída', atomicText(result.outputBurnAtomic || '0', output.decimals) + ' ' + output.symbol);
            addReview('Quantidade líquida recebida pelo comprador', atomicText(result.receiveAtomic, output.decimals) + ' ' + output.symbol);
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
        if (result.operation === 'pool') addReview('Liquidez de usuários', (result.launch ? result.launch.allowUserLiquidity : result.allowUserLiquidity) ? 'Habilitada: adicionar e remover a própria participação' : 'Desabilitada');
        if (['add', 'remove'].includes(result.operation)) {
            byId('review-title').textContent = result.operation === 'add' ? 'Adicionar liquidez' : 'Remover minha liquidez';
            byId('review-description').textContent = 'Confira os dois ativos. A participação será atualizada após a confirmação em bloco.';
        }
        if (result.launch) {
            byId('review-title').textContent = result.transactionKind === 13 ? 'Criar pool de lançamento' : result.transactionKind === 16 ? 'Devolver aportes do lançamento' : result.transactionKind === 15 ? 'Completar meta e abrir negociação' : 'Aportar no lançamento';
            byId('review-description').textContent = 'As reservas e cada pagamento abaixo são conferidos pela carteira. Tudo depende da confirmação em bloco.';
            addReview('Meta da captação', atomicText(result.launch.targetAtomic, result.assetB.decimals) + ' ' + result.assetB.symbol);
            addReview('Total captado após a operação', atomicText(result.launch.raisedAtomic, result.assetB.decimals) + ' ' + result.assetB.symbol);
            addReview('Tokens para venda', atomicText(result.launch.saleTokensAtomic, result.assetA.decimals) + ' ' + result.assetA.symbol);
            addReview('Bloco limite', String(result.launch.deadlineHeight));
            addReview('Abertura', 'Captação integral na liquidez da carteira criadora; compradores recebem tokens proporcionais.');
        }
        if (result.initialLaunchPurchase) addReview('Lançamento inicial', '1 USDC local por 6 POVIX brutos menos a taxa da rede. O líquido fica bloqueado até a meta de 179.700 POVIX na reserva. Uma participação por identidade.');
        addReview('Taxa da rede paga pelo solicitante', atomicText(result.feeAtomic, 8) + ' POVIX');
        result.outputs.forEach((output, index) => {
            const token = output.assetId === 'povix' ? { decimals: 8, symbol: 'POVIX' } : output.assetId === result.assetA.id ? result.assetA : result.assetB;
            addReview(result.transactionKind === 16 ? 'Devolução de lançamento' : result.transactionKind === 15 && index >= 2 && index < 2 + result.launch.contributors.length ? 'Tokens comprados no lançamento' : index < 2 ? (result.operation === 'close' ? 'Devolução ' : 'Reserva bloqueada ') + token.symbol : result.operation === 'remove' && index < 4 ? 'Liquidez recebida' : result.operation === 'swap' && index === 2 ? 'Recebimento do comprador' : 'Troco',
                atomicText(output.amountAtomic, token.decimals) + ' ' + token.symbol);
            addReview('Endereço', output.address);
        });
        byId('sign-exchange').textContent = result.operation === 'lock' ? 'Confirmar bloqueio das reservas' : result.operation === 'close' ? 'Confirmar encerramento da pool' :
            result.operation === 'add' ? 'Confirmar e adicionar liquidez' : result.operation === 'remove' ? 'Confirmar e remover liquidez' :
            result.operation === 'pool' ? 'Confirmar e criar pool' : 'Confirmar troca';
        if (result.initialLaunchPurchase) { byId('review-title').textContent = 'Compra do lançamento'; byId('sign-exchange').textContent = 'Confirmar compra com taxa descontada'; }
        if (result.launch) byId('sign-exchange').textContent = result.transactionKind === 16 ? 'Assinar devolução dos aportes' : result.transactionKind === 15 ? 'Assinar aporte e abrir negociação' : result.transactionKind === 13 ? 'Assinar e criar lançamento' : 'Assinar aporte no lançamento';
        byId('sign-actions').hidden = false; update(); byId('review-title').scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
    function ensureFunds(requirements) {
        for (const [id, amount] of requirements) {
            const token = asset(id);
            if (!token || BigInt(token.balanceAtomic) < amount) throw new Error('Saldo livre insuficiente de ' + (token ? token.symbol : id) + '. Reservas da pool e entradas pendentes não estão disponíveis.');
        }
    }
    const wantedFromPool = pool => ({ poolId: pool.poolId, reserveTransactionId: pool.reserveTransactionId, providerAddress: pool.providerAddress,
        burnEnabled: Boolean(pool.burnEnabled), burnPolicy: pool.burnPolicy, participation: pool.participation, assetA: pool.assetA, assetB: pool.assetB, reserveAAtomic: pool.reserveAAtomic, reserveBAtomic: pool.reserveBAtomic });
    async function manageReserves(pool) {
        if (busy || draft || !wallet.isUnlocked() || !network?.CanCreate || !pool.canManage) return;
        if (managing) { byId('pool-management-review').hidden = false; byId('pool-management-fee').hidden = false; }
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
            const burnPolicy = selectedBurnPolicy(a, b);
            const wanted = { operation: 'pool', burnEnabled: Boolean(burnPolicy), burnPolicy, rewardMode, loyaltyPolicy, allowUserLiquidity, assetA: a, assetB: b, reserveAAtomic, reserveBAtomic, feeAtomic, changeAddress };
            const result = await post(app.dataset.preparePoolUrl, { AssetA: a.id, AssetB: b.id, AmountA: byId('pool-amount-a').value,
                AmountB: byId('pool-amount-b').value, BurnEnabled: Boolean(burnPolicy), BurnMode: byId('pool-burn-mode')?.value || 'percent',
                BurnPercent: byId('pool-burn-percent')?.value || '', BurnFixedA: byId('pool-burn-fixed-a')?.value || '0', BurnFixedB: byId('pool-burn-fixed-b')?.value || '0', AllowUserLiquidity: allowUserLiquidity, RewardMode: rewardMode, LoyaltyFeePercent: rewardMode === 'loyalty' ? byId('pool-loyalty-fee').value : '',
                LoyaltyMinimumReserve: rewardMode === 'loyalty' ? byId('pool-loyalty-minimum').value : '', LoyaltyEarlyExit: loyaltyPolicy?.earlyExit || '', FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress });
            await review(result, wanted);
        } catch (error) { reset(); message(error.message); }
        finally { busy = false; update(); }
    });
    function launchCreateRequest() {
        const a = asset(byId('launch-token').value), b = asset(byId('launch-funding-asset').value);
        if (!a || !b || a.id === b.id || a.id === 'povix') throw new Error('Escolha o token e outro ativo de captação.');
        const saleTokensAtomic = wallet.parseSupply(byId('launch-sale').value, a.decimals), liquidity = wallet.parseSupply(byId('launch-liquidity').value, a.decimals);
        const targetAtomic = wallet.parseSupply(byId('launch-target').value, b.decimals), raisedAtomic = wallet.parseSupply(byId('launch-seed').value, b.decimals);
        const totalTokensAtomic = String(BigInt(saleTokensAtomic) + BigInt(liquidity)), deadlineHeight = Number(byId('launch-deadline').value);
        if (!Number.isSafeInteger(deadlineHeight) || deadlineHeight <= (network?.Height || 0) + 1 || deadlineHeight > (network?.Height || 0) + 1000001 ||
            BigInt(raisedAtomic) >= BigInt(targetAtomic) || BigInt(saleTokensAtomic) * BigInt(raisedAtomic) / BigInt(targetAtomic) < 1n)
            throw new Error('Use um bloco futuro e um aporte inicial menor que a meta, suficiente para comprar ao menos uma unidade atômica do token.');
        const address = wallet.addresses()[0];
        return { a, b, address, launch: { saleTokensAtomic, totalTokensAtomic, targetAtomic, raisedAtomic, deadlineHeight,
            allowUserLiquidity: Boolean(byId('launch-user-liquidity').checked), contributors: [{ address, amountAtomic: raisedAtomic }] } };
    }
    function launchOperationRequest() {
        const pool = pools.find(item => item.poolId === byId('launch-pool').value), refund = byId('launch-operation').value === 'refund';
        if (!pool?.launch || pool.status !== (refund ? 'launch_expired' : 'collecting')) throw new Error(refund ? 'A devolução exige prazo encerrado e meta não atingida.' : 'Selecione um lançamento em captação.');
        const amountAtomic = refund ? '0' : wallet.parseSupply(byId('launch-amount').value, pool.assetB.decimals);
        if (!refund && BigInt(amountAtomic) > BigInt(pool.launch.targetAtomic) - BigInt(pool.launch.raisedAtomic)) throw new Error('O aporte supera o valor restante para a meta.');
        return { pool, refund, amountAtomic };
    }
    function updateLaunch() {
        if (!app.dataset.prepareLaunchPoolUrl) return;
        const ready = !busy && !draft && wallet.isUnlocked() && network?.CanCreate && assets.length > 0;
        byId('launch-create-fields').disabled = !ready; byId('prepare-launch-create').disabled = !ready;
        byId('launch-contribute-fields').disabled = !ready; byId('prepare-launch-operation').disabled = !ready;
        if (network?.Height != null) { byId('launch-deadline').min = String(network.Height + 2); if (!byId('launch-deadline').value) byId('launch-deadline').value = String(network.Height + 1000); }
        const refund = byId('launch-operation').value === 'refund'; byId('launch-amount').disabled = refund; byId('launch-amount').required = !refund;
        const selected = pools.find(item => item.poolId === byId('launch-pool').value);
        byId('launch-progress').textContent = selected?.launch ? 'Captado: ' + atomicText(selected.launch.raisedAtomic, selected.assetB.decimals) + ' / ' + atomicText(selected.launch.targetAtomic, selected.assetB.decimals) + ' ' + selected.assetB.symbol + ' · Tokens para compradores: ' + atomicText(selected.launch.saleTokensAtomic, selected.assetA.decimals) + ' ' + selected.assetA.symbol + ' · Bloco limite ' + selected.launch.deadlineHeight : 'Selecione um lançamento.';
        try {
            const request = launchCreateRequest();
            byId('launch-create-estimate').textContent = 'Depósito: ' + atomicText(request.launch.totalTokensAtomic, request.a.decimals) + ' ' + request.a.symbol + ' + ' + atomicText(request.launch.raisedAtomic, request.b.decimals) + ' ' + request.b.symbol + '. Meta: ' + atomicText(request.launch.targetAtomic, request.b.decimals) + ' ' + request.b.symbol;
        } catch (error) { byId('prepare-launch-create').disabled = true; byId('launch-create-estimate').textContent = error.message; }
        try {
            const request = launchOperationRequest();
            byId('launch-contribution-estimate').textContent = request.refund ? 'A rede devolverá cada aporte ao seu endereço original, junto com os tokens ao criador.' :
                'Seu aporte será reservado até a meta. Tokens proporcionais entregues na abertura; arredondamentos permanecem na liquidez.';
        } catch (error) { byId('prepare-launch-operation').disabled = true; byId('launch-contribution-estimate').textContent = error.message; }
    }
    if (app.dataset.prepareLaunchPoolUrl) {
        for (const id of ['launch-create-form', 'launch-contribute-form']) { byId(id).addEventListener('input', updateLaunch); byId(id).addEventListener('change', updateLaunch); }
        byId('launch-create-form').addEventListener('submit', async event => {
            event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !network?.CanCreate) return;
            busy = true; message(''); update();
            try {
                const request = launchCreateRequest(), feeAtomic = fee();
                const funds = new Map([['povix', BigInt(feeAtomic)]]);
                for (const [id, amount] of [[request.a.id, request.launch.totalTokensAtomic], [request.b.id, request.launch.raisedAtomic]]) funds.set(id, (funds.get(id) || 0n) + BigInt(amount));
                ensureFunds(funds);
                const wanted = { operation: 'pool', rewardMode: 'fees', allowUserLiquidity: false, launch: request.launch, assetA: request.a, assetB: request.b,
                    reserveAAtomic: request.launch.totalTokensAtomic, reserveBAtomic: request.launch.raisedAtomic, feeAtomic, changeAddress: request.address };
                const result = await post(app.dataset.prepareLaunchPoolUrl, { AssetA: request.a.id, AssetB: request.b.id, SaleTokens: byId('launch-sale').value, LiquidityTokens: byId('launch-liquidity').value,
                    InitialContribution: byId('launch-seed').value, Target: byId('launch-target').value, DeadlineHeight: request.launch.deadlineHeight, AllowUserLiquidity: request.launch.allowUserLiquidity,
                    FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress: request.address });
                await review(result, wanted);
            } catch (error) { reset(); message(error.message); } finally { busy = false; update(); }
        });
        byId('launch-contribute-form').addEventListener('submit', async event => {
            event.preventDefault(); if (busy || draft || !wallet.isUnlocked() || !network?.CanCreate) return;
            busy = true; message(''); update();
            try {
                const request = launchOperationRequest(), feeAtomic = fee(), changeAddress = wallet.addresses()[0];
                const funds = new Map([['povix', BigInt(feeAtomic)]]);
                if (!request.refund) funds.set(request.pool.assetB.id, (funds.get(request.pool.assetB.id) || 0n) + BigInt(request.amountAtomic)); ensureFunds(funds);
                const wanted = { ...wantedFromPool(request.pool), launch: request.pool.launch, operation: request.refund ? 'launch-refund' : 'launch-contribute', amountAtomic: request.amountAtomic, feeAtomic, changeAddress };
                const result = await post(app.dataset.prepareLaunchOperationUrl, { PoolId: request.pool.poolId, Operation: request.refund ? 'refund' : 'contribute',
                    Amount: request.refund ? '' : byId('launch-amount').value, FeePriority: byId('exchange-priority').value, FeeAtomic: feeAtomic, publicKeys: wallet.publicKeys(), changeAddress });
                await review(result, wanted);
            } catch (error) { reset(); message(error.message); } finally { busy = false; update(); }
        });
    }
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
            if (!pool.available && !nativeCollecting(pool)) throw new Error(pool.statusMessage || 'A pool aguarda reservas confirmadas e disponíveis. Consulte seu comprovante.');
            const inputSide = Number(selectedSide), input = inputSide === 0 ? pool.assetA : pool.assetB, output = inputSide === 0 ? pool.assetB : pool.assetA;
            const amountAtomic = wallet.parseSupply(byId('swap-amount').value, input.decimals), minimumReceiveAtomic = wallet.parseSupply(byId('swap-minimum').value, output.decimals);
            const quote = nativeCollecting(pool) ? { receiveAtomic: String(600000000n - BigInt(fee())), inputDebitAtomic: '1000000' } : wallet.quotePoolSwap(inputSide === 0 ? pool.reserveAAtomic : pool.reserveBAtomic, inputSide === 0 ? pool.reserveBAtomic : pool.reserveAAtomic, amountAtomic, pool.assetA, pool.assetB, inputSide, pool.burnPolicy);
            const receive = quote.receiveAtomic;
            if (BigInt(receive) < BigInt(minimumReceiveAtomic)) throw new Error('O mínimo a receber supera a cotação atual.');
            const feeAtomic = fee(), changeAddress = wallet.addresses()[0], funds = new Map(nativeCollecting(pool) ? [] : [['povix', BigInt(feeAtomic)]]);
            funds.set(input.id, (funds.get(input.id) || 0n) + BigInt(quote.inputDebitAtomic)); ensureFunds(funds);
            const wanted = { ...wantedFromPool(pool), operation: 'swap', initialLaunchPurchase: nativeCollecting(pool), inputSide, amountAtomic, minimumReceiveAtomic, feeAtomic, changeAddress };
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
                inputAddresses: draft.inputs.slice(0, ['swap', 'add', 'remove', 'launch-contribute', 'launch-refund'].includes(draft.operation) ? draft.userInputCount : undefined).map(item => item.address).concat(draft.initialLaunchPurchase ? [draft.outputs[2].address] : []) });
            const result = await post(['launch-contribute', 'launch-refund'].includes(draft.operation) ? app.dataset.submitLaunchUrl : ['add', 'remove'].includes(draft.operation) ? app.dataset.submitLiquidityUrl : draft.operation === 'lock' || draft.operation === 'close' ? app.dataset.submitReservesUrl :
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
    function filterPoolList() {
        if (!byId('pool-search')) return;
        const query = byId('pool-search').value.trim().toLocaleLowerCase('pt-BR');
        const status = byId('pool-status-filter').value;
        const rows = Array.from(byId('pool-list').children);
        let count = 0;
        rows.forEach(row => {
            row.hidden = !row.dataset.search.includes(query) || status !== 'all' && row.dataset.group !== status;
            if (!row.hidden) count++;
        });
        byId('pool-result-count').textContent = count + ' de ' + rows.length + (rows.length === 1 ? ' pool' : ' pools');
        byId('pool-filter-empty').hidden = rows.length === 0 || count > 0;
    }
    if (byId('pool-search')) {
        byId('pool-search').addEventListener('input', filterPoolList);
        byId('pool-status-filter').addEventListener('change', filterPoolList);
    }
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
            const visiblePools = pools.filter(visiblePool);
            byId('pool-list').replaceChildren(); byId('pools-empty').hidden = visiblePools.length > 0;
            if (managing) byId('pools-empty').textContent = wallet.isUnlocked() ? 'Nenhuma pool criada por esta carteira.' : 'Conecte sua carteira para consultar suas pools.';
            visiblePools.forEach(pool => {
                const node = (tag, className, text) => {
                    const element = document.createElement(tag); element.className = className;
                    if (text != null) element.textContent = text;
                    return element;
                };
                const entry = node('article', 'pool-item');
                entry.dataset.search = [pool.assetA.symbol, pool.assetB.symbol, pool.assetA.name || '', pool.assetB.name || '', pool.poolId].join(' ').toLocaleLowerCase('pt-BR');
                entry.dataset.group = ['available', 'closed'].includes(pool.status) ? pool.status : ['pending', 'validated'].includes(pool.status) ? 'waiting' : 'other';
                const heading = node('div', 'pool-item-heading');
                const icons = node('div', 'pool-item-icons'); icons.setAttribute('aria-hidden', 'true');
                icons.append(node('span', '', pool.assetA.symbol.slice(0, 2)), node('span', '', pool.assetB.symbol.slice(0, 2)));
                const pair = node('div', 'pool-item-pair');
                pair.append(node('h3', '', pool.assetA.symbol + ' / ' + pool.assetB.symbol),
                    node('span', 'pool-item-names', (pool.assetA.name || pool.assetA.symbol) + ' / ' + (pool.assetB.name || pool.assetB.symbol)));
                pair.append(node('span', 'pool-item-id', '#' + pool.poolId.slice(0, 6) + '…' + pool.poolId.slice(-4)));
                const status = node('span', 'pool-item-status', poolStates[pool.status] || 'Indisponível');
                status.dataset.state = pool.status;
                heading.append(icons, pair);
                const reserves = node('dl', 'pool-item-reserves');
                for (const [token, amount] of [[pool.assetA, pool.reserveAAtomic], [pool.assetB, pool.reserveBAtomic]]) {
                    const reserve = node('div', '');
                    reserve.append(node('dt', '', 'Reserva de ' + token.symbol),
                        node('dd', '', atomicText(amount, token.decimals) + ' ' + token.symbol));
                    reserves.append(reserve);
                }
                const price = node('p', 'pool-item-price');
                try { price.textContent = pool.nativeInitialLaunch && pool.status === 'collecting' ? 'Lançamento inicial: 6 POVIX por 1 USDC local' : priceText(pool.assetA, pool.assetB, pool.reserveAAtomic, pool.reserveBAtomic); }
                catch (_) { price.textContent = 'Preço: indisponível.'; }
                const details = node('details', 'pool-item-details');
                if (pool.nativeInitialLaunch) details.append(node('p', '', pool.statusMessage));
                details.append(node('summary', '', 'Detalhes da pool'),
                    node('p', '', 'Recompensa: ' + (pool.rewardMode === 'loyalty' ? 'Bônus por permanência (configuração, sem pagamento ativo)' : 'Participação nas taxas')),
                    node('p', '', 'Liquidez de usuários: ' + (pool.allowUserLiquidity ? 'habilitada' : 'desabilitada')),
                    node('p', 'pool-item-full-id', 'Identificador: ' + pool.poolId));
                const actions = node('div', 'pool-item-actions');
                const marketLink = node('a', 'button button-secondary', 'Abrir mercado ↗');
                marketLink.href = app.dataset.marketUrl.replace('__id__', encodeURIComponent(pool.poolId));
                const receiptLink = node('a', 'text-button', 'Comprovante'); receiptLink.href = receipt(pool.poolId);
                actions.append(marketLink, receiptLink);
                if (pool.canManage && wallet.isUnlocked() && wallet.addresses().includes(pool.providerAddress)) {
                    const button = node('button', 'button pool-item-close', pool.locked ? 'Encerrar pool' : 'Bloquear reservas');
                    button.type = 'button';
                    button.addEventListener('click', () => manageReserves(pool)); actions.append(button);
                }
                const policy = node('div', 'pool-card-policy', pool.allowUserLiquidity ? 'Liquidez aberta a outros usuários' : 'Liquidez exclusiva do criador');
                if (pool.statusMessage) details.append(node('p', 'pool-item-message', pool.statusMessage));
                if (pool.launch) details.append(node('p', 'pool-item-message', 'Captação: ' + atomicText(pool.launch.raisedAtomic, pool.assetB.decimals) + ' / ' + atomicText(pool.launch.targetAtomic, pool.assetB.decimals) + ' ' + pool.assetB.symbol + ' · Bloco limite ' + pool.launch.deadlineHeight));
                if (pool.managementMessage) details.append(node('p', 'pool-item-message', pool.managementMessage));
                const top = node('div', 'pool-card-top'); top.append(heading, status);
                entry.append(top, reserves, price, policy, details, actions); byId('pool-list').appendChild(entry);
            });
            filterPoolList();
            if (app.dataset.prepareLaunchPoolUrl && !draft) {
                options('launch-token', assets.filter(item => item.id !== 'povix'), item => item.symbol + ' · ' + item.id.slice(0, 12), 'Selecione o token confirmado');
                options('launch-funding-asset', assets, item => item.symbol + ' · ' + item.id.slice(0, 12), 'Selecione o ativo de captação');
                options('launch-pool', pools.filter(item => item.launch && ['collecting', 'launch_expired', 'busy'].includes(item.status)).map(item => ({ ...item, available: item.status !== 'busy' })),
                    item => item.assetA.symbol + '/' + item.assetB.symbol + ' · ' + item.poolId.slice(0, 12) + ' · ' + poolStates[item.status], 'Selecione o lançamento');
            }
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
            epoch++; assets = []; pools = [];
            if (managing) { byId('pool-list').replaceChildren(); byId('pools-empty').hidden = false; byId('pools-empty').textContent = 'Consultando pools da carteira…'; }
            filterPoolList();
            reset();
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
    const createPoolButton = byId('create-pool');
    if (createPoolButton) {
        createPoolButton.addEventListener('click', () => {
            if (!wallet.isUnlocked()) return;
            location.assign(createPoolButton.dataset.url);
        });
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
