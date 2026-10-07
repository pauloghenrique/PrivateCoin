(function (root, factory) {
    'use strict';
    const api = factory(root.crypto);
    if (typeof module === 'object' && module.exports) module.exports = api;
    else root.DEXPovixWallet = api;
}(typeof globalThis !== 'undefined' ? globalThis : window, function (crypto) {
    'use strict';
    const rsa = { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' };
    const encoder = new TextEncoder();
    function requireCrypto() {
        if (!crypto || !crypto.subtle) throw new Error('A carteira precisa de HTTPS ou localhost e Web Crypto.');
    }
    function base64(value) {
        const data = new Uint8Array(value), chunks = [];
        for (let i = 0; i < data.length; i += 8192)
            chunks.push(String.fromCharCode.apply(null, data.subarray(i, i + 8192)));
        return btoa(chunks.join(''));
    }
    function bytes(value) {
        if (typeof value !== 'string') throw new Error('Backup inválido.');
        return Uint8Array.from(atob(value), char => char.charCodeAt(0));
    }
    function xml(jwk) {
        const convert = value => {
            const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
            return normalized + '='.repeat((4 - normalized.length % 4) % 4);
        };
        return '<RSAKeyValue><Modulus>' + convert(jwk.n) + '</Modulus><Exponent>' +
            convert(jwk.e) + '</Exponent></RSAKeyValue>';
    }
    async function hash(value) {
        requireCrypto();
        return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(value))),
            byte => byte.toString(16).padStart(2, '0')).join('');
    }
    async function keyFromJwk(jwk) {
        requireCrypto();
        const key = await crypto.subtle.importKey('jwk', jwk, rsa, true, ['sign']);
        if (key.algorithm.modulusLength !== 2048) throw new Error('O backup exige chaves RSA de 2048 bits.');
        const normalized = await crypto.subtle.exportKey('jwk', key);
        const publicKey = xml(normalized);
        // Check that imported private parameters match their public key before using funds.
        const signature = await crypto.subtle.sign(rsa, key, encoder.encode('DEXPovix backup check'));
        const publicCryptoKey = await crypto.subtle.importKey('jwk',
            { kty: 'RSA', n: normalized.n, e: normalized.e, alg: 'RS256', ext: true }, rsa, false, ['verify']);
        if (!await crypto.subtle.verify(rsa, publicCryptoKey, signature, encoder.encode('DEXPovix backup check')))
            throw new Error('O backup contém uma chave inconsistente.');
        return { jwk: normalized, publicKey: publicKey, address: await hash(publicKey), key: key };
    }
    async function newKey() {
        requireCrypto();
        const pair = await crypto.subtle.generateKey({ name: rsa.name, modulusLength: 2048,
            publicExponent: new Uint8Array([1, 0, 1]), hash: rsa.hash }, true, ['sign', 'verify']);
        return keyFromJwk(await crypto.subtle.exportKey('jwk', pair.privateKey));
    }
    async function derive(password, salt, usage) {
        requireCrypto();
        if (typeof password !== 'string' || password.length < 12)
            throw new Error('Use uma senha de backup com pelo menos 12 caracteres.');
        const material = await crypto.subtle.importKey('raw', encoder.encode(password), 'PBKDF2', false, ['deriveKey']);
        return crypto.subtle.deriveKey({ name: 'PBKDF2', salt: salt, iterations: 310000, hash: 'SHA-256' },
            material, { name: 'AES-GCM', length: 256 }, false, [usage]);
    }
    async function encrypt(wallet, password) {
        if (!wallet || !Array.isArray(wallet.keys) || wallet.keys.length < 1 || wallet.keys.length > 100)
            throw new Error('Carteira inválida.');
        const salt = crypto.getRandomValues(new Uint8Array(16)), iv = crypto.getRandomValues(new Uint8Array(12));
        const key = await derive(password, salt, 'encrypt');
        const data = encoder.encode(JSON.stringify({ keys: wallet.keys.map(item => item.jwk),
            receiveAddress: wallet.receiveAddress }));
        const encrypted = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: iv }, key, data);
        return { format: 'DEXPovix-wallet', version: 1, salt: base64(salt), iv: base64(iv), data: base64(encrypted) };
    }
    async function decrypt(backup, password) {
        if (!backup || backup.format !== 'DEXPovix-wallet' || backup.version !== 1 ||
            typeof backup.data !== 'string' || backup.data.length > 1000000)
            throw new Error('Backup DEXPovix inválido.');
        const salt = bytes(backup.salt), iv = bytes(backup.iv);
        if (salt.length !== 16 || iv.length !== 12) throw new Error('Backup inválido.');
        const key = await derive(password, salt, 'decrypt');
        let value;
        try {
            value = JSON.parse(new TextDecoder().decode(await crypto.subtle.decrypt(
                { name: 'AES-GCM', iv: iv }, key, bytes(backup.data))));
        } catch (_) { throw new Error('Senha incorreta ou backup corrompido.'); }
        if (!value || !Array.isArray(value.keys) || value.keys.length < 1 || value.keys.length > 100)
            throw new Error('Backup inválido.');
        const keys = [];
        for (const jwk of value.keys) keys.push(await keyFromJwk(jwk));
        if (new Set(keys.map(item => item.address)).size !== keys.length ||
            !keys.some(item => item.address === value.receiveAddress)) throw new Error('Backup inválido.');
        return { keys: keys, receiveAddress: value.receiveAddress };
    }
    async function sign(wallet, payload, addresses) {
        if (typeof payload !== 'string' || payload.length > 200000 || !Array.isArray(addresses) ||
            addresses.length < 1 || addresses.length > 100) throw new Error('Preparação inválida.');
        return Promise.all(addresses.map(async address => {
            const item = wallet.keys.find(key => key.address === address);
            if (!item) throw new Error('A carteira não possui a chave de uma entrada.');
            return base64(await crypto.subtle.sign(rsa, item.key, encoder.encode(payload)));
        }));
    }
    return { newKey: newKey, encrypt: encrypt, decrypt: decrypt, sign: sign, hash: hash };
}));
