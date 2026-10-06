import test from 'node:test';
import assert from 'node:assert/strict';
import { bech32 } from '@scure/base';
import { decodeFunctionData } from 'viem';
import { ASSETS, assetFor, parseAmount, integer, formatUnits, validateAddress, normalizeQuote, buildEvmRequest, buildBitcoinRequest } from '../src/core.mjs';
import { ThorClient, THOR_API } from '../src/thor.mjs';
import { connectWallet, executeSwap } from '../src/wallets.mjs';

const address = digit => '0x' + digit.repeat(40);
const sender = address('1'), recipient = address('2'), vault = address('3'), router = address('4');
const btc = bech32.encode('bc', [0, ...bech32.toWords(new Uint8Array(20).fill(1))]);
const bitcoinVault = bech32.encode('bc', [0, ...bech32.toWords(new Uint8Array(32).fill(2))]);
const inbound = asset => ({ chain: asset.chain, address: asset.chain === 'BTC' ? bitcoinVault : vault, router: asset.chain === 'BTC' ? '' : router, halted: false, chain_trading_paused: false, global_trading_paused: false });
const specFor = (from = 'ETH', to = 'BTC') => ({ from, to, amount: '0.25', destination: to === 'BTC' ? btc : recipient, slippageBps: 100 });
function rawFor(spec) {
  return { inbound_address: inbound(ASSETS[spec.from]).address, expected_amount_out: '1000000', expiry: Math.floor(Date.now() / 1000) + 300, memo: `=:${ASSETS[spec.to].id}:${spec.destination}:990000/1/0`, dust_threshold: '1000', recommended_min_amount_in: '2000', fees: { asset: ASSETS[spec.to].id, total: '12000' }, total_swap_seconds: 600 };
}
function quoteFor(spec = specFor()) { return normalizeQuote(rawFor(spec), spec, inbound(ASSETS[spec.from])); }

test('amounts preserve precision beyond Number.MAX_SAFE_INTEGER and round-trip', () => {
  assert.equal(parseAmount('90071992.54740993'), 9007199254740993n);
  assert.equal(formatUnits(parseAmount('90071992.54740993')), '90071992.54740993');
  assert.equal(parseAmount('0,00000001'), 1n);
  assert.equal(parseAmount('000.25'), 25000000n);
});

test('invalid, zero, scientific and over-precision amounts fail closed', () => {
  for (const value of ['0', '-1', '1e3', 'Infinity', '0.000000001', '1,000.00', '1.2.3', '1 000', '', 'NaN']) assert.throws(() => parseAmount(value));
  assert.throws(() => integer(Number.MAX_SAFE_INTEGER + 1));
  assert.throws(() => assetFor('POVIX'));
  assert.throws(() => assetFor('__proto__'));
});

test('destination validation rejects other networks, corrupt checksums and zero addresses', () => {
  assert.equal(validateAddress(ASSETS.BTC, btc), btc);
  assert.equal(validateAddress(ASSETS.ETH, recipient), recipient);
  const testnet = bech32.encode('tb', [0, ...bech32.toWords(new Uint8Array(20).fill(1))]);
  for (const value of [testnet, btc.slice(0, -1) + 'x', recipient]) assert.throws(() => validateAddress(ASSETS.BTC, value));
  for (const value of [btc, '0x' + '0'.repeat(40), '0x1234']) assert.throws(() => validateAddress(ASSETS.BNB, value));
});

test('valid quote binds recipient, asset, minimum and current vault', () => {
  const quote = quoteFor();
  assert.equal(quote.amount, 25000000n);
  assert.equal(quote.minimum, 990000n);
  assert.equal(quote.destination, btc);
  assert.equal(quote.inboundAddress, vault);
  assert.ok(Object.isFrozen(quote));
});

test('unsafe or mismatched quote instructions never reach a wallet', () => {
  const spec = specFor();
  for (const patch of [
    { memo: `=:BTC.BTC:${btc}:0/1/0` },
    { memo: `=:BTC.BTC:${btc}:100/1/0` },
    { memo: `=:ETH.ETH:${btc}:990000/1/0` },
    { memo: `=:BTC.BTC:${bitcoinVault}:990000/1/0` },
    { memo: `=:BTC.BTC:${btc}:990000/1/0:someone:100` },
    { inbound_address: recipient },
    { expiry: Math.floor(Date.now() / 1000) - 1 },
    { expected_amount_out: '0' },
    { recommended_min_amount_in: '30000000' },
    { fees: { asset: 'UNKNOWN.TOKEN', total: '10' } }
  ]) assert.throws(() => normalizeQuote({ ...rawFor(spec), ...patch }, spec, inbound(ASSETS.ETH)));
});

test('paused or halted networks and rotated vaults are rejected', () => {
  const spec = specFor();
  for (const flag of ['halted', 'chain_trading_paused', 'global_trading_paused']) assert.throws(() => normalizeQuote(rawFor(spec), spec, { ...inbound(ASSETS.ETH), [flag]: true }));
  assert.throws(() => buildEvmRequest(quoteFor(), sender, { ...inbound(ASSETS.ETH), address: recipient }));
});

test('EVM transaction deposits the exact native amount through depositWithExpiry', () => {
  const quote = quoteFor();
  const tx = buildEvmRequest(quote, sender, inbound(ASSETS.ETH));
  assert.equal(tx.to, router);
  assert.equal(BigInt(tx.value), 250000000000000000n);
  const abi = [{ type: 'function', name: 'depositWithExpiry', inputs: [{ type: 'address' }, { type: 'address' }, { type: 'uint256' }, { type: 'string' }, { type: 'uint256' }], outputs: [] }];
  const decoded = decodeFunctionData({ abi, data: tx.data });
  assert.equal(decoded.functionName, 'depositWithExpiry');
  assert.equal(decoded.args[0].toLowerCase(), vault);
  assert.equal(decoded.args[1], '0x' + '0'.repeat(40));
  assert.equal(decoded.args[2], BigInt(tx.value));
  assert.equal(decoded.args[3], quote.memo);
  assert.ok(decoded.args[4] >= BigInt(Math.floor(Date.now() / 1000) + 3590));
});

test('Bitcoin adapter uses satoshis, literal UTF-8 memo and current vault', () => {
  const quote = quoteFor(specFor('BTC', 'ETH'));
  const tx = buildBitcoinRequest(quote, btc, inbound(ASSETS.BTC));
  assert.equal(BigInt(tx.value), 25000000n);
  assert.equal(tx.data, quote.memo);
  assert.equal(tx.to, bitcoinVault);
  assert.throws(() => buildBitcoinRequest(quote, btc, { ...inbound(ASSETS.BTC), address: btc }));
});

test('EVM wallet alone is never treated as a Bitcoin wallet', async () => {
  const browser = { ethereum: { request: async () => [sender] } };
  await assert.rejects(connectWallet(ASSETS.BTC, browser), /Bitcoin/);
});

test('source EVM network must acknowledge the selected chain', async () => {
  let chain = '0x1';
  const methods = [];
  const provider = { request: async ({ method }) => { methods.push(method); if (method === 'wallet_switchEthereumChain') chain = '0x38'; return method === 'eth_chainId' ? chain : [sender]; } };
  const connected = await connectWallet(ASSETS.BNB, { ethereum: provider });
  assert.equal(connected.address, sender);
  assert.ok(methods.includes('wallet_switchEthereumChain'));
  const stuck = { request: async ({ method }) => method === 'eth_chainId' ? '0x1' : [sender] };
  await assert.rejects(connectWallet(ASSETS.BNB, { ethereum: stuck }), /rede incorreta/);
});

function executionFixture(quote = quoteFor()) {
  const calls = [];
  const account = quote.from.chain === 'BTC' ? btc : sender;
  const provider = { request: async request => { calls.push(request); if (request.method === 'eth_chainId') return quote.from.chainId; if (request.method.includes('send')) return 'a'.repeat(64); return [account]; } };
  const client = { assertPersonalAddress: async () => {}, quote: async () => quote, getInbounds: async () => Object.values(ASSETS).map(inbound) };
  return { quote, session: { asset: quote.from, address: account, provider }, client, calls };
}

test('preview never requests any account, signature or broadcast', async () => {
  const fixture = executionFixture();
  await assert.rejects(executeSwap(fixture), /desativado/);
  assert.deepEqual(fixture.calls, []);
});

test('authorized mock EVM flow rechecks account and emits one exact transaction', async () => {
  const fixture = executionFixture();
  const result = await executeSwap({ ...fixture, enabled: true });
  assert.equal(result.txid, 'a'.repeat(64));
  assert.equal(fixture.calls.filter(c => c.method === 'eth_accounts').length, 2);
  assert.equal(fixture.calls.filter(c => c.method === 'eth_sendTransaction').length, 1);
  assert.equal(fixture.calls.at(-1).params[0].to, router);
});

test('Bitcoin execution uses the documented adapter and does not ask an EVM wallet to send BTC', async () => {
  const fixture = executionFixture(quoteFor(specFor('BTC', 'BNB')));
  await executeSwap({ ...fixture, enabled: true });
  assert.equal(fixture.calls.at(-1).method, 'send_transaction');
  assert.equal(fixture.calls.at(-1).params[0].data, fixture.quote.memo);
});

test('a worse minimum after quote refresh requires another review', async () => {
  const fixture = executionFixture();
  fixture.client.quote = async () => ({ ...fixture.quote, minimum: fixture.quote.minimum - 1n });
  await assert.rejects(executeSwap({ ...fixture, enabled: true }), /mudou/);
  assert.ok(!fixture.calls.some(c => c.method === 'eth_sendTransaction'));
});

test('account changes during network checks block the signature', async () => {
  const fixture = executionFixture();
  const original = fixture.session.provider.request;
  let accountChecks = 0;
  fixture.session.provider.request = async request => request.method === 'eth_accounts' && ++accountChecks === 2 ? [recipient] : original(request);
  await assert.rejects(executeSwap({ ...fixture, enabled: true }), /conta.*mudou/);
  assert.ok(!fixture.calls.some(c => c.method === 'eth_sendTransaction'));
});

test('wallet rejection is propagated without automatic retries', async () => {
  const fixture = executionFixture();
  const original = fixture.session.provider.request;
  let attempts = 0;
  fixture.session.provider.request = async request => { if (request.method === 'eth_sendTransaction') { attempts++; throw Object.assign(new Error('Rejected'), { code: 4001 }); } return original(request); };
  await assert.rejects(executeSwap({ ...fixture, enabled: true }), e => e.code === 4001);
  assert.equal(attempts, 1);
});

test('HTTP quote uses native asset IDs, exact 1e8 units and a bounded minimum', async () => {
  const requests = [];
  const spec = specFor('BNB', 'ETH');
  const client = new ThorClient(async url => {
    requests.push(url);
    return { ok: true, json: async () => url.endsWith('/pools') ? Object.values(ASSETS).map(a => ({ asset: a.id, status: 'Available' })) : url.endsWith('/inbound_addresses') ? Object.values(ASSETS).map(inbound) : rawFor(spec) };
  });
  const result = await client.quote(spec);
  const url = new URL(requests.find(u => u.includes('/quote/swap')));
  assert.equal(url.origin + url.pathname, THOR_API + '/quote/swap');
  assert.equal(url.searchParams.get('from_asset'), 'BSC.BNB');
  assert.equal(url.searchParams.get('amount'), '25000000');
  assert.equal(url.searchParams.get('liquidity_tolerance_bps'), '100');
  assert.equal(result.to.id, 'ETH.ETH');
});

test('unavailable pools stop before quoting; API errors never produce a fake price', async () => {
  const requests = [];
  const unavailable = new ThorClient(async url => { requests.push(url); return { ok: true, json: async () => url.endsWith('/pools') ? [] : Object.values(ASSETS).map(inbound) }; });
  await assert.rejects(unavailable.quote(specFor()), /pool/);
  assert.ok(!requests.some(u => u.includes('/quote/swap')));
  const denied = new ThorClient(async () => ({ ok: false, status: 403 }));
  await assert.rejects(denied.quote(specFor()), /indisponível/);
});

test('contract destinations are rejected before a signature', async () => {
  const client = new ThorClient(async () => ({ ok: true, json: async () => ({ result: '0x6001' }) }));
  await assert.rejects(client.assertPersonalAddress(ASSETS.ETH, recipient), /sem contratos/);
});
