(function (global) {
    'use strict';
    const FORMAT = 'povix-dex-wallet-v1';
    const STORAGE = 'povix.dex.encrypted-wallet.v1';
    const ITERATIONS = 210000;
    const encoder = new TextEncoder();
    const decoder = new TextDecoder();
    const algorithm = { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' };
    let active = null;
    let staged = null;

    function requireCrypto() {
        if (!global.crypto || !global.crypto.subtle) throw new Error('Abra o Povix.Dex em HTTPS para assinar com segurança.');
    }
    function fromBase64(value) { return Uint8Array.from(atob(value), c => c.charCodeAt(0)); }
    function toBase64(value) {
        let text = '';
        for (const byte of new Uint8Array(value)) text += String.fromCharCode(byte);
        return btoa(text);
    }
    function concat(...arrays) {
        const result = new Uint8Array(arrays.reduce((sum, item) => sum + item.length, 0));
        let offset = 0;
        arrays.forEach(item => { result.set(item, offset); offset += item.length; });
        return result;
    }
    async function sha256(text) {
        return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(text))),
            byte => byte.toString(16).padStart(2, '0')).join('');
    }
    function base64url(base64) { return base64.replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_'); }
    function xmlParts(xml) {
        if (typeof xml !== 'string' || xml.length > 5000) throw new Error('Chave de carteira inválida.');
        const fields = {};
        // No DOM parsing is needed for this fixed RSA XML format.
        for (const name of ['Modulus', 'Exponent', 'D', 'P', 'Q', 'DP', 'DQ', 'InverseQ']) {
            const match = xml.match(new RegExp('<' + name + '>([A-Za-z0-9+/=]+)</' + name + '>'));
            if (!match) throw new Error('O arquivo não contém uma chave privada RSA completa.');
            fields[name] = match[1];
        }
        if (fromBase64(fields.Modulus).length !== 256) throw new Error('A carteira precisa usar chaves RSA de 2048 bits.');
        return fields;
    }
    async function importKey(xml) {
        const p = xmlParts(xml);
        const publicXml = '<RSAKeyValue><Modulus>' + p.Modulus + '</Modulus><Exponent>' + p.Exponent + '</Exponent></RSAKeyValue>';
        const jwk = { kty: 'RSA', n: base64url(p.Modulus), e: base64url(p.Exponent), d: base64url(p.D),
            p: base64url(p.P), q: base64url(p.Q), dp: base64url(p.DP), dq: base64url(p.DQ), qi: base64url(p.InverseQ), ext: false };
        const key = await crypto.subtle.importKey('jwk', jwk, algorithm, false, ['sign']);
        const verificationKey = await crypto.subtle.importKey('jwk', { kty: 'RSA', n: jwk.n, e: jwk.e, ext: false }, algorithm, false, ['verify']);
        const challenge = encoder.encode('Povix.Dex local wallet validation');
        if (!await crypto.subtle.verify(algorithm.name, verificationKey, await crypto.subtle.sign(algorithm.name, key, challenge), challenge))
            throw new Error('Os parâmetros da chave não correspondem à carteira.');
        return { xml, publicXml, address: await sha256(publicXml), key };
    }
    async function derive(password, salt) {
        if (typeof password !== 'string' || password.length < 10) throw new Error('Use uma senha de pelo menos 10 caracteres para o backup.');
        const material = await crypto.subtle.importKey('raw', encoder.encode(password), 'PBKDF2', false, ['deriveBits']);
        const bits = new Uint8Array(await crypto.subtle.deriveBits({ name: 'PBKDF2', salt, iterations: ITERATIONS, hash: 'SHA-256' }, material, 512));
        const aes = await crypto.subtle.importKey('raw', bits.slice(0, 32), 'AES-CBC', false, ['encrypt', 'decrypt']);
        const hmac = await crypto.subtle.importKey('raw', bits.slice(32), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']);
        bits.fill(0);
        return { aes, hmac };
    }
    async function read(record, password) {
        active = null;
        staged = null;
        requireCrypto();
        if (!record || record.format !== FORMAT || record.iterations !== ITERATIONS || typeof record.data !== 'string' || record.data.length > 9000000)
            throw new Error('O arquivo de carteiras não está em um formato reconhecido.');
        const salt = fromBase64(record.salt), iv = fromBase64(record.iv), cipher = fromBase64(record.data);
        if (salt.length !== 16 || iv.length !== 16 || cipher.length === 0) throw new Error('Backup inválido.');
        const protection = await derive(password, salt);
        if (!await crypto.subtle.verify('HMAC', protection.hmac, fromBase64(record.hmac), concat(salt, iv, cipher)))
            throw new Error('Senha incorreta ou backup alterado.');
        const clear = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-CBC', iv }, protection.aes, cipher));
        let data;
        try { data = JSON.parse(decoder.decode(clear)); }
        finally { clear.fill(0); }
        const legacy = Boolean(data && Array.isArray(data.PrivateKeys));
        const wallets = legacy ? [data] : data && data.Wallets;
        if (!Array.isArray(wallets) || wallets.length < 1 || wallets.length > 100 || wallets.some(item =>
            !item || !Array.isArray(item.PrivateKeys) || item.PrivateKeys.length > 1000))
            throw new Error('O arquivo não contém uma coleção válida de carteiras.');
        staged = { wallets, legacy, salt, protection, record };
        return wallets.map((item, index) => ({ id: String(index), name: typeof item.Name === 'string' ? item.Name.slice(0, 64) : 'Carteira ' + (index + 1), addressCount: item.PrivateKeys.length }));
    }
    async function selectWallet(id) {
        if (!staged || typeof id !== 'string' || !/^(0|[1-9][0-9]*)$/.test(id) || Number(id) >= staged.wallets.length)
            throw new Error('Selecione uma carteira salva no arquivo.');
        active = null;
        const selectedIndex = Number(id), data = staged.wallets[selectedIndex];
        if (data.PrivateKeys.length === 0)
            throw new Error('Esta carteira ainda não tem endereços. Selecione outra carteira ou gere um endereço no PrivateCoin.Desktop e leia o arquivo novamente.');
        const keys = new Map();
        for (const xml of data.PrivateKeys) {
            const item = await importKey(xml);
            if (keys.has(item.address)) throw new Error('O backup contém chaves duplicadas.');
            keys.set(item.address, item);
        }
        active = { ...staged, name: typeof data.Name === 'string' ? data.Name.slice(0, 64) : 'Minha carteira', keys, selectedIndex };
        try { await save(); }
        catch (error) { active = null; throw error; }
        return { name: active.name, addressCount: keys.size };
    }
    async function save() {
        const wallet = requireWallet();
        const iv = crypto.getRandomValues(new Uint8Array(16));
        const wallets = wallet.wallets.slice();
        wallets[wallet.selectedIndex] = { ...wallets[wallet.selectedIndex], Name: wallet.name, PrivateKeys: Array.from(wallet.keys.values(), item => item.xml) };
        const clear = encoder.encode(JSON.stringify(wallet.legacy ? wallets[0] : { Wallets: wallets }));
        let cipher;
        try { cipher = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-CBC', iv }, wallet.protection.aes, clear)); }
        finally { clear.fill(0); }
        const record = { format: FORMAT, iterations: ITERATIONS, salt: toBase64(wallet.salt), iv: toBase64(iv), data: toBase64(cipher),
            hmac: toBase64(await crypto.subtle.sign('HMAC', wallet.protection.hmac, concat(wallet.salt, iv, cipher))) };
        // Abort before preparation/signing if durable local storage is unavailable.
        try { global.localStorage.setItem(STORAGE, JSON.stringify(record)); }
        catch (_) { throw new Error('Não foi possível salvar a carteira cifrada neste navegador. Libere espaço ou permita o armazenamento local.'); }
        wallet.record = record;
        wallet.wallets = wallets;
        staged = { wallets, legacy: wallet.legacy, salt: wallet.salt, protection: wallet.protection, record };
    }
    function requireWallet() { if (!active) throw new Error('Desbloqueie sua carteira primeiro.'); return active; }
    function paddedJwk(value, width) {
        const bytes = fromBase64(value.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - value.length % 4) % 4));
        const padded = new Uint8Array(width || bytes.length);
        if (bytes.length > padded.length) throw new Error('Parâmetro RSA inválido.');
        padded.set(bytes, padded.length - bytes.length);
        return toBase64(padded);
    }
    async function createAddress() {
        const wallet = requireWallet();
        if (wallet.keys.size >= 1000) throw new Error('A carteira atingiu o limite de chaves deste formulário.');
        const pair = await crypto.subtle.generateKey({ ...algorithm, modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]) }, true, ['sign', 'verify']);
        const jwk = await crypto.subtle.exportKey('jwk', pair.privateKey);
        const fields = { Modulus: paddedJwk(jwk.n, 256), Exponent: paddedJwk(jwk.e), P: paddedJwk(jwk.p, 128), Q: paddedJwk(jwk.q, 128),
            DP: paddedJwk(jwk.dp, 128), DQ: paddedJwk(jwk.dq, 128), InverseQ: paddedJwk(jwk.qi, 128), D: paddedJwk(jwk.d, 256) };
        const xml = '<RSAKeyValue>' + Object.entries(fields).map(([name, value]) => '<' + name + '>' + value + '</' + name + '>').join('') + '</RSAKeyValue>';
        const item = await importKey(xml);
        wallet.keys.set(item.address, item);
        try { await save(); }
        catch (error) { wallet.keys.delete(item.address); throw error; }
        return item.address;
    }
    function field(value) { return '|' + (value == null ? -1 : value.length) + ':' + (value == null ? '' : value); }
    async function verifyDraft(draft, expected, networkId, changeAddress) {
        const wallet = requireWallet();
        if (draft.networkId !== networkId || draft.consensusVersion !== 17 || draft.name !== expected.Name || draft.symbol !== expected.Symbol ||
            draft.decimals !== Number(expected.Decimals) || draft.supplyAtomic !== parseSupply(expected.Supply, Number(expected.Decimals)) ||
            draft.feeAtomic !== expected.FeeAtomic || draft.destinationAddress !== expected.DestinationAddress || (draft.changeAddress && draft.changeAddress !== changeAddress) ||
            !/^[0-9a-f]{64}$/.test(draft.tokenId) || !/^[0-9]+$/.test(draft.timestampUtcTicks) ||
            !Array.isArray(draft.inputs) || draft.inputs.length === 0 || draft.inputs.length > 1000 ||
            !Array.isArray(draft.inputAddresses) || draft.inputAddresses.length !== draft.inputs.length ||
            !/^[0-9]+$/.test(draft.changeAtomic) || !/^[0-9]+$/.test(draft.feeAtomic) ||
            BigInt(draft.feeAtomic) < 1n || BigInt(draft.feeAtomic) > 100000000n)
            throw new Error('A preparação recebida não corresponde ao cadastro.');
        let payload = draft.timestampUtcTicks;
        for (let index = 0; index < draft.inputs.length; index++) {
            const input = draft.inputs[index];
            if (!wallet.keys.has(input.address) || draft.inputAddresses[index] !== input.address ||
                !/^[0-9a-f]{64}$/.test(input.transactionId) || !Number.isSafeInteger(input.outputIndex) || input.outputIndex < 0)
                throw new Error('A preparação contém uma entrada que não pertence à carteira.');
            payload += '|' + input.transactionId + ':' + input.outputIndex;
        }
        const first = draft.inputs[0];
        if (await sha256('povix-token-v1|' + first.transactionId + '|' + first.outputIndex) !== draft.tokenId)
            throw new Error('Identificador do token inválido.');
        payload += '|' + draft.supplyAtomic + ':' + draft.destinationAddress;
        const hasChange = BigInt(draft.changeAtomic) > 0n;
        if (hasChange && (!draft.changeAddress || !wallet.keys.has(draft.changeAddress))) throw new Error('O troco deve voltar à sua carteira.');
        if (hasChange) payload += '|' + draft.changeAtomic + ':' + draft.changeAddress;
        payload += '|fee:' + draft.feeAtomic + '|kind:3|validator:|reward:|owned:|assets-v1|' + field(draft.tokenId);
        if (hasChange) payload += field(null);
        payload += field(draft.tokenId) + field(draft.name) + field(draft.symbol) + '|' + draft.decimals + '|' + draft.supplyAtomic;
        if (toBase64(encoder.encode(payload)) !== draft.signingPayload) throw new Error('O conteúdo para assinatura difere da revisão.');
        if (!Number.isFinite(Date.parse(draft.expiresUtc)) || Date.parse(draft.expiresUtc) <= Date.now()) throw new Error('A preparação expirou. Prepare novamente.');
        return true;
    }
    function quoteExchange(reserveIn, reserveOut, amount) {
        const rIn = BigInt(reserveIn), rOut = BigInt(reserveOut), value = BigInt(amount);
        if (rIn <= 0n || rOut <= 0n || value <= 0n) throw new Error('Quantidade ou reservas inválidas.');
        const adjusted = value * 9970n, result = rOut * adjusted / (rIn * 10000n + adjusted);
        if (result <= 0n || result >= rOut) throw new Error('Liquidez insuficiente para esta quantidade.');
        return result.toString();
    }
    async function verifyExchangeDraft(draft, expected, networkId, provider) {
        const wallet = requireWallet(), id = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
        const atomic = value => typeof value === 'string' && /^(0|[1-9][0-9]*)$/.test(value) && BigInt(value) <= 9223372036854775807n;
        const fail = () => { throw new Error('A preparação não corresponde à pool ou à troca revisada.'); };
        if (draft.networkId !== networkId || draft.consensusVersion !== 17 || draft.operation !== expected.operation ||
            !/^[0-9a-f]{32}$/.test(draft.draftId) || !atomic(draft.timestampUtcTicks) || !atomic(draft.feeAtomic) ||
            draft.feeAtomic !== expected.feeAtomic || BigInt(draft.feeAtomic) < 1n || BigInt(draft.feeAtomic) > 100000000n ||
            draft.poolFeeBasisPoints !== 30 || !id(draft.providerAddress) || !id(draft.changeAddress) ||
            draft.changeAddress !== expected.changeAddress || !Array.isArray(draft.inputs) || !draft.inputs.length || draft.inputs.length > 1000 ||
            !Array.isArray(draft.outputs) || !Number.isSafeInteger(draft.userInputCount) || draft.userInputCount < 1 ||
            !Number.isFinite(Date.parse(draft.expiresUtc)) || Date.parse(draft.expiresUtc) <= Date.now()) fail();
        for (const side of ['assetA', 'assetB']) {
            const actual = draft[side], wanted = expected[side];
            if (!actual || !wanted || actual.id !== wanted.id || actual.name !== wanted.name || actual.symbol !== wanted.symbol || actual.decimals !== wanted.decimals ||
                !(actual.id === 'povix' || id(actual.id)) || !Number.isInteger(actual.decimals) || actual.decimals < 0 || actual.decimals > 8) fail();
        }
        if (draft.assetA.id === draft.assetB.id || !atomic(draft.reserveAAtomic) || !atomic(draft.reserveBAtomic) ||
            draft.reserveAAtomic !== expected.reserveAAtomic || draft.reserveBAtomic !== expected.reserveBAtomic ||
            BigInt(draft.reserveAAtomic) <= 0n || BigInt(draft.reserveBAtomic) <= 0n) fail();
        const allowed = new Set(['povix', draft.assetA.id, draft.assetB.id]), seen = new Set(), totals = new Map();
        let payload = draft.timestampUtcTicks;
        draft.inputs.forEach((input, index) => {
            const key = input.transactionId + ':' + input.outputIndex;
            if (!id(input.transactionId) || !id(input.address) || !Number.isSafeInteger(input.outputIndex) || input.outputIndex < 0 ||
                !allowed.has(input.assetId) || !atomic(input.amountAtomic) || BigInt(input.amountAtomic) <= 0n || seen.has(key)) fail();
            seen.add(key); payload += '|' + key;
            const owned = draft.operation !== 'swap' || (provider ? index >= draft.userInputCount :
                index < draft.userInputCount || wallet.keys.has(draft.providerAddress));
            if (owned && !wallet.keys.has(input.address)) fail();
            if (index < draft.userInputCount) totals.set(input.assetId, (totals.get(input.assetId) || 0n) + BigInt(input.amountAtomic));
        });
        const output = (assetId, amount, address) => ({ assetId, amountAtomic: String(amount), address });
        const spends = new Map([['povix', BigInt(draft.feeAtomic)]]), wantedOutputs = [];
        const addSpend = (assetId, amount) => spends.set(assetId, (spends.get(assetId) || 0n) + BigInt(amount));
        const a = BigInt(draft.reserveAAtomic), b = BigInt(draft.reserveBAtomic);
        if (draft.operation === 'pool') {
            if (provider || draft.userInputCount !== draft.inputs.length || draft.providerAddress !== draft.changeAddress || !wallet.keys.has(draft.changeAddress)) fail();
            wantedOutputs.push(output(draft.assetA.id, a, draft.providerAddress), output(draft.assetB.id, b, draft.providerAddress));
            addSpend(draft.assetA.id, a); addSpend(draft.assetB.id, b);
        } else if (draft.operation === 'swap' || draft.operation === 'lock' || draft.operation === 'close') {
            if (draft.inputs.length !== draft.userInputCount + 2 || draft.poolId !== expected.poolId || draft.reserveTransactionId !== expected.reserveTransactionId ||
                draft.providerAddress !== expected.providerAddress) fail();
            if (draft.operation === 'swap' && (draft.inputSide !== expected.inputSide ||
                draft.amountAtomic !== expected.amountAtomic || draft.minimumReceiveAtomic !== expected.minimumReceiveAtomic ||
                !atomic(draft.amountAtomic) || !atomic(draft.minimumReceiveAtomic) || BigInt(draft.minimumReceiveAtomic) <= 0n ||
                (draft.inputSide !== 0 && draft.inputSide !== 1))) fail();
            for (let side = 0; side < 2; side++) {
                const input = draft.inputs[draft.userInputCount + side], asset = side === 0 ? draft.assetA : draft.assetB;
                if (input.transactionId !== expected.reserveTransactionId || input.outputIndex !== side || input.address !== draft.providerAddress ||
                    input.assetId !== asset.id || input.amountAtomic !== (side === 0 ? draft.reserveAAtomic : draft.reserveBAtomic)) fail();
            }
            if (draft.operation !== 'swap') {
                if (provider || draft.changeAddress !== draft.providerAddress || !wallet.keys.has(draft.providerAddress) ||
                    draft.amountAtomic !== '0' || draft.minimumReceiveAtomic !== '0' || draft.receiveAtomic !== '0' || draft.inputSide !== 0) fail();
                wantedOutputs.push(output(draft.assetA.id, a, draft.providerAddress), output(draft.assetB.id, b, draft.providerAddress));
            } else {
                const amount = BigInt(draft.amountAtomic), receive = BigInt(quoteExchange(draft.inputSide === 0 ? a : b, draft.inputSide === 0 ? b : a, amount));
                if (draft.receiveAtomic !== receive.toString() || receive < BigInt(draft.minimumReceiveAtomic)) fail();
                wantedOutputs.push(output(draft.assetA.id, draft.inputSide === 0 ? a + amount : a - receive, draft.providerAddress),
                    output(draft.assetB.id, draft.inputSide === 1 ? b + amount : b - receive, draft.providerAddress),
                    output(draft.inputSide === 0 ? draft.assetB.id : draft.assetA.id, receive, draft.changeAddress));
                addSpend(draft.inputSide === 0 ? draft.assetA.id : draft.assetB.id, amount);
            }
        } else fail();
        for (const assetId of Array.from(new Set([...spends.keys(), ...totals.keys()])).sort((x, y) => x === 'povix' ? -1 : y === 'povix' ? 1 : x.localeCompare(y))) {
            const change = (totals.get(assetId) || 0n) - (spends.get(assetId) || 0n);
            if (change < 0n || !spends.has(assetId)) fail();
            if (change > 0n) wantedOutputs.push(output(assetId, change, draft.changeAddress));
        }
        if (draft.outputs.length !== wantedOutputs.length) fail();
        draft.outputs.forEach((item, index) => {
            const wanted = wantedOutputs[index];
            if (!atomic(item.amountAtomic) || BigInt(item.amountAtomic) <= 0n || item.amountAtomic !== wanted.amountAtomic || item.address !== wanted.address || item.assetId !== wanted.assetId) fail();
            payload += '|' + item.amountAtomic + ':' + item.address;
        });
        const kind = { pool: 7, swap: 8, lock: 7, close: 9 }[draft.operation];
        if (draft.transactionKind !== kind) fail();
        payload += '|fee:' + draft.feeAtomic + '|kind:' + kind + '|validator:|reward:|owned:|assets-v1|';
        draft.outputs.forEach(item => { payload += field(item.assetId === 'povix' ? null : item.assetId); });
        if (draft.operation === 'swap') payload += '|pool-swap-v1|' + draft.inputSide + '|' + draft.amountAtomic;
        if (toBase64(encoder.encode(payload)) !== draft.signingPayload) fail();
        return true;
    }
    function transferAuthorization(draft) {
        return toBase64(encoder.encode('povix-dex-token-transfer-authorize-v1|' + draft.networkId + '|' + draft.draftId + '|' + draft.tokenId + '|' + draft.signingPayload));
    }
    async function verifyTransferDraft(draft, expected, networkId, changeAddress) {
        const wallet = requireWallet();
        const atomic = value => typeof value === 'string' && /^[0-9]+$/.test(value) && BigInt(value) <= 9223372036854775807n;
        if (draft.networkId !== networkId || draft.consensusVersion !== 17 || draft.tokenId !== expected.TokenId || !/^[0-9a-f]{64}$/.test(draft.tokenId) ||
            draft.name !== expected.Name || draft.symbol !== expected.Symbol || draft.decimals !== expected.Decimals ||
            draft.amountAtomic !== parseSupply(expected.Amount, expected.Decimals) || draft.destinationAddress !== expected.DestinationAddress ||
            draft.creatorAddress !== expected.CreatorAddress || !wallet.keys.has(draft.creatorAddress) ||
            !/^[0-9a-f]{32}$/.test(draft.draftId) || !/^[0-9]+$/.test(draft.timestampUtcTicks) ||
            draft.changeAddress !== changeAddress || !wallet.keys.has(changeAddress) ||
            !atomic(draft.tokenChangeAtomic) || !atomic(draft.povixChangeAtomic) || !atomic(draft.feeAtomic) ||
            draft.feeAtomic !== expected.FeeAtomic || BigInt(draft.feeAtomic) < 1n || BigInt(draft.feeAtomic) > 100000000n ||
            !Array.isArray(draft.inputs) || draft.inputs.length < 2 || draft.inputs.length > 1000 ||
            !Array.isArray(draft.inputAddresses) || draft.inputAddresses.length !== draft.inputs.length)
            throw new Error('A preparação não corresponde à movimentação revisada ou à carteira do criador.');
        let payload = draft.timestampUtcTicks;
        const used = new Set();
        for (let index = 0; index < draft.inputs.length; index++) {
            const input = draft.inputs[index];
            const reference = input.transactionId + ':' + input.outputIndex;
            if (!wallet.keys.has(input.address) || draft.inputAddresses[index] !== input.address ||
                !/^[0-9a-f]{64}$/.test(input.transactionId) || !Number.isSafeInteger(input.outputIndex) || input.outputIndex < 0 || used.has(reference))
                throw new Error('A movimentação contém uma entrada inválida ou de outra carteira.');
            used.add(reference);
            payload += '|' + reference;
        }
        payload += '|' + draft.amountAtomic + ':' + draft.destinationAddress;
        const tokenChange = BigInt(draft.tokenChangeAtomic) > 0n, povixChange = BigInt(draft.povixChangeAtomic) > 0n;
        if (tokenChange) payload += '|' + draft.tokenChangeAtomic + ':' + changeAddress;
        if (povixChange) payload += '|' + draft.povixChangeAtomic + ':' + changeAddress;
        payload += '|fee:' + draft.feeAtomic + '|kind:4|validator:|reward:|owned:|assets-v1|' + field(draft.tokenId);
        if (tokenChange) payload += field(draft.tokenId);
        if (povixChange) payload += field(null);
        if (toBase64(encoder.encode(payload)) !== draft.signingPayload || transferAuthorization(draft) !== draft.authorizationPayload)
            throw new Error('O conteúdo da assinatura ou da autorização do criador difere da revisão.');
        if (!Number.isFinite(Date.parse(draft.expiresUtc)) || Date.parse(draft.expiresUtc) <= Date.now())
            throw new Error('A preparação expirou. Prepare novamente.');
        return true;
    }
    function parseSupply(text, decimals) {
        if (!/^[0-9]+([.,][0-9]+)?$/.test(text) || !Number.isInteger(decimals) || decimals < 0 || decimals > 8) throw new Error('Quantidade inválida.');
        const parts = text.replace(',', '.').split('.');
        const fraction = parts[1] || '';
        if (fraction.length > decimals) throw new Error('A quantidade tem mais casas decimais do que o token permite.');
        const amount = BigInt(parts[0] + fraction.padEnd(decimals, '0'));
        if (amount <= 0n || amount > 9223372036854775807n) throw new Error('A quantidade ultrapassa o limite permitido ou não é positiva.');
        return amount.toString();
    }
    function formatAtomic(value, decimals) {
        const text = BigInt(value).toString().padStart(decimals + 1, '0');
        const whole = (decimals ? text.slice(0, -decimals) : text).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
        return whole + (decimals ? ',' + text.slice(-decimals) : '');
    }
    global.PovixTokenWallet = {
        async readFile(file, password) {
            active = null;
            staged = null;
            if (!file || file.size > 9000000) throw new Error('Escolha um arquivo de carteiras de até 9 MB.');
            const bytes = new Uint8Array(await file.arrayBuffer());
            // DPAPI files are binary. They are opened only by the user's local Windows helper.
            // Portable encrypted backups are parsed and decrypted entirely in this browser.
            let record;
            if (bytes.length >= 20 && bytes[0] === 1 && bytes[1] === 0 && bytes[2] === 0 && bytes[3] === 0 &&
                toBase64(bytes.slice(4, 20)) === '0Iyd3wEV0RGMegDAT8KX6w==') {
                if (password.length < 10) throw new Error('Defina uma senha de pelo menos 10 caracteres para a cópia local.');
                const controller = new AbortController();
                const timeout = setTimeout(() => controller.abort(), 20000);
                try {
                    const response = await global.fetch('http://127.0.0.1:4781/import', {
                        method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'omit',
                        redirect: 'error', referrerPolicy: 'no-referrer', signal: controller.signal,
                        body: JSON.stringify({ file: toBase64(bytes), password })
                    });
                    const result = await response.json();
                    if (!response.ok || result.error) {
                        const code = typeof result.code === 'string' && /^[a-z_]{1,40}$/.test(result.code) ? ' (código: ' + result.code + ')' : '';
                        throw new Error((result.error || 'Não foi possível abrir o arquivo no Windows.') + code);
                    }
                    record = result.wallet;
                } catch (error) {
                    if (error instanceof TypeError || error.name === 'AbortError')
                        throw new Error('Inicie o Povix.WalletBridge no Windows com este site autorizado e permita o acesso à rede local no navegador. O arquivo do Desktop é aberto somente no seu dispositivo.');
                    throw error;
                } finally { clearTimeout(timeout); bytes.fill(0); }
            } else {
                try { record = JSON.parse(decoder.decode(bytes).replace(/^\uFEFF/, '')); }
                catch (_) { throw new Error('Arquivo inválido. Escolha wallet.dat, wallets.dat ou um backup cifrado do DEX.'); }
                finally { bytes.fill(0); }
            }
            return read(record, password);
        },
        async readSaved(password) {
            const saved = global.localStorage.getItem(STORAGE);
            if (!saved) throw new Error('Escolha o arquivo de carteiras primeiro.');
            return read(JSON.parse(saved), password);
        },
        selectWallet,
        async importFile(file, password) {
            const choices = await this.readFile(file, password);
            if (choices.length !== 1) throw new Error('O arquivo contém várias carteiras. Selecione qual deseja abrir.');
            return selectWallet(choices[0].id);
        },
        async unlock(password) {
            const saved = global.localStorage.getItem(STORAGE);
            if (!saved) throw new Error('Importe um backup de carteira primeiro.');
            const choices = await read(JSON.parse(saved), password);
            if (choices.length !== 1) throw new Error('O arquivo contém várias carteiras. Selecione qual deseja abrir.');
            return selectWallet(choices[0].id);
        },
        hasSavedWallet() { try { return Boolean(global.localStorage.getItem(STORAGE)); } catch (_) { return false; } },
        lock() { active = null; staged = null; },
        isUnlocked() { return Boolean(active); },
        publicKeys() { return Array.from(requireWallet().keys.values(), item => toBase64(encoder.encode(item.publicXml))); },
        addresses() { return Array.from(requireWallet().keys.keys()); },
        createAddress, verifyDraft, verifyTransferDraft, verifyExchangeDraft, quoteExchange, parseSupply, formatAtomic,
        async authorizeProfile(draft, expected, networkId) {
            const creator = requireWallet().keys.get(expected.creatorAddress);
            if (!creator || draft.networkId !== networkId || draft.creatorAddress !== expected.creatorAddress ||
                draft.tokenId !== expected.tokenId || draft.name !== expected.name || draft.symbol !== expected.symbol ||
                draft.description !== expected.description || draft.website !== expected.website || draft.renounced !== expected.renounced ||
                draft.revision !== expected.revision + 1 || !/^[0-9a-f]{32}$/.test(draft.draftId) || !/^[0-9a-f]{64}$/.test(draft.creationId))
                throw new Error('A preparação não corresponde ao perfil revisado.');
            const payload = toBase64(encoder.encode([
                'povix-dex-token-profile-v1', networkId, draft.draftId, draft.tokenId, draft.creationId,
                draft.creatorAddress, String(draft.revision), draft.renounced ? '1' : '0',
                ...[draft.name, draft.symbol, draft.description, draft.website].map(value => toBase64(encoder.encode(value)))
            ].join('|')));
            if (payload !== draft.authorizationPayload) throw new Error('A autorização do perfil foi alterada.');
            return toBase64(await crypto.subtle.sign(algorithm.name, creator.key, fromBase64(payload)));
        },
        async authorizeTransfer(draft) {
            const wallet = requireWallet(), creator = wallet.keys.get(draft.creatorAddress);
            if (!creator || transferAuthorization(draft) !== draft.authorizationPayload)
                throw new Error('A carteira não pode autorizar esta movimentação como criadora do token.');
            return toBase64(await crypto.subtle.sign(algorithm.name, creator.key, fromBase64(draft.authorizationPayload)));
        },
        backup() {
            const wallet = requireWallet();
            const url = URL.createObjectURL(new Blob([JSON.stringify(wallet.record)], { type: 'application/json' }));
            const anchor = document.createElement('a');
            anchor.href = url;
            anchor.download = 'povix-dex-carteira-atualizada.povixwallet';
            anchor.click();
            setTimeout(() => URL.revokeObjectURL(url), 10000);
        },
        async sign(draft) {
            const wallet = requireWallet(), payload = fromBase64(draft.signingPayload);
            const signatures = [];
            for (const address of draft.inputAddresses) {
                const item = wallet.keys.get(address);
                if (!item) throw new Error('Entrada desconhecida na carteira.');
                signatures.push(toBase64(await crypto.subtle.sign(algorithm.name, item.key, payload)));
            }
            return signatures;
        }
    };
    if (global.addEventListener) global.addEventListener('pagehide', () => { active = null; staged = null; });
})(typeof window === 'undefined' ? globalThis : window);
