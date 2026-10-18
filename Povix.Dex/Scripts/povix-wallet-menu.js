(() => {
    'use strict';
    const wallet = window.PovixTokenWallet, el = id => document.getElementById(id);
    const dialog = el('wallet-menu');
    if (!dialog) return;
    let choices = null, pending = false, pageBusy = false;
    const message = text => { el('wallet-message').textContent = text; };
    function update() {
        const connected = wallet.isUnlocked(), blocked = pending || pageBusy;
        el('wallet-form').hidden = connected;
        el('wallet-connected').hidden = !connected;
        el('wallet-state').textContent = connected ? 'Conectada' : 'Desconectada';
        el('wallet-name').textContent = connected ? wallet.info().name : '';
        document.querySelectorAll('[data-wallet-label]').forEach(label => { label.textContent = connected ? wallet.info().name : 'Conectar carteira'; });
        el('wallet-file').disabled = blocked;
        el('wallet-password').disabled = blocked || !!choices;
        el('wallet-choice').disabled = blocked || !choices;
        el('wallet-connect').disabled = blocked || (choices ? !el('wallet-choice').value : !el('wallet-file').files.length);
        el('wallet-lock').disabled = blocked;
    }
    let balanceRequest = null, balanceEpoch = 0;
    async function refreshBalance() {
        const epoch = ++balanceEpoch;
        if (balanceRequest) balanceRequest.abort();
        balanceRequest = null;
        el('wallet-balance-detail').hidden = true;
        if (!wallet.isUnlocked()) { el('wallet-balance').textContent = '—'; return; }
        el('wallet-balance').textContent = 'Consultando…';
        const controller = new AbortController();
        balanceRequest = controller;
        const timer = setTimeout(() => controller.abort(), 15000);
        try {
            const form = el('wallet-form');
            const body = new URLSearchParams({ __RequestVerificationToken: form.querySelector('[name="__RequestVerificationToken"]').value });
            wallet.addresses().forEach(address => body.append('addresses', address));
            const response = await fetch(form.dataset.balanceUrl, { method: 'POST', body, signal: controller.signal,
                credentials: 'same-origin', cache: 'no-store' });
            const data = await response.json();
            if (!response.ok || !data || !/^[0-9]+$/.test(data.balanceAtomic)) throw new Error('balance');
            if (epoch !== balanceEpoch || !wallet.isUnlocked()) return;
            el('wallet-balance').textContent = wallet.formatAtomic(data.balanceAtomic, 8) + ' POVIX';
            const details = [];
            for (const [field, label] of [['confirmedAtomic', 'Confirmado'], ['launchLockedAtomic', 'Bloqueado no lançamento'],
                ['poolReservedAtomic', 'Em pools'], ['pendingFeesAtomic', 'Taxas pendentes']]) {
                if (/^[0-9]+$/.test(data[field] || '') && (field === 'confirmedAtomic' || BigInt(data[field]) > 0n))
                    details.push(label + ': ' + wallet.formatAtomic(data[field], 8) + ' POVIX');
            }
            el('wallet-balance-detail').textContent = details.join(' · ');
            el('wallet-balance-detail').hidden = !details.length;
        } catch (_) {
            if (epoch === balanceEpoch && wallet.isUnlocked()) el('wallet-balance').textContent = 'Consulta indisponível. Abra a carteira para tentar novamente.';
        } finally {
            clearTimeout(timer);
            if (epoch === balanceEpoch) balanceRequest = null;
        }
    }
    function changed() { update(); refreshBalance(); document.dispatchEvent(new Event('povix-wallet-change')); }
    function reset() {
        choices = null;
        el('wallet-selection').hidden = true;
        el('wallet-choice').replaceChildren(new Option('Selecione uma carteira', ''));
        el('wallet-choice').required = false;
        el('wallet-password').value = '';
        el('wallet-password').required = false;
        el('wallet-password-field').hidden = true;
        el('wallet-connect').textContent = 'Ler carteiras do arquivo';
    }
    window.PovixWalletMenu = { setBusy(value) { pageBusy = value; update(); } };
    document.querySelectorAll('[data-wallet-open]').forEach(link => link.addEventListener('click', event => {
        event.preventDefault(); dialog.showModal(); refreshBalance();
    }));
    el('wallet-close').addEventListener('click', () => dialog.close());
    el('wallet-lock').addEventListener('click', () => {
        if (pending || pageBusy) return;
        wallet.lock(); reset(); el('wallet-file').value = ''; message('Carteira desconectada de todas as telas.'); changed();
    });
    el('wallet-file').addEventListener('change', async () => {
        reset(); message('');
        const file = el('wallet-file').files[0];
        if (file) {
            pending = true; update();
            try {
                const header = new Uint8Array(await file.slice(0, 4).arrayBuffer());
                const desktop = header.length === 4 && header[0] === 1 && header[1] === 0 && header[2] === 0 && header[3] === 0;
                el('wallet-password-field').hidden = desktop;
                el('wallet-password').required = !desktop;
            } catch (_) { message('Não foi possível ler o arquivo. Selecione novamente.'); }
            finally { pending = false; update(); }
        } else update();
    });
    el('wallet-choice').addEventListener('change', update);
    el('wallet-form').addEventListener('submit', async event => {
        event.preventDefault(); if (pending || pageBusy) return;
        pending = true; message(''); update();
        try {
            if (!choices) {
                choices = await wallet.readFile(el('wallet-file').files[0], el('wallet-password').value);
                el('wallet-password').value = '';
                el('wallet-password').required = false; el('wallet-password-field').hidden = true;
                el('wallet-choice').replaceChildren(new Option('Selecione uma carteira', ''));
                choices.forEach(item => {
                    const option = new Option(item.name + ' (' + item.addressCount + ' endereços)', item.id);
                    option.disabled = !item.addressCount; el('wallet-choice').add(option);
                });
                el('wallet-selection').hidden = false; el('wallet-choice').required = true;
                el('wallet-connect').textContent = 'Conectar carteira';
                el('wallet-choice-help').textContent = 'Esta carteira será usada em todas as telas. Carteiras sem endereços não podem assinar.';
            } else {
                await wallet.selectWallet(el('wallet-choice').value);
                el('wallet-file').value = ''; el('wallet-balance').textContent = 'Consultando…';
                changed(); dialog.close();
            }
        } catch (error) { message(error.message); }
        finally { pending = false; update(); }
    });
    // Defer restoration until the page controller has subscribed to wallet changes.
    async function restore() {
        pending = true; update();
        try { await wallet.restoreSession(); }
        finally { pending = false; changed(); }
    }
    document.addEventListener('DOMContentLoaded', restore);
    window.addEventListener('pageshow', event => { if (event.persisted) restore(); });
    update();
})();
