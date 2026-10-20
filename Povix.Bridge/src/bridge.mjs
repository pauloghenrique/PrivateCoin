import { Contract, Wallet, JsonRpcProvider, getAddress, sha256, toUtf8Bytes, verifyTypedData, keccak256, FetchRequest } from 'ethers';
import { readFileSync, statSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { DatabaseSync } from 'node:sqlite';
import { root } from './paths.mjs';
// Native ticks/int64 fields are signed. Preserve their exact JSON digits through JavaScript.
export const parseJson = text => JSON.parse(text, (key, value, context) =>
  typeof value === 'number' && !Number.isSafeInteger(value) ? JSON.rawJSON(context.source) : value);
export const protocolToken = 'c834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d';
export const representationId = (chainId, asset) => 'b9b0' + sha256(toUtf8Bytes('povix-bnb-token-v1|' + chainId + '|' + asset.toLowerCase())).slice(6);
export const nativeSymbol = symbol => 'p' + (String(symbol).toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 9) || 'TOKEN');
export const types = {
  Release: [{ name: 'burnId', type: 'bytes32' }, { name: 'recipient', type: 'address' }, { name: 'nativeAmount', type: 'uint256' }, { name: 'deadline', type: 'uint256' }],
  Pause: [{ name: 'paused', type: 'bool' }, { name: 'nonce', type: 'uint256' }, { name: 'deadline', type: 'uint256' }]
};
export const domain = config => ({ name: config.mode === "production" ? "POVIX Asset Bridge" : "POVIX USDT Bridge", version: config.mode === "production" ? "2" : "1", chainId: config.chainId, verifyingContract: config.bridge });
export const networkHash = config => sha256(toUtf8Bytes(config.nativeNetwork));
export const productionTypes = { ...types, Release: [...types.Release, { name: 'nonce', type: 'uint256' }] };
export const signingTypes = (c, kind) => ({ [kind]: (c.mode === 'production' ? productionTypes : types)[kind] });
export function validateConfig(c) {
  if (c.chainId === 56 && c.mode !== 'production') throw Error('Mainnet requires explicit production mode.');
  if (![56, 97, 31337].includes(c.chainId) || typeof c.nativeNetwork !== 'string' || !c.nativeNetwork.startsWith('povix-mainnet-rules-v1-') ||
      !Number.isInteger(c.nativeConfirmations) || c.nativeConfirmations < 12 || !Number.isInteger(c.bscConfirmations) || c.bscConfirmations < 15 ||
      (c.nativeToken !== protocolToken && (c.nativeToken !== representationId(c.chainId, c.asset) || !Number.isInteger(c.nativeDecimals) || c.nativeDecimals < 0 || c.nativeDecimals > 8 || !/^p[A-Z0-9]{1,9}$/.test(c.nativeSymbol))) || !Array.isArray(c.nativeAdministrators) || c.nativeAdministrators.length < 3 ||
      c.nativeAdministrators.some(a => !/^[a-f0-9]{64}$/.test(a)) || new Set(c.nativeAdministrators).size !== c.nativeAdministrators.length ||
      !Number.isInteger(c.nativeThreshold) || c.nativeThreshold <= c.nativeAdministrators.length / 2 || c.nativeThreshold > c.nativeAdministrators.length ||
      !Array.isArray(c.operators) || c.operators.length < 3 || c.operators.length > 10 ||
      !Number.isInteger(c.threshold) || c.threshold <= c.operators.length / 2 || c.threshold > c.operators.length)
    throw Error('Invalid bridge config; require 12 native / 15 BSC confirmations and majority operators.');
  const members = c.operators.map(getAddress);
  if (new Set(members).size !== members.length || members.includes('0x' + '0'.repeat(40))) throw Error('Invalid operator addresses.');
  if (!/^[1-9][0-9]*$/.test(c.reserveCap) || !/^[1-9][0-9]*$/.test(c.dailyReleaseCap) || BigInt(c.dailyReleaseCap) > BigInt(c.reserveCap) || BigInt(c.reserveCap) > 9223372036854775807n) throw Error('Invalid caps (native 8-decimal atomic units).');
  for (const address of [c.asset, ...(c.bridge === null ? [] : [c.bridge])]) {
    if (!/^0x[a-f0-9]{40}$/.test(address) || /^0x0{40}$/.test(address)) throw Error('Use lowercase nonzero BSC addresses.');
  }
  const url = new URL(c.rpcUrl);
  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname))) throw Error('Use HTTPS RPC or loopback.');
  if (typeof c.nativeSnapshot !== 'string') throw Error('Native snapshot path required.');
  if (c.mode === 'production') {
    if (c.operators.length < 5 || c.threshold < 3 || c.nativeAdministrators.length < 5 || c.nativeThreshold < 3 ||
        c.nativeConfirmations < (c.chainId === 31337 ? 12 : 120) || c.bscConfirmations < (c.chainId === 31337 ? 15 : 64) || !Array.isArray(c.rpcUrls) || c.rpcUrls.length < 2 ||
        new Set(c.rpcUrls).size !== c.rpcUrls.length || !/^0x[a-f0-9]{64}$/.test(c.assetCodeHash || '') ||
        c.bridge !== null && !/^0x[a-f0-9]{64}$/.test(c.vaultCodeHash || '') || c.factory && !/^0x[a-f0-9]{64}$/.test(c.factoryCodeHash || '')) throw Error('Production requires five operators, majority quorum, 120 native / 64 BNB confirmations, independent RPCs and pinned code hashes.');
    const urls = c.rpcUrls.map(value => new URL(value));
    if (!c.rpcUrls.includes(c.rpcUrl) || urls.some(url => url.protocol !== 'https:' && !(c.chainId === 31337 && url.protocol === 'http:' && ['127.0.0.1','localhost'].includes(url.hostname))) ||
        c.chainId === 56 && new Set(urls.map(url => url.hostname)).size < 2) throw Error('Use two independent HTTPS RPC hosts.');
  }
  return c;
}
export function journal(filename, config) {
  const db = new DatabaseSync(filename);
  db.exec('PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS scope (id INTEGER PRIMARY KEY, value TEXT); CREATE TABLE IF NOT EXISTS decisions (id TEXT PRIMARY KEY, payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS registrations (id TEXT PRIMARY KEY, tx TEXT NOT NULL); CREATE TABLE IF NOT EXISTS mints (id TEXT PRIMARY KEY, tx TEXT NOT NULL UNIQUE);');
  if (config) {
    const value = JSON.stringify([config.chainId, config.bridge, config.nativeNetwork, config.nativeToken]);
    db.prepare('INSERT OR IGNORE INTO scope VALUES (1, ?)').run(value);
    if (db.prepare('SELECT value FROM scope WHERE id=1').get().value !== value) { db.close(); throw Error('Journal belongs to another bridge.'); }
  }
  return db;
}
export function recordDecision(db, id, value) {
  const payload = JSON.stringify(value), old = db.prepare('SELECT payload FROM decisions WHERE id=?').get(id);
  if (old && old.payload !== payload) throw Error('Conflicting authorization for an existing burn.');
  db.prepare('INSERT OR IGNORE INTO decisions VALUES (?, ?)').run(id, payload);
}
export function rpcProvider(url) {
  const request = new FetchRequest(url); request.timeout = 15000;
  return new JsonRpcProvider(request, undefined, { cacheTimeout: -1 });
}
export async function connect(config, signer = null, suppliedProvider = null) {
  validateConfig(config);
  if (!config.bridge) throw Error('Deploy the configured vault and pin its address first.');
  const provider = suppliedProvider || rpcProvider(config.rpcUrl);
  try {
    if ((await provider.getNetwork()).chainId !== BigInt(config.chainId)) throw Error('Wrong RPC chain.');
    const artifact = JSON.parse(readFileSync(path.join(root, config.mode === 'production' ? 'build/ProductionVault.json' : 'build/UsdtVault.json'), 'utf8'));
    const contract = new Contract(config.bridge, artifact.abi, signer ? signer.connect(provider) : provider);
    if (await provider.getCode(config.bridge) === '0x' || await contract.nativeNetwork() !== networkHash(config) ||
        await contract.nativeToken() !== '0x' + config.nativeToken || getAddress(await contract.asset()) !== getAddress(config.asset) ||
        await contract.threshold() !== BigInt(config.threshold) || await contract.reserveCap() !== BigInt(config.reserveCap) ||
        await contract.dailyReleaseCap() !== BigInt(config.dailyReleaseCap)) throw Error('Vault does not match pinned configuration.');
    for (let i = 0; i < config.operators.length; i++) if (getAddress(await contract.operators(i)) !== getAddress(config.operators[i])) throw Error('Unexpected operators.');
    if (config.mode === 'production') {
      if (keccak256(await provider.getCode(config.asset)) !== config.assetCodeHash || keccak256(await provider.getCode(config.bridge)) !== config.vaultCodeHash ||
          await contract.bridgeVersion() !== 2n || await contract.releaseDelay() !== 86400n) throw Error('Production bytecode/version/delay mismatch.');
    }
    if (config.mode === 'production' && config.factory && keccak256(await provider.getCode(config.factory)) !== config.factoryCodeHash) throw Error('Production factory bytecode mismatch.');
    const token = new Contract(config.asset, ['function decimals() view returns(uint8)'], provider);
    const decimals = Number(await token.decimals()), expected = config.nativeToken === protocolToken ? 8 : config.nativeDecimals;
    if (decimals > 18 || expected !== Math.min(8, decimals) || await contract.scale() !== 10n ** BigInt(decimals - expected)) throw Error('Invalid asset precision.');
    return { provider, contract };
  } catch (error) { if (!suppliedProvider) await provider.destroy(); throw error; }
}
export function native(config, command, request, keysFile) {
  if (Date.now() - statSync(config.nativeSnapshot).mtimeMs > 300000) throw Error('Refresh the independent native snapshot (older than five minutes).');
  const directory = mkdtempSync(path.join(tmpdir(), 'povix-usdt-'));
  try {
    const configFile = path.join(directory, 'config.json'), requestFile = path.join(directory, 'request.json');
    writeFileSync(configFile, JSON.stringify(config), { mode: 0o600 }); writeFileSync(requestFile, JSON.stringify(request), { mode: 0o600 });
    const args = [path.join(root, 'build/native/BridgeNative.exe'), command, config.nativeSnapshot, configFile, requestFile];
    if (keysFile) args.push(keysFile);
    return parseJson(execFileSync(process.env.POVIX_MONO || 'mono', args, { encoding: 'utf8', timeout: 240000, maxBuffer: 16 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] }));
  } finally { rmSync(directory, { recursive: true, force: true }); }
}
export async function loadWallet(filename) {
  if (process.platform !== 'win32' && (statSync(filename).mode & 0o077) !== 0) throw Error('Operator keystore must be private (chmod 600).');
  if (!process.env.BRIDGE_KEYSTORE_PASSWORD) throw Error('Set BRIDGE_KEYSTORE_PASSWORD locally to unlock the encrypted EVM keystore.');
  return Wallet.fromEncryptedJson(readFileSync(filename, 'utf8'), process.env.BRIDGE_KEYSTORE_PASSWORD);
}
export function sortSignatures(config, kind, value, signatures) {
  const recovered = signatures.map(signature => ({ signature, address: verifyTypedData(domain(config), signingTypes(config, kind), value, signature) }));
  recovered.sort((a, b) => BigInt(a.address) < BigInt(b.address) ? -1 : 1);
  const allowed = new Set(config.operators.map(getAddress));
  if (recovered.length < config.threshold || new Set(recovered.map(s => s.address)).size !== recovered.length || recovered.some(s => !allowed.has(s.address))) throw Error('Invalid operator quorum.');
  return recovered.map(s => s.signature);
}
export async function verifyDeposit(provider, contract, config, transactionHash, logIndex) {
  if (!Number.isInteger(logIndex) || logIndex < 0 || !/^0x[a-fA-F0-9]{64}$/.test(transactionHash)) throw Error('Invalid deposit reference.');
  if ((await provider.getNetwork()).chainId !== BigInt(config.chainId)) throw Error('Wrong deposit chain.');
  const receipt = await provider.getTransactionReceipt(transactionHash);
  if (!receipt || receipt.status !== 1) throw Error('Deposit is not confirmed successfully.');
  const latest = await provider.getBlockNumber(), block = await provider.getBlock(receipt.blockNumber);
  if (!block || block.hash !== receipt.blockHash || latest - receipt.blockNumber + 1 < config.bscConfirmations) throw Error('Deposit is orphaned or lacks confirmations.');
  const log = receipt.logs.find(l => l.index === logIndex && getAddress(l.address) === getAddress(config.bridge));
  if (!log) throw Error('Deposit log is not from the pinned vault.');
  const decoded = contract.interface.parseLog(log);
  if (decoded?.name !== 'Deposited' || decoded.args.nativeAmount <= 0n || decoded.args.nativeAmount > 9223372036854775807n || decoded.args.assetAmount !== decoded.args.nativeAmount * await contract.scale()) throw Error('Invalid deposit amount.');
  return { depositId: decoded.args.depositId, recipient: decoded.args.nativeRecipient.slice(2), amount: decoded.args.nativeAmount.toString(), blockHash: block.hash };
}
export async function prepareMint(db, provider, contract, config, reference, keysFile, fee, nativeCall = native) {
  if (config.mode === 'production' || config.chainId === 56) throw Error('Use independent production proposals/signatures instead of consolidated keys.');
  if (!/^[a-f0-9]{64}$/.test(config.nativeToken || '')) throw Error('Pin a native token before preparing issuance.');
  const deposit = await verifyDeposit(provider, contract, config, reference.transactionHash, reference.logIndex);
  db.exec('BEGIN IMMEDIATE');
  try {
    const prior = db.prepare('SELECT tx FROM mints WHERE id=?').get(deposit.depositId);
    const registrationRow = db.prepare('SELECT tx FROM registrations WHERE id=?').get(config.nativeToken);
    let registration = registrationRow ? parseJson(registrationRow.tx) : null;
    if (prior) { db.exec('COMMIT'); return registration ? { transactions: [registration, parseJson(prior.tx)] } : parseJson(prior.tx); }
    if (config.nativeToken !== protocolToken && !registration) {
      registration = nativeCall(config, 'register', {}, keysFile);
      if (registration) db.prepare('INSERT INTO registrations VALUES (?, ?)').run(config.nativeToken, JSON.stringify(registration));
    }
    const pending = db.prepare('SELECT tx FROM mints').all().map(row => parseJson(row.tx));
    if (registration) pending.unshift(registration);
    const tx = nativeCall(config, 'operation', { operation: 'mint', recipient: deposit.recipient, amount: deposit.amount, fee, pending }, keysFile);
    db.prepare('INSERT INTO mints VALUES (?, ?)').run(deposit.depositId, JSON.stringify(tx));
    db.exec('COMMIT'); return registration ? { transactions: [registration, tx] } : tx;
  } catch (error) { db.exec('ROLLBACK'); throw error; }
}

// Resolve a source contract through the canonical factory, never by symbol.
export async function resolveAssetConfig(base, asset) {
  if (base.nativeToken !== protocolToken) throw Error('Resolve assets from the primary pUSDT bridge configuration.');
  await connect(base);
  asset = getAddress(asset).toLowerCase();
  if (base.mode === 'production') throw Error('Use an explicitly reviewed per-asset production configuration.');
  if (!base.factory || !/^0x[a-f0-9]{40}$/.test(base.factory)) throw Error('Pin the test factory address.');
  const provider = new JsonRpcProvider(base.rpcUrl);
  const factory = new Contract(base.factory, ['function nativeNetwork() view returns(bytes32)', 'function vaults(address) view returns(address)', 'function representation(address) view returns(bytes32)'], provider);
  if (await provider.getCode(base.factory) === '0x' || await factory.nativeNetwork() !== networkHash(base) ||
      (await factory.vaults(base.asset)).toLowerCase() !== base.bridge) throw Error('Factory differs from the pinned primary bridge.');
  const bridge = (await factory.vaults(asset)).toLowerCase();
  if (/^0x0{40}$/.test(bridge)) throw Error('Create and activate the test vault first.');
  const token = new Contract(asset, ['function decimals() view returns(uint8)', 'function symbol() view returns(string)'], provider);
  const decimals = Number(await token.decimals());
  if (decimals < 0 || decimals > 18) throw Error('Unsupported decimals.');
  let symbol; try { symbol = await token.symbol(); } catch { symbol = 'TOKEN'; }
  const resolved = { ...base, asset, bridge, nativeToken: asset === base.asset ? protocolToken : representationId(base.chainId, asset), nativeDecimals: Math.min(8, decimals), nativeSymbol: nativeSymbol(symbol) };
  if (await factory.representation(asset) !== '0x' + resolved.nativeToken) throw Error('Factory representation differs from source contract.');
  await connect(resolved); return resolved;
}
