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
    const SESSION = 'povix.dex.wallet-session.v1';
    let sessionPassword = null;
    let automaticPassword = false;
    function rememberSession() {
        if (!global.sessionStorage || !active || !sessionPassword) return;
        try { global.sessionStorage.setItem(SESSION, JSON.stringify({ password: sessionPassword, automaticPassword, selected: String(active.selectedIndex), record: active.record })); }
        catch (_) { throw new Error('Permita o armazenamento de sessão para manter a carteira conectada entre as telas.'); }
    }

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
        rememberSession();
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
        if (draft.networkId !== networkId || draft.consensusVersion !== 22 || draft.name !== expected.Name || draft.symbol !== expected.Symbol ||
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
        const policy = expected.PolicyValues || null;
        const rate = String(expected.AutoBurnBasisPoints || '0'), fixed = String(expected.AutoBurnFixedAtomic || '0');
        const burnPolicy = rate !== '0' || fixed !== '0' ? '|auto-burn-v1:' + rate + ':' + fixed : '';
        if ((draft.policyCanonical || null) !== (policy ? policy.map(field).join('') + burnPolicy : null)) throw new Error('As regras do token diferem da revisão.');
        const tranches = policy && Number(policy[12]) > 0 ? Number(policy[12]) : 1;
        const locks = [];
        for (let index = 0; index < tranches; index++) {
            const supply = BigInt(draft.supplyAtomic), count = BigInt(tranches);
            payload += '|' + (supply / count + (BigInt(index) < supply % count ? 1n : 0n)) + ':' + draft.destinationAddress;
            locks.push(policy && Number(policy[12]) > 0 ? Number(policy[10]) + index * Number(policy[11]) : 0);
        }
        const hasChange = BigInt(draft.changeAtomic) > 0n;
        if (hasChange && (!draft.changeAddress || !wallet.keys.has(draft.changeAddress))) throw new Error('O troco deve voltar à sua carteira.');
        if (hasChange) payload += '|' + draft.changeAtomic + ':' + draft.changeAddress;
        payload += '|fee:' + draft.feeAtomic + '|kind:3|validator:|reward:|owned:|assets-v1|' + field(draft.tokenId).repeat(tranches);
        if (hasChange) payload += field(null);
        payload += field(draft.tokenId) + field(draft.name) + field(draft.symbol) + '|' + draft.decimals + '|' + draft.supplyAtomic;
        if (policy) payload += '|token-policy-v1|' + draft.policyCanonical;
        if (locks.some(height => height !== 0)) payload += '|token-locks-v1|' + locks.concat(hasChange ? [0] : []).join(',');
        if (toBase64(encoder.encode(payload)) !== draft.signingPayload) throw new Error('O conteúdo para assinatura difere da revisão.');
        if (!Number.isFinite(Date.parse(draft.expiresUtc)) || Date.parse(draft.expiresUtc) <= Date.now()) throw new Error('A preparação expirou. Prepare novamente.');
        return true;
    }
    async function verifyManagementDraft(draft, expected, networkId) {
        const current = requireWallet(), token = expected.token;
        if (draft.networkId !== networkId || draft.consensusVersion !== 22 || draft.tokenId !== token.tokenId ||
            draft.operation !== expected.operation || draft.amountAtomic !== expected.amountAtomic || draft.feeAtomic !== expected.feeAtomic ||
            draft.revision !== token.revision || !/^[0-9a-f]{32}$/.test(draft.draftId) || !/^[0-9]+$/.test(draft.timestampUtcTicks) ||
            !/^[0-9]+$/.test(draft.revision) || !Array.isArray(draft.inputs) || draft.inputs.length < 1 || draft.inputs.length > 1000 ||
            !Array.isArray(draft.inputAddresses) || draft.inputs.length !== draft.inputAddresses.length ||
            !Array.isArray(draft.outputs) || draft.outputs.length > 2 || !Array.isArray(draft.administratorAddresses) ||
            !Array.isArray(draft.administratorPublicKeys) || draft.administratorAddresses.length !== draft.administratorPublicKeys.length ||
            !Number.isFinite(Date.parse(draft.expiresUtc)) || Date.parse(draft.expiresUtc) <= Date.now()) throw new Error('A preparação não corresponde à operação solicitada.');
        let payload = draft.timestampUtcTicks;
        for (let i = 0; i < draft.inputs.length; i++) {
            const input = draft.inputs[i];
            if (!current.keys.has(input.address) || input.address !== draft.inputAddresses[i] || !/^[0-9a-f]{64}$/.test(input.transactionId) ||
                !Number.isSafeInteger(input.outputIndex) || input.outputIndex < 0) throw new Error('Entrada desconhecida na operação.');
            payload += '|' + input.transactionId + ':' + input.outputIndex;
        }
        let assets = 0;
        for (const output of draft.outputs) {
            if (!/^[0-9]+$/.test(output.amountAtomic) || BigInt(output.amountAtomic) < 1n || BigInt(output.amountAtomic) > 9223372036854775807n ||
                !/^[0-9a-f]{64}$/.test(output.address)) throw new Error('Saída inválida na operação.');
            if (output.assetId != null) {
                assets++;
                if (output.assetId !== token.tokenId || !['mint', 'burn'].includes(expected.operation) ||
                    (expected.operation === 'mint' ? output.address !== expected.destination || output.amountAtomic !== expected.amountAtomic : output.address !== expected.changeAddress)) throw new Error('Saída de token não autorizada.');
            } else if (output.address !== expected.changeAddress) throw new Error('O troco precisa voltar à sua carteira.');
            payload += '|' + output.amountAtomic + ':' + output.address;
        }
        if (assets > 1 || expected.operation === 'mint' && assets !== 1 || draft.outputs.filter(output => output.assetId == null).length > 1) throw new Error('Quantidade de saídas inválida.');
        payload += '|fee:' + draft.feeAtomic + '|kind:12|validator:|reward:|owned:';
        if (assets) payload += '|assets-v1|' + draft.outputs.map(output => field(output.assetId)).join('');
        payload += '|token-command-v1|' + token.tokenId + ':' + expected.operation + ':' + expected.amountAtomic + ':' + token.revision;
        const seen = new Set();
        for (let i = 0; i < draft.administratorAddresses.length; i++) {
            const address = draft.administratorAddresses[i], publicKey = draft.administratorPublicKeys[i];
            if (!current.keys.has(address) || !token.administrators.includes(address) || seen.has(address) || await sha256(publicKey) !== address) throw new Error('Administrador inválido na preparação.');
            seen.add(address); payload += field(publicKey);
        }
        if (expected.operation === 'burn' ? seen.size !== 0 : seen.size !== token.requiredSignatures) throw new Error('Número incorreto de assinaturas administrativas.');
        if (toBase64(encoder.encode(payload)) !== draft.signingPayload) throw new Error('O conteúdo da operação difere da revisão.');
        return true;
    }
    function quoteExchange(reserveIn, reserveOut, amount) {
        const rIn = BigInt(reserveIn), rOut = BigInt(reserveOut), value = BigInt(amount);
        if (rIn <= 0n || rOut <= 0n || value <= 0n) throw new Error('Quantidade ou reservas inválidas.');
        const adjusted = value * 9970n, result = rOut * adjusted / (rIn * 10000n + adjusted);
        if (result <= 0n || result >= rOut) throw new Error('Liquidez insuficiente para esta quantidade.');
        return result.toString();
    }
    function normalizePoolBurn(policy, a, b) {
        if (policy == null) return null;
        const rate = policy.basisPoints, fixedA = String(policy.fixedAAtomic), fixedB = String(policy.fixedBAtomic);
        if (!Number.isInteger(rate) || rate < 0 || rate > 10000 || !/^[0-9]+$/.test(fixedA) || !/^[0-9]+$/.test(fixedB) ||
            BigInt(fixedA) > 9223372036854775807n || BigInt(fixedB) > 9223372036854775807n ||
            rate > 0 && (BigInt(fixedA) !== 0n || BigInt(fixedB) !== 0n) || rate === 0 && BigInt(fixedA) === 0n && BigInt(fixedB) === 0n ||
            a.id === 'povix' && BigInt(fixedA) !== 0n || b.id === 'povix' && BigInt(fixedB) !== 0n) throw new Error('Regra de queima da pool inválida.');
        return { basisPoints: rate, fixedAAtomic: BigInt(fixedA).toString(), fixedBAtomic: BigInt(fixedB).toString() };
    }
    function quotePoolSwap(reserveIn, reserveOut, amount, assetA, assetB, inputSide, policy) {
        const rules = normalizePoolBurn(policy, assetA, assetB), gross = BigInt(quoteExchange(reserveIn, reserveOut, amount)), value = BigInt(amount);
        function burn(side, quantity) {
            if (!rules || [assetA, assetB][side].id === 'povix') return 0n;
            return rules.basisPoints > 0 ? quantity * BigInt(rules.basisPoints) / 10000n : BigInt(side === 0 ? rules.fixedAAtomic : rules.fixedBAtomic);
        }
        const inputBurn = burn(inputSide, value), outputBurn = burn(1 - inputSide, gross);
        if (outputBurn >= gross || value + inputBurn > 9223372036854775807n) throw new Error('A queima consome todo o recebimento ou supera o limite do token.');
        return { grossReceiveAtomic: gross.toString(), receiveAtomic: (gross - outputBurn).toString(), inputBurnAtomic: inputBurn.toString(), outputBurnAtomic: outputBurn.toString(), inputDebitAtomic: (value + inputBurn).toString() };
    }
    async function verifyExchangeDraft(draft, expected, networkId) {
        const wallet = requireWallet(), id = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
        const atomic = value => typeof value === 'string' && /^(0|[1-9][0-9]*)$/.test(value) && BigInt(value) <= 9223372036854775807n;
        const fail = () => { throw new Error('A preparação não corresponde à pool ou à troca revisada.'); };
        if (draft.networkId !== networkId || draft.consensusVersion !== 22 || draft.operation !== expected.operation ||
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
        const burnRules = normalizePoolBurn(draft.burnPolicy, draft.assetA, draft.assetB);
        const wantedBurn = normalizePoolBurn(expected.burnPolicy, expected.assetA, expected.assetB);
        if (Boolean(draft.burnEnabled) !== Boolean(expected.burnEnabled) || Boolean(draft.burnEnabled) !== Boolean(burnRules) || JSON.stringify(burnRules) !== JSON.stringify(wantedBurn)) fail();
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
            const owned = !['swap', 'add', 'remove', 'launch-contribute', 'launch-refund'].includes(draft.operation) || index < draft.userInputCount;
            if (owned && !wallet.keys.has(input.address)) fail();
            if (index < draft.userInputCount) totals.set(input.assetId, (totals.get(input.assetId) || 0n) + BigInt(input.amountAtomic));
        });
        const output = (assetId, amount, address) => ({ assetId, amountAtomic: String(amount), address });
        const spends = new Map([['povix', BigInt(draft.feeAtomic)]]), wantedOutputs = [];
        const addSpend = (assetId, amount) => spends.set(assetId, (spends.get(assetId) || 0n) + BigInt(amount));
        const a = BigInt(draft.reserveAAtomic), b = BigInt(draft.reserveBAtomic);
        const participation = value => {
            if (!value || !Array.isArray(value.holders) || value.holders.length > 1000) fail();
            const map = new Map();
            const locked = value.lockedSharesAtomic || '0';
            if (!atomic(locked)) fail();
            map.lockedShares = BigInt(locked);
            if (!value.holders.length && map.lockedShares === 0n) fail();
            for (const holder of value.holders) {
                if (!id(holder.address) || !atomic(holder.sharesAtomic) || BigInt(holder.sharesAtomic) <= 0n || map.has(holder.address)) fail();
                map.set(holder.address, BigInt(holder.sharesAtomic));
            }
            const total = [...map.values()].reduce((sum, amount) => sum + amount, map.lockedShares);
            if (!atomic(value.totalSharesAtomic) || total !== BigInt(value.totalSharesAtomic)) fail();
            return map;
        };
        const canonical = map => [...map.entries()].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([address, shares]) => address + ':' + shares).join(',') + (map.lockedShares > 0n ? '|locked:' + map.lockedShares : '');
        const launchState = value => {
            if (!value || !atomic(value.targetAtomic) || BigInt(value.targetAtomic) <= 1n || !atomic(value.saleTokensAtomic) || BigInt(value.saleTokensAtomic) <= 0n ||
                !atomic(value.totalTokensAtomic) || BigInt(value.totalTokensAtomic) <= BigInt(value.saleTokensAtomic) ||
                !Number.isSafeInteger(value.deadlineHeight) || value.deadlineHeight < 1 || typeof value.allowUserLiquidity !== 'boolean' ||
                !Array.isArray(value.contributors) || value.contributors.length < 1 || value.contributors.length > 100) fail();
            const amounts = new Map();
            for (const item of value.contributors) {
                if (!id(item.address) || !atomic(item.amountAtomic) || BigInt(item.amountAtomic) <= 0n || amounts.has(item.address) ||
                    BigInt(value.saleTokensAtomic) * BigInt(item.amountAtomic) / BigInt(value.targetAtomic) <= 0n) fail();
                amounts.set(item.address, BigInt(item.amountAtomic));
            }
            const raised = [...amounts.values()].reduce((sum, amount) => sum + amount, 0n);
            if (raised > BigInt(value.targetAtomic) || !atomic(value.raisedAtomic) || raised !== BigInt(value.raisedAtomic)) fail();
            return amounts;
        };
        const launchTerms = value => [value.targetAtomic, value.saleTokensAtomic, value.totalTokensAtomic, value.deadlineHeight, value.allowUserLiquidity ? '1' : '0'].join('|');
        let expectedShares = null, launchEncoding = null;

        if (draft.operation === 'pool') {
            if (!['fees', 'loyalty'].includes(draft.rewardMode || 'fees') || (draft.rewardMode || 'fees') !== (expected.rewardMode || 'fees')) fail();
            const policy = draft.loyaltyPolicy, wantedPolicy = expected.loyaltyPolicy;
            if (draft.rewardMode === 'loyalty') {
                if (!policy || !wantedPolicy || !Number.isInteger(policy.feeBasisPoints) || policy.feeBasisPoints < 1 || policy.feeBasisPoints > 10000 ||
                    !atomic(policy.minimumReserveAtomic) || BigInt(policy.minimumReserveAtomic) <= 0n || !['forfeit', 'locked'].includes(policy.earlyExit) ||
                    policy.feeBasisPoints !== wantedPolicy.feeBasisPoints || policy.minimumReserveAtomic !== wantedPolicy.minimumReserveAtomic || policy.earlyExit !== wantedPolicy.earlyExit ||
                    policy.status !== 'configured') fail();
            } else if (policy) fail();
            if (Boolean(draft.allowUserLiquidity) !== Boolean(expected.allowUserLiquidity)) fail();
            if (expected.allowUserLiquidity && !expected.launch) expectedShares = new Map([[draft.providerAddress, a]]);
        } else if (expected.participation) expectedShares = participation(expected.participation);
        if (expected.launch && draft.operation === 'pool' || ['launch-contribute', 'launch-refund'].includes(draft.operation)) {
            const wanted = expected.launch, state = draft.launch, amounts = launchState(wanted), actual = launchState(state);
            if (launchTerms(state) !== launchTerms(wanted)) fail();
            const initial = draft.operation === 'pool', refund = draft.operation === 'launch-refund';
            let kind;
            if (initial) {
                if (draft.inputs.length !== draft.userInputCount || draft.changeAddress !== draft.providerAddress || amounts.size !== 1 || amounts.get(draft.providerAddress) !== b ||
                    wanted.totalTokensAtomic !== draft.reserveAAtomic || BigInt(wanted.raisedAtomic) >= BigInt(wanted.targetAtomic) || draft.assetA.id === 'povix') fail();
                kind = 13; addSpend(draft.assetA.id, a); addSpend(draft.assetB.id, b);
            } else {
                if (draft.poolId !== expected.poolId || draft.reserveTransactionId !== expected.reserveTransactionId || draft.providerAddress !== expected.providerAddress ||
                    draft.inputs.length !== draft.userInputCount + 2 || wanted.totalTokensAtomic !== draft.reserveAAtomic || wanted.raisedAtomic !== draft.reserveBAtomic) fail();
                for (let side = 0; side < 2; side++) {
                    const input = draft.inputs[draft.userInputCount + side];
                    if (input.transactionId !== draft.reserveTransactionId || input.outputIndex !== side || input.address !== draft.providerAddress ||
                        input.assetId !== (side === 0 ? draft.assetA.id : draft.assetB.id) || input.amountAtomic !== (side === 0 ? draft.reserveAAtomic : draft.reserveBAtomic)) fail();
                }
                if (!refund) {
                    if (!atomic(draft.amountAtomic) || draft.amountAtomic !== expected.amountAtomic || BigInt(draft.amountAtomic) <= 0n) fail();
                    amounts.set(draft.changeAddress, (amounts.get(draft.changeAddress) || 0n) + BigInt(draft.amountAtomic));
                    addSpend(draft.assetB.id, draft.amountAtomic);
                } else if (draft.amountAtomic !== '0') fail();
                const raised = [...amounts.values()].reduce((sum, amount) => sum + amount, 0n);
                kind = refund ? 16 : raised === BigInt(state.targetAtomic) ? 15 : 14;
            }
            if (canonical(amounts) !== canonical(actual) || draft.transactionKind !== kind) fail();
            if (kind === 16) {
                wantedOutputs.push(output(draft.assetA.id, state.totalTokensAtomic, draft.providerAddress));
                for (const [address, amount] of [...amounts].sort(([x], [y]) => x.localeCompare(y))) wantedOutputs.push(output(draft.assetB.id, amount, address));
            } else if (kind === 15) {
                const payouts = [...amounts].sort(([x], [y]) => x.localeCompare(y)).map(([address, amount]) => output(draft.assetA.id, BigInt(state.saleTokensAtomic) * amount / BigInt(state.targetAtomic), address));
                const liquidity = BigInt(state.totalTokensAtomic) - payouts.reduce((sum, item) => sum + BigInt(item.amountAtomic), 0n);
                wantedOutputs.push(output(draft.assetA.id, liquidity, draft.providerAddress), output(draft.assetB.id, state.targetAtomic, draft.providerAddress), ...payouts);
                if (state.allowUserLiquidity) expectedShares = new Map([[draft.providerAddress, liquidity]]);
            } else wantedOutputs.push(output(draft.assetA.id, state.totalTokensAtomic, draft.providerAddress), output(draft.assetB.id, state.raisedAtomic, draft.providerAddress));
            launchEncoding = launchTerms(state) + '|' + canonical(actual);
        } else if (['add', 'remove'].includes(draft.operation)) {
            if (!expectedShares || draft.sharesAtomic !== expected.sharesAtomic || !atomic(draft.sharesAtomic) || BigInt(draft.sharesAtomic) <= 0n ||
                draft.inputs.length !== draft.userInputCount + 2 || draft.poolId !== expected.poolId || draft.reserveTransactionId !== expected.reserveTransactionId ||
                draft.providerAddress !== expected.providerAddress) fail();
            for (let side = 0; side < 2; side++) {
                const input = draft.inputs[draft.userInputCount + side];
                if (input.transactionId !== expected.reserveTransactionId || input.outputIndex !== side || input.address !== draft.providerAddress ||
                    input.assetId !== (side === 0 ? draft.assetA.id : draft.assetB.id) || input.amountAtomic !== (side === 0 ? draft.reserveAAtomic : draft.reserveBAtomic)) fail();
            }
            const add = draft.operation === 'add', shares = BigInt(draft.sharesAtomic), total = BigInt(expected.participation.totalSharesAtomic);
            const owned = expectedShares.get(draft.changeAddress) || 0n;
            if (!add && (shares > owned || shares >= total || !(expectedShares.lockedShares > 0n) && draft.changeAddress === draft.providerAddress && shares === owned)) fail();
            const amountA = (a * shares + (add ? total - 1n : 0n)) / total, amountB = (b * shares + (add ? total - 1n : 0n)) / total;
            if (amountA <= 0n || amountB <= 0n) fail();
            const next = add ? owned + shares : owned - shares;
            if (next) expectedShares.set(draft.changeAddress, next); else expectedShares.delete(draft.changeAddress);
            wantedOutputs.push(output(draft.assetA.id, add ? a + amountA : a - amountA, draft.providerAddress),
                output(draft.assetB.id, add ? b + amountB : b - amountB, draft.providerAddress));
            if (add) { addSpend(draft.assetA.id, amountA); addSpend(draft.assetB.id, amountB); }
            else wantedOutputs.push(output(draft.assetA.id, amountA, draft.changeAddress), output(draft.assetB.id, amountB, draft.changeAddress));
        } else if (draft.operation === 'pool') {
            if (draft.userInputCount !== draft.inputs.length || draft.providerAddress !== draft.changeAddress || !wallet.keys.has(draft.changeAddress)) fail();
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
                if (draft.changeAddress !== draft.providerAddress || !wallet.keys.has(draft.providerAddress) ||
                    draft.amountAtomic !== '0' || draft.minimumReceiveAtomic !== '0' || draft.receiveAtomic !== '0' || draft.inputSide !== 0) fail();
                wantedOutputs.push(output(draft.assetA.id, a, draft.providerAddress), output(draft.assetB.id, b, draft.providerAddress));
            } else {
                const amount = BigInt(draft.amountAtomic), quote = quotePoolSwap(draft.inputSide === 0 ? a : b, draft.inputSide === 0 ? b : a, amount,
                    draft.assetA, draft.assetB, draft.inputSide, burnRules);
                const receive = BigInt(quote.receiveAtomic), gross = BigInt(quote.grossReceiveAtomic);
                if (draft.receiveAtomic !== quote.receiveAtomic || receive < BigInt(draft.minimumReceiveAtomic) ||
                    String(draft.inputBurnAtomic || '0') !== quote.inputBurnAtomic || String(draft.outputBurnAtomic || '0') !== quote.outputBurnAtomic) fail();
                wantedOutputs.push(output(draft.assetA.id, draft.inputSide === 0 ? a + amount : a - gross, draft.providerAddress),
                    output(draft.assetB.id, draft.inputSide === 1 ? b + amount : b - gross, draft.providerAddress),
                    output(draft.inputSide === 0 ? draft.assetB.id : draft.assetA.id, receive, draft.changeAddress));
                addSpend(draft.inputSide === 0 ? draft.assetA.id : draft.assetB.id, quote.inputDebitAtomic);
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
        const kind = launchEncoding ? draft.transactionKind : { pool: 7, swap: 8, lock: 7, close: 9, add: 10, remove: 11 }[draft.operation];
        if (!launchEncoding && draft.launch) fail();
        if (draft.transactionKind !== kind) fail();
        payload += '|fee:' + draft.feeAtomic + '|kind:' + kind + '|validator:|reward:|owned:|assets-v1|';
        draft.outputs.forEach(item => { payload += field(item.assetId === 'povix' ? null : item.assetId); });
        if (draft.operation === 'swap') {
            payload += '|pool-swap-v1|' + draft.inputSide + '|' + draft.amountAtomic;
            if (BigInt(draft.inputBurnAtomic || '0') > 0n || BigInt(draft.outputBurnAtomic || '0') > 0n) payload += '|pool-burn-v1|' + draft.inputBurnAtomic + '|' + draft.outputBurnAtomic;
        }
        if (draft.operation === 'close') { if (expectedShares?.lockedShares > 0n) fail(); expectedShares = null; }
        if (Boolean(draft.allowUserLiquidity) !== Boolean(expectedShares)) fail();
        if (expectedShares) {
            if (canonical(participation(draft.participation)) !== canonical(expectedShares)) fail();
            payload += '|pool-participation-v1|' + canonical(expectedShares);
        } else if (draft.participation) fail();
        if (['add', 'remove'].includes(draft.operation)) payload += '|pool-change-v1|' + draft.changeAddress + '|' + draft.sharesAtomic;
        if (burnRules) payload += '|pool-burn-enabled-v1|' + burnRules.basisPoints + ':' + burnRules.fixedAAtomic + ':' + burnRules.fixedBAtomic;
        if (launchEncoding) payload += '|pool-launch-v1|' + launchEncoding;
        if (toBase64(encoder.encode(payload)) !== draft.signingPayload) fail();
        return true;
    }
    function transferAuthorization(draft) {
        return toBase64(encoder.encode('povix-dex-token-transfer-authorize-v1|' + draft.networkId + '|' + draft.draftId + '|' + draft.tokenId + '|' + draft.signingPayload));
    }
    async function verifyTransferDraft(draft, expected, networkId, changeAddress) {
        const wallet = requireWallet();
        const atomic = value => typeof value === 'string' && /^[0-9]+$/.test(value) && BigInt(value) <= 9223372036854775807n;
        if (draft.networkId !== networkId || draft.consensusVersion !== 22 || draft.tokenId !== expected.TokenId || !/^[0-9a-f]{64}$/.test(draft.tokenId) ||
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
        if (draft.automaticBurnEnabled) {
            if (String(draft.automaticBurnAtomic) !== String(expected.AutomaticBurnAtomic) || !atomic(draft.automaticBurnAtomic)) throw new Error('A queima automática difere da revisão.');
            payload += '|token-transfer-burn-v1|' + draft.tokenId + ':' + draft.amountAtomic + ':' + draft.automaticBurnAtomic;
        } else if (BigInt(expected.AutomaticBurnAtomic || '0') !== 0n) throw new Error('A queima automática não foi incluída.');
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
            password = password || '';
            automaticPassword = false;
            active = null;
            staged = null;
            if (!file || file.size > 9000000) throw new Error('Escolha um arquivo de carteiras de até 9 MB.');
            const bytes = new Uint8Array(await file.arrayBuffer());
            // DPAPI files are binary. They are opened only by the user's local Windows helper.
            // Portable encrypted backups are parsed and decrypted entirely in this browser.
            let record;
            if (bytes.length >= 20 && bytes[0] === 1 && bytes[1] === 0 && bytes[2] === 0 && bytes[3] === 0 &&
                toBase64(bytes.slice(4, 20)) === '0Iyd3wEV0RGMegDAT8KX6w==') {
                if (!password) {
                    password = toBase64(crypto.getRandomValues(new Uint8Array(32)));
                    automaticPassword = true;
                }
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
            const choices = await read(record, password);
            sessionPassword = password;
            return choices;
        },
        async readSaved(password) {
            const saved = global.localStorage.getItem(STORAGE);
            if (!saved) throw new Error('Escolha o arquivo de carteiras primeiro.');
            const choices = await read(JSON.parse(saved), password);
            sessionPassword = password; automaticPassword = false;
            return choices;
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
        lock() { active = null; staged = null; sessionPassword = null;
            try { if (global.sessionStorage) global.sessionStorage.removeItem(SESSION); } catch (_) { }
        },
        info() { return active ? { name: active.name, addressCount: active.keys.size } : null; },
        async restoreSession() {
            if (!global.sessionStorage) return false;
            try {
                const saved = global.sessionStorage.getItem(SESSION);
                if (!saved) return false;
                const session = JSON.parse(saved);
                await read(session.record, session.password);
                sessionPassword = session.password; automaticPassword = !!session.automaticPassword;
                await selectWallet(session.selected);
                return true;
            } catch (_) { this.lock(); return false; }
        },
        isUnlocked() { return Boolean(active); },
        publicKeys() { return Array.from(requireWallet().keys.values(), item => toBase64(encoder.encode(item.publicXml))); },
        addresses() { return Array.from(requireWallet().keys.keys()); },
        createAddress, verifyDraft, verifyManagementDraft, verifyTransferDraft, verifyExchangeDraft, quoteExchange, quotePoolSwap, parseSupply, formatAtomic,
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
        async backup() {
            const wallet = requireWallet();
            let record = wallet.record;
            if (automaticPassword) {
                const password = global.prompt('Defina uma senha de pelo menos 10 caracteres para proteger o arquivo de backup. Ela não é necessária para navegar no DEX.');
                if (!password) throw new Error('Backup cancelado.');
                const salt = crypto.getRandomValues(new Uint8Array(16)), iv = crypto.getRandomValues(new Uint8Array(16));
                const protection = await derive(password, salt);
                const clear = encoder.encode(JSON.stringify(wallet.legacy ? wallet.wallets[0] : { Wallets: wallet.wallets }));
                let cipher;
                try { cipher = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-CBC', iv }, protection.aes, clear)); }
                finally { clear.fill(0); }
                record = { format: FORMAT, iterations: ITERATIONS, salt: toBase64(salt), iv: toBase64(iv), data: toBase64(cipher),
                    hmac: toBase64(await crypto.subtle.sign('HMAC', protection.hmac, concat(salt, iv, cipher))) };
            }
            const url = URL.createObjectURL(new Blob([JSON.stringify(record)], { type: 'application/json' }));
            const anchor = document.createElement('a');
            anchor.href = url;
            anchor.download = 'povix-dex-carteira-atualizada.povixwallet';
            anchor.click();
            setTimeout(() => URL.revokeObjectURL(url), 10000);
        },
        async signManagementAdministrators(draft) {
            const current = requireWallet(), payload = fromBase64(draft.signingPayload), signatures = [];
            for (const address of draft.administratorAddresses) {
                const item = current.keys.get(address);
                if (!item) throw new Error('Chave administrativa ausente.');
                signatures.push(toBase64(await crypto.subtle.sign(algorithm.name, item.key, payload)));
            }
            return signatures;
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
