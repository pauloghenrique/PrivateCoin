import { assertActiveInbound, assertQuoteFresh, buildBitcoinRequest, buildEvmRequest, sameAddress, validateAddress } from './core.mjs';

export function getProvider(asset, browser = globalThis.window) {
  const provider = asset.chain === 'BTC' ? browser?.vultisig?.bitcoin : (browser?.vultisig?.ethereum || browser?.ethereum);
  if (typeof provider?.request !== 'function') {
    throw new Error(asset.chain === 'BTC' ? 'Para conectar Bitcoin, instale a extensão Vultisig.' : 'Abra esta página em um navegador com MetaMask ou Vultisig.');
  }
  return provider;
}

export async function connectWallet(asset, browser, switchNetwork = true) {
  const provider = getProvider(asset, browser);
  if (asset.chain !== 'BTC' && switchNetwork) {
    const chain = await provider.request({ method: 'eth_chainId' });
    if (BigInt(chain) !== BigInt(asset.chainId)) await provider.request({ method: 'wallet_switchEthereumChain', params: [{ chainId: asset.chainId }] });
    const active = await provider.request({ method: 'eth_chainId' });
    if (BigInt(active) !== BigInt(asset.chainId)) throw new Error('A carteira está na rede incorreta.');
  }
  const accounts = await provider.request({ method: asset.chain === 'BTC' ? 'request_accounts' : 'eth_requestAccounts' });
  if (!Array.isArray(accounts) || typeof accounts[0] !== 'string') throw new Error('A carteira não forneceu um endereço.');
  const address = validateAddress(asset, accounts[0]);
  return Object.freeze({ asset, provider, address });
}

export async function executeSwap({ quote, session, client, enabled = false }) {
  // Real broadcasts remain off until deployment-side wallet and network QA is complete.
  if (enabled !== true) throw new Error('Envio de transações desativado nesta prévia.');
  assertQuoteFresh(quote);
  if (quote.from.id !== session?.asset.id) throw new Error('Conecte a carteira da moeda de origem.');
  const provider = session.provider;
  const accounts = await provider.request({ method: quote.from.chain === 'BTC' ? 'get_accounts' : 'eth_accounts' });
  if (!Array.isArray(accounts) || !sameAddress(accounts[0] || '', session.address)) throw new Error('A conta da carteira mudou. Conecte novamente.');
  if (quote.from.chain !== 'BTC') {
    const chain = await provider.request({ method: 'eth_chainId' });
    if (BigInt(chain) !== BigInt(quote.from.chainId)) throw new Error('A carteira está na rede incorreta.');
  }
  await client.assertPersonalAddress(quote.to, quote.destination);
  // Always refresh quote and vault immediately before requesting a signature.
  const fresh = await client.quote(quote.spec);
  if (fresh.minimum < quote.minimum) throw new Error('A cotação mudou. Consulte e revise uma nova cotação.');
  const inbounds = await client.getInbounds();
  const inbound = assertActiveInbound(inbounds.find(i => i.chain === quote.from.chain), quote.from);
  assertActiveInbound(inbounds.find(i => i.chain === quote.to.chain), quote.to);
  // Account and network can change during asynchronous API checks.
  const finalAccounts = await provider.request({ method: quote.from.chain === 'BTC' ? 'get_accounts' : 'eth_accounts' });
  if (!sameAddress(finalAccounts?.[0] || '', session.address)) throw new Error('A conta da carteira mudou. Conecte novamente.');
  if (quote.from.chain !== 'BTC' && BigInt(await provider.request({ method: 'eth_chainId' })) !== BigInt(quote.from.chainId)) throw new Error('A carteira está na rede incorreta.');
  assertQuoteFresh(fresh);
  const transaction = quote.from.chain === 'BTC' ? buildBitcoinRequest(fresh, session.address, inbound) : buildEvmRequest(fresh, session.address, inbound);
  const txid = await provider.request({ method: quote.from.chain === 'BTC' ? 'send_transaction' : 'eth_sendTransaction', params: [transaction] });
  if (typeof txid !== 'string' || !/^(?:0x)?[a-fA-F0-9]{64}$/.test(txid)) throw new Error('A carteira respondeu sem um identificador válido. Verifique a carteira antes de tentar novamente.');
  return { txid, quote: fresh };
}
