import { assetFor, assertActiveInbound, normalizeQuote, parseAmount, validateAddress } from './core.mjs';

export const THOR_API = 'https://gateway.liquify.com/chain/thorchain_api/thorchain';
const RPC = Object.freeze({ ETH: 'https://ethereum-rpc.publicnode.com', BSC: 'https://bsc-rpc.publicnode.com' });

export class ThorClient {
  constructor(fetcher = globalThis.fetch.bind(globalThis)) { this.fetcher = fetcher; }

  async get(path, signal) {
    const timeout = AbortSignal.timeout(15000);
    const response = await this.fetcher(THOR_API + path, { signal: signal ? AbortSignal.any([signal, timeout]) : timeout, cache: 'no-store', credentials: 'omit' });
    if (!response.ok) throw new Error('O serviço de swaps está indisponível. Tente novamente.');
    const body = await response.json();
    if (body.error || body.code) throw new Error('A rota não está disponível para esta quantidade ou tolerância de preço.');
    return body;
  }

  async getInbounds(signal) {
    const result = await this.get('/inbound_addresses', signal);
    if (!Array.isArray(result)) throw new Error('Não foi possível verificar as redes.');
    return result;
  }

  async quote(spec, signal) {
    const from = assetFor(spec.from), to = assetFor(spec.to);
    if (from.id === to.id) throw new Error('Selecione moedas diferentes.');
    const amount = parseAmount(spec.amount);
    validateAddress(to, spec.destination);
    const bps = Number(spec.slippageBps);
    if (!Number.isInteger(bps) || bps < 10 || bps > 300) throw new Error('Tolerância de preço inválida.');
    const [pools, inbounds] = await Promise.all([this.get('/pools', signal), this.getInbounds(signal)]);
    if (!Array.isArray(pools) || ![from, to].every(asset => pools.some(p => p.asset === asset.id && p.status === 'Available'))) {
      throw new Error('Uma das moedas está sem pool disponível.');
    }
    const inbound = assertActiveInbound(inbounds.find(i => i.chain === from.chain), from);
    assertActiveInbound(inbounds.find(i => i.chain === to.chain), to);
    const params = new URLSearchParams({
      from_asset: from.id, to_asset: to.id, amount: amount.toString(), destination: spec.destination,
      liquidity_tolerance_bps: String(bps), streaming_interval: '1', streaming_quantity: '0'
    });
    const raw = await this.get('/quote/swap?' + params, signal);
    return normalizeQuote(raw, spec, inbound);
  }

  async assertPersonalAddress(asset, address) {
    if (asset.chain === 'BTC') return;
    const response = await this.fetcher(RPC[asset.chain], {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'omit',
      signal: AbortSignal.timeout(15000), body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'eth_getCode', params: [address, 'latest'] })
    });
    if (!response.ok) throw new Error('Não foi possível verificar o endereço na rede de destino.');
    const body = await response.json();
    if (body.error || body.result !== '0x') throw new Error('Esta versão aceita apenas endereços de carteiras pessoais, sem contratos.');
  }

  async stages(txid, signal) {
    if (!/^(?:0x)?[a-fA-F0-9]{64}$/.test(txid)) throw new Error('Identificador de transação inválido.');
    return this.get('/tx/stages/' + txid.replace(/^0x/, ''), signal);
  }
}
