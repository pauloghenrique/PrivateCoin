(function (global) {
    'use strict';
    const FORMAT = 'povix-dex-wallet-v1';
    const STORAGE = 'povix.dex.encrypted-wallet.v1';
    const ITERATIONS = 210000;
    const encoder = new TextEncoder();
    const decoder = new TextDecoder();
    const algorithm = { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' };
    let active = null;

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
    async function load(record, password) {
        requireCrypto();
        if (!record || record.format !== FORMAT || record.iterations !== ITERATIONS || typeof record.data !== 'string' || record.data.length > 6000000)
            throw new Error('Use o arquivo .povixwallet exportado para o DEX.');
        const salt = fromBase64(record.salt), iv = fromBase64(record.iv), cipher = fromBase64(record.data);
        if (salt.length !== 16 || iv.length !== 16 || cipher.length === 0) throw new Error('Backup inválido.');
        const protection = await derive(password, salt);
        if (!await crypto.subtle.verify('HMAC', protection.hmac, fromBase64(record.hmac), concat(salt, iv, cipher)))
            throw new Error('Senha incorreta ou backup alterado.');
        const clear = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-CBC', iv }, protection.aes, cipher));
        let data;
        try { data = JSON.parse(decoder.decode(clear)); }
        finally { clear.fill(0); }
        if (!data || !Array.isArray(data.PrivateKeys) || data.PrivateKeys.length < 1 || data.PrivateKeys.length > 998)
            throw new Error('A carteira deve conter entre 1 e 998 chaves.');
        const keys = new Map();
        for (const xml of data.PrivateKeys) {
            const item = await importKey(xml);
            if (keys.has(item.address)) throw new Error('O backup contém chaves duplicadas.');
            keys.set(item.address, item);
        }
        active = { name: typeof data.Name === 'string' ? data.Name.slice(0, 64) : 'Minha carteira', keys, salt, protection, record };
        try { await save(); }
        catch (error) { active = null; throw error; }
        return { name: active.name, addressCount: keys.size };
    }
    async function save() {
        const wallet = requireWallet();
        const iv = crypto.getRandomValues(new Uint8Array(16));
        const clear = encoder.encode(JSON.stringify({ Name: wallet.name, PrivateKeys: Array.from(wallet.keys.values(), item => item.xml) }));
        let cipher;
        try { cipher = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-CBC', iv }, wallet.protection.aes, clear)); }
        finally { clear.fill(0); }
        const record = { format: FORMAT, iterations: ITERATIONS, salt: toBase64(wallet.salt), iv: toBase64(iv), data: toBase64(cipher),
            hmac: toBase64(await crypto.subtle.sign('HMAC', wallet.protection.hmac, concat(wallet.salt, iv, cipher))) };
        // Abort before preparation/signing if durable local storage is unavailable.
        try { global.localStorage.setItem(STORAGE, JSON.stringify(record)); }
        catch (_) { throw new Error('Não foi possível salvar a carteira cifrada neste navegador. Libere espaço ou permita o armazenamento local.'); }
        wallet.record = record;
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
        if (draft.networkId !== networkId || draft.consensusVersion !== 3 || draft.name !== expected.Name || draft.symbol !== expected.Symbol ||
            draft.decimals !== Number(expected.Decimals) || draft.supplyAtomic !== parseSupply(expected.Supply, Number(expected.Decimals)) ||
            draft.destinationAddress !== expected.DestinationAddress || (draft.changeAddress && draft.changeAddress !== changeAddress) ||
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
        async importFile(file, password) {
            if (!file || file.size > 6000000) throw new Error('Escolha um backup de carteira de até 6 MB.');
            return load(JSON.parse(await file.text()), password);
        },
        async unlock(password) {
            const saved = global.localStorage.getItem(STORAGE);
            if (!saved) throw new Error('Importe um backup de carteira primeiro.');
            return load(JSON.parse(saved), password);
        },
        hasSavedWallet() { try { return Boolean(global.localStorage.getItem(STORAGE)); } catch (_) { return false; } },
        lock() { active = null; },
        isUnlocked() { return Boolean(active); },
        publicKeys() { return Array.from(requireWallet().keys.values(), item => toBase64(encoder.encode(item.publicXml))); },
        addresses() { return Array.from(requireWallet().keys.keys()); },
        createAddress, verifyDraft, parseSupply, formatAtomic,
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
    if (global.addEventListener) global.addEventListener('pagehide', () => { active = null; });
})(typeof window === 'undefined' ? globalThis : window);
