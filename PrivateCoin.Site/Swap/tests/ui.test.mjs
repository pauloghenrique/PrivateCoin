import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';
import { bech32 } from '@scure/base';
import { startSwap } from '../src/app.mjs';
import { ASSETS, normalizeQuote } from '../src/core.mjs';

const markup = await readFile(new URL('../markup.html', import.meta.url), 'utf8');
const btc = bech32.encode('bc', [0, ...bech32.toWords(new Uint8Array(20).fill(1))]);
const vault = '0x' + '3'.repeat(40), router = '0x' + '4'.repeat(40);
const makeQuote = spec => normalizeQuote({ inbound_address: vault, expiry: Math.floor(Date.now() / 1000) + 300, expected_amount_out: '1000000', memo: `=:${ASSETS[spec.to].id}:${spec.destination}:990000/1/0`, dust_threshold: '1000', recommended_min_amount_in: '2000', fees: { asset: ASSETS[spec.to].id, total: '12000' }, total_swap_seconds: 600 }, spec, { chain: 'ETH', address: vault, router, halted: false });
const turn = () => new Promise(resolve => setImmediate(resolve));
function setup(t, client = { quote: async spec => makeQuote(spec) }) {
  const dom = new JSDOM(markup, { url: 'https://povix.example/Home/Swap' });
  const { document } = dom.window;
  const app = startSwap(document, dom.window, client);
  t.after(() => { app.dispose(); dom.window.close(); });
  const get = id => document.getElementById('swap-' + id);
  const input = (id, value) => { get(id).value = value; get(id).dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
  const submit = () => get('form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
  return { dom, document, get, input, submit };
}

test('preview renders no invented price and exposes the transaction gate', t => {
  const { get, document } = setup(t);
  assert.equal(get('received').textContent, '—');
  assert.equal(get('review').disabled, true);
  assert.match(document.querySelector('.swap-preview').textContent, /envio desativado/);
  assert.equal(get('details').hidden, true);
});

test('successful quote shows minimum, real response fees and expiry while broadcast stays disabled', async t => {
  const { get, input, submit } = setup(t);
  input('amount', '0.25'); input('destination', btc); submit(); await turn();
  assert.equal(get('received').textContent, '0.01');
  assert.equal(get('minimum').textContent, '0.0099 BTC');
  assert.equal(get('fees').textContent, '0.00012 BTC');
  assert.equal(get('details').hidden, false);
  assert.equal(get('review').disabled, true);
  assert.match(get('expiry').textContent, /^\d+:\d{2}$/);
});

test('editing the amount invalidates received value, minimum and reviewed quote', async t => {
  const { get, input, submit } = setup(t);
  input('amount', '0.25'); input('destination', btc); submit(); await turn();
  input('amount', '0.5');
  assert.equal(get('received').textContent, '—');
  assert.equal(get('details').hidden, true);
  assert.equal(get('review').disabled, true);
});

test('a slow response for an old amount cannot overwrite the current form', async t => {
  let resolve;
  const client = { quote: spec => new Promise(done => { resolve = () => done(makeQuote(spec)); }) };
  const { get, input, submit } = setup(t, client);
  input('amount', '0.25'); input('destination', btc); submit();
  input('amount', '0.5'); resolve(); await turn();
  assert.equal(get('received').textContent, '—');
  assert.equal(get('details').hidden, true);
});

test('reversing networks clears the destination and source wallet session', t => {
  const { get, input } = setup(t);
  input('destination', btc); get('reverse').click();
  assert.equal(get('from').value, 'BTC');
  assert.equal(get('to').value, 'ETH');
  assert.equal(get('destination').value, '');
  assert.equal(get('from-network').textContent, 'Bitcoin');
  assert.equal(get('to-network').textContent, 'Ethereum');
});

test('network failures remain visible and leave the quote empty', async t => {
  const { get, input, submit } = setup(t, { quote: async () => { throw new Error('Serviço indisponível'); } });
  input('amount', '0.25'); input('destination', btc); submit(); await turn();
  assert.equal(get('received').textContent, '—');
  assert.match(get('message').textContent, /indisponível/);
  assert.equal(get('details').hidden, true);
});

test('connecting an absent wallet explains the requirement without requesting any secrets', async t => {
  const { get } = setup(t);
  get('connect').click(); await turn();
  assert.match(get('message').textContent, /MetaMask|Vultisig/);
  assert.equal(get('wallet-status').textContent, 'Carteira desconectada');
});

test('changing the wallet account invalidates a previously displayed quote', async t => {
  const { dom, get, input, submit } = setup(t);
  const handlers = new Map();
  dom.window.ethereum = {
    request: async ({ method }) => method === 'eth_chainId' ? '0x1' : ['0x' + '1'.repeat(40)],
    on: (event, handler) => handlers.set(event, handler),
    removeListener: event => handlers.delete(event)
  };
  get('connect').click(); await turn();
  input('amount', '0.25'); input('destination', btc); submit(); await turn();
  assert.equal(get('details').hidden, false);
  handlers.get('accountsChanged')();
  assert.equal(get('received').textContent, '—');
  assert.equal(get('details').hidden, true);
  assert.equal(get('wallet-status').textContent, 'Carteira desconectada');
});
