import { bech32, bech32m } from '@scure/base';
import { encodeFunctionData, isAddress } from 'viem';

export const ASSETS = Object.freeze({
  BTC: Object.freeze({ symbol: 'BTC', name: 'Bitcoin', chain: 'BTC', id: 'BTC.BTC', decimals: 8, network: 'Bitcoin', chainId: null }),
  ETH: Object.freeze({ symbol: 'ETH', name: 'Ethereum', chain: 'ETH', id: 'ETH.ETH', decimals: 18, network: 'Ethereum', chainId: '0x1' }),
  BNB: Object.freeze({ symbol: 'BNB', name: 'BNB', chain: 'BSC', id: 'BSC.BNB', decimals: 18, network: 'BNB Smart Chain', chainId: '0x38' })
});

export function assetFor(symbol) {
  if (!Object.hasOwn(ASSETS, symbol)) throw new Error('Moeda não suportada.');
  return ASSETS[symbol];
}

export function parseAmount(text, decimals = 8) {
  const value = String(text).trim().replace(',', '.');
  if (value.length > 50 || !/^\d+(?:\.\d+)?$/.test(value)) throw new Error('Informe uma quantidade válida, sem separadores de milhar.');
  const [whole, fraction = ''] = value.split('.');
  if (fraction.length > decimals) throw new Error(`Use no máximo ${decimals} casas decimais.`);
  const units = BigInt(whole) * 10n ** BigInt(decimals) + BigInt(fraction.padEnd(decimals, '0') || '0');
  if (units <= 0n) throw new Error('A quantidade precisa ser maior que zero.');
  return units;
}

export function integer(text, label = 'Valor') {
  if (typeof text === 'number' && !Number.isSafeInteger(text)) throw new Error(`${label} inválido.`);
  const value = String(text);
  if (!/^\d{1,80}$/.test(value)) throw new Error(`${label} inválido.`);
  return BigInt(value);
}

export function formatUnits(value, decimals = 8) {
  const n = integer(value);
  const padded = n.toString().padStart(decimals + 1, '0');
  if (!decimals) return padded;
  const tail = padded.slice(-decimals).replace(/0+$/, '');
  return padded.slice(0, -decimals) + (tail ? '.' + tail : '');
}

export function validateAddress(asset, address) {
  if (typeof address !== 'string' || address !== address.trim()) throw new Error('Endereço de destino inválido.');
  if (asset.chain !== 'BTC') {
    if (!isAddress(address) || /^0x0{40}$/i.test(address)) throw new Error('Use um endereço válido da rede selecionada.');
    return address;
  }
  // This release accepts mainnet native SegWit and Taproot receiving addresses.
  try {
    const decoder = address.toLowerCase().startsWith('bc1p') ? bech32m : bech32;
    const { prefix, words } = decoder.decode(address, 90);
    const program = decoder.fromWords(words.slice(1));
    if (prefix !== 'bc' || !((words[0] === 0 && [20, 32].includes(program.length)) || (words[0] === 1 && program.length === 32))) throw new Error();
    return address.toLowerCase();
  } catch {
    throw new Error('Use um endereço Bitcoin mainnet SegWit ou Taproot válido (bc1…).');
  }
}

export function sameAddress(a, b) { return a.toLowerCase() === b.toLowerCase(); }

export function assertActiveInbound(inbound, asset) {
  if (!inbound || inbound.chain !== asset.chain || inbound.halted !== false ||
      [inbound.chain_trading_paused, inbound.global_trading_paused].some(v => v === true || v === 'true')) {
    throw new Error(`As trocas na rede ${asset.network} estão indisponíveis.`);
  }
  validateAddress(asset, inbound.address);
  if (asset.chain !== 'BTC' && !isAddress(inbound.router)) throw new Error('Roteador da rede indisponível.');
  return inbound;
}

export function assertQuoteFresh(quote, now = Date.now()) {
  if (!Number.isSafeInteger(quote.expiry) || quote.expiry * 1000 <= now + 30000) throw new Error('A cotação expirou. Consulte uma nova cotação.');
}

export function normalizeQuote(raw, spec, inbound, now = Date.now()) {
  const from = assetFor(spec.from), to = assetFor(spec.to);
  if (from.id === to.id) throw new Error('Selecione moedas diferentes.');
  const amount = parseAmount(spec.amount);
  const destination = validateAddress(to, spec.destination);
  assertActiveInbound(inbound, from);
  if (!sameAddress(raw.inbound_address || '', inbound.address)) throw new Error('O endereço de entrada mudou. Consulte novamente.');
  const expiry = Number(raw.expiry);
  assertQuoteFresh({ expiry }, now);
  const received = integer(raw.expected_amount_out, 'Quantidade recebida');
  if (!received) throw new Error('A rota não oferece recebimento válido.');
  const min = integer(raw.recommended_min_amount_in ?? raw.dust_threshold ?? '0');
  if (amount <= integer(raw.dust_threshold ?? '0') || amount < min) throw new Error(`Quantidade insuficiente para cobrir as taxas. Mínimo recomendado: ${formatUnits(min)} ${from.symbol}.`);
  const parts = String(raw.memo || '').split(':');
  if (!['=', 'SWAP', 's'].includes(parts[0]) || parts[1] !== to.id || !sameAddress(parts[2] || '', destination) ||
      !/^\d+(?:\/\d+\/\d+)?$/.test(parts[3] || '') || parts.length !== 4) {
    throw new Error('A rota retornou instruções de troca incompatíveis.');
  }
  const minimum = integer(parts[3].split('/')[0]);
  if (!minimum || minimum > received) throw new Error('A cotação precisa garantir um recebimento mínimo válido.');
  const bps = Number(spec.slippageBps);
  if (!Number.isInteger(bps) || bps < 10 || bps > 300 || minimum < received * BigInt(10000 - bps) / 10000n) {
    throw new Error('O recebimento mínimo não respeita a tolerância de preço selecionada.');
  }
  // Long Bitcoin memos need additional outputs; never silently truncate them.
  if (from.chain === 'BTC' && new TextEncoder().encode(raw.memo).length > 80) throw new Error('Esta rota exige um formato Bitcoin ainda não suportado.');
  const feeAsset = Object.values(ASSETS).find(a => a.id === raw.fees?.asset);
  const totalFees = integer(raw.fees?.total ?? '0', 'Taxas');
  if (totalFees && !feeAsset) throw new Error('A cotação retornou uma moeda de taxa incompatível.');
  const seconds = Number(raw.total_swap_seconds);
  return Object.freeze({
    from, to, amount, amountText: formatUnits(amount), destination, inboundAddress: inbound.address,
    router: inbound.router || null, memo: raw.memo, expiry, received, minimum, totalFees,
    feeAsset: feeAsset || to, seconds: Number.isFinite(seconds) && seconds >= 0 ? seconds : null,
    spec: Object.freeze({ ...spec, destination })
  });
}

export function buildEvmRequest(quote, sender, inbound, now = Date.now()) {
  assertQuoteFresh(quote, now);
  if (quote.from.chain === 'BTC') throw new Error('A origem não é uma rede EVM.');
  validateAddress(quote.from, sender);
  assertActiveInbound(inbound, quote.from);
  if (!sameAddress(inbound.address, quote.inboundAddress) || !sameAddress(inbound.router, quote.router)) throw new Error('A rota mudou. Consulte novamente.');
  const value = quote.amount * 10n ** BigInt(quote.from.decimals - 8);
  const data = encodeFunctionData({
    abi: [{ type: 'function', name: 'depositWithExpiry', stateMutability: 'payable', inputs: [
      { name: 'vault', type: 'address' }, { name: 'asset', type: 'address' }, { name: 'amount', type: 'uint256' },
      { name: 'memo', type: 'string' }, { name: 'expiry', type: 'uint256' }
    ], outputs: [] }],
    functionName: 'depositWithExpiry',
    args: [inbound.address, '0x0000000000000000000000000000000000000000', value, quote.memo, BigInt(Math.floor(now / 1000) + 3600)]
  });
  return { from: sender, to: inbound.router, value: '0x' + value.toString(16), data };
}

export function buildBitcoinRequest(quote, sender, inbound, now = Date.now()) {
  assertQuoteFresh(quote, now);
  if (quote.from.chain !== 'BTC') throw new Error('A origem não é Bitcoin.');
  validateAddress(quote.from, sender);
  assertActiveInbound(inbound, quote.from);
  if (!sameAddress(inbound.address, quote.inboundAddress)) throw new Error('O endereço de entrada mudou. Consulte novamente.');
  if (quote.amount > BigInt(Number.MAX_SAFE_INTEGER)) throw new Error('Quantidade incompatível com a carteira Bitcoin.');
  // Vultisig's UTXO adapter expects the memo as UTF-8 text, not hexadecimal.
  return { from: sender, to: inbound.address, value: '0x' + quote.amount.toString(16), data: quote.memo };
}
