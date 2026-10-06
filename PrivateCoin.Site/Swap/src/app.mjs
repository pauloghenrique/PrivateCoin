import { assetFor, assertQuoteFresh, formatUnits } from './core.mjs';
import { ThorClient } from './thor.mjs';
import { connectWallet, executeSwap } from './wallets.mjs';

export function startSwap(document, browser, client = new ThorClient()) {
  const root = document.getElementById('povix-swap');
  if (!root) return;
  const el = id => document.getElementById('swap-' + id);
  const enabled = root.dataset.executionEnabled === 'true';
  let session = null, quote = null, revision = 0, abort = null, expiryTimer = null, stageTimer = null, disposed = false;
  let quoting = false, sending = false, connecting = false, connectingDestination = false;
  const listeners = [];
  let walletListeners = [];
  const detachWallet = () => { walletListeners.forEach(remove => remove()); walletListeners = []; };
  const on = (target, name, handler) => { target.addEventListener(name, handler); listeners.push(() => target.removeEventListener(name, handler)); };
  const controls = ['amount', 'from', 'to', 'destination', 'slippage', 'reverse', 'connect', 'connect-destination'];
  const message = (text, kind = 'error') => { el('message').textContent = text; el('message').dataset.kind = kind; el('message').hidden = !text; };
  const spec = () => ({ from: el('from').value, to: el('to').value, amount: el('amount').value, destination: el('destination').value.trim(), slippageBps: Number(el('slippage').value) });
  function updateButtons() {
    const busy = quoting || sending || connecting || connectingDestination;
    el('quote').disabled = busy;
    el('review').disabled = !enabled || !quote || !session || busy;
    controls.forEach(id => { el(id).disabled = sending; });
    el('connect').disabled = busy;
    el('connect-destination').disabled = busy;
  }
  function invalidate() {
    revision++;
    abort?.abort();
    quote = null;
    browser.clearInterval(expiryTimer);
    el('received').textContent = '—';
    el('details').hidden = true;
    message('');
    el('confirm').close?.();
    updateButtons();
  }
  function showWallet() {
    el('wallet-status').textContent = session ? session.address.slice(0, 7) + '…' + session.address.slice(-5) : 'Carteira desconectada';
    el('connect').textContent = session ? 'Reconectar carteira ↗' : 'Conectar carteira ↗';
  }
  function updateAssets() {
    const from = assetFor(el('from').value), to = assetFor(el('to').value);
    el('from-network').textContent = from.network;
    el('to-network').textContent = to.network;
    el('destination').placeholder = to.chain === 'BTC' ? 'bc1…' : '0x…';
    el('destination-help').textContent = to.chain === 'BTC' ? 'Use um endereço Bitcoin mainnet SegWit ou Taproot (bc1…).' : `Use um endereço de carteira pessoal na rede ${to.network}.`;
    if (session?.asset.id !== from.id) { detachWallet(); session = null; }
    showWallet();
  }
  function timer() {
    if (!quote) return;
    const seconds = Math.max(0, Math.floor(quote.expiry - Date.now() / 1000));
    el('expiry').textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
    try { assertQuoteFresh(quote); } catch { invalidate(); message('A cotação expirou. Consulte novamente.'); }
  }
  async function requestQuote(event) {
    event.preventDefault();
    if (quoting || sending) return;
    invalidate();
    const token = revision;
    abort = new AbortController();
    quoting = true;
    el('quote').textContent = 'Consultando…';
    updateButtons();
    try {
      const result = await client.quote(spec(), abort.signal);
      if (token !== revision || disposed) return;
      quote = result;
      el('received').textContent = formatUnits(result.received);
      el('minimum').textContent = `${formatUnits(result.minimum)} ${result.to.symbol}`;
      el('fees').textContent = `${formatUnits(result.totalFees)} ${result.feeAsset.symbol}`;
      el('time').textContent = result.seconds === null ? 'Depende das redes' : `Cerca de ${Math.max(1, Math.ceil(result.seconds / 60))} min`;
      el('details').hidden = false;
      el('review').textContent = enabled ? 'Revisar troca' : 'Envio desativado nesta prévia';
      timer();
      expiryTimer = browser.setInterval(timer, 1000);
      if (!enabled) message('Esta prévia permite consultar cotações e conectar carteiras. O envio de transações está desativado.', 'success');
    } catch (error) {
      if (token === revision && !disposed && error.name !== 'AbortError') message(error.message || 'Não foi possível consultar a cotação.');
    } finally {
      quoting = false;
      el('quote').textContent = 'Consultar cotação →';
      updateButtons();
    }
  }
  async function connectSource() {
    if (connecting) return;
    detachWallet();
    invalidate();
    session = null;
    showWallet();
    const token = revision;
    connecting = true; updateButtons(); message('');
    try {
      const result = await connectWallet(assetFor(el('from').value), browser);
      if (token !== revision || disposed) return;
      session = result;
      showWallet();
      message('Carteira conectada. Nenhuma assinatura de transação foi solicitada.', 'success');
      // Account or chain changes invalidate reviewed quotes. Reconnect explicitly.
      if (result.provider.on) {
        const changed = () => { session = null; invalidate(); showWallet(); };
        for (const event of ['accountsChanged', 'chainChanged', 'disconnect']) {
          result.provider.on(event, changed);
          walletListeners.push(() => result.provider.removeListener?.(event, changed));
        }
      }
    } catch (error) { if (token === revision) message(error.message || 'A conexão foi recusada.'); }
    finally { connecting = false; updateButtons(); }
  }
  async function connectDestination() {
    if (connectingDestination) return;
    const token = revision;
    connectingDestination = true; updateButtons();
    try {
      const result = await connectWallet(assetFor(el('to').value), browser, false);
      if (token !== revision || disposed) return;
      invalidate(); el('destination').value = result.address;
      message('Endereço de recebimento preenchido com sua carteira.', 'success');
    } catch (error) { if (token === revision) message(error.message || 'Não foi possível obter o endereço.'); }
    finally { connectingDestination = false; updateButtons(); }
  }
  function review() {
    if (!enabled || !quote || !session || sending) return;
    try {
      assertQuoteFresh(quote);
      el('confirm-summary').textContent = `${quote.amountText} ${quote.from.symbol} → aproximadamente ${formatUnits(quote.received)} ${quote.to.symbol}`;
      el('confirm-destination').textContent = quote.destination;
      el('confirm-minimum').textContent = `${formatUnits(quote.minimum)} ${quote.to.symbol}`;
      el('confirm').showModal();
    } catch (error) { message(error.message); }
  }
  async function sign() {
    if (!enabled || !quote || !session || sending) return;
    sending = true;
    el('sign').disabled = true;
    updateButtons();
    let tracked;
    try {
      tracked = await executeSwap({ quote, session, client, enabled });
      el('confirm').close();
      el('history').hidden = false;
      const explorers = { BTC: 'https://mempool.space/tx/', ETH: 'https://etherscan.io/tx/', BSC: 'https://bscscan.com/tx/' };
      el('tx-link').href = explorers[tracked.quote.from.chain] + tracked.txid;
      el('tx-status').textContent = 'Transação enviada. Aguardando observação e confirmações das redes.';
      invalidate();
      let attempts = 0;
      browser.clearInterval(stageTimer);
      const poll = async () => {
        try {
          const stages = await client.stages(tracked.txid);
          if (stages.outbound_signed?.completed) { el('tx-status').textContent = 'A transação de destino foi assinada. Confira o recebimento na sua carteira.'; browser.clearInterval(stageTimer); }
          else if (stages.swap_finalised?.completed) el('tx-status').textContent = 'Troca processada. Aguardando envio para a rede de destino.';
          else if (stages.inbound_observed?.completed) el('tx-status').textContent = 'Depósito observado. Aguardando processamento da troca.';
        } catch { el('tx-status').textContent = 'Não foi possível atualizar o acompanhamento. Consulte a carteira e o explorador antes de repetir a operação.'; }
        if (++attempts >= 240) browser.clearInterval(stageTimer);
      };
      stageTimer = browser.setInterval(poll, 15000);
      await poll();
    } catch (error) {
      el('confirm').close();
      message(error.code === 4001 ? 'Você recusou a transação na carteira.' : error.message || 'A carteira não confirmou o envio. Confira a carteira antes de repetir a operação.');
      if (!tracked) { quote = null; el('received').textContent = '—'; el('details').hidden = true; browser.clearInterval(expiryTimer); }
    } finally { sending = false; el('sign').disabled = false; updateButtons(); }
  }
  on(el('form'), 'submit', requestQuote);
  for (const id of ['amount', 'destination', 'slippage']) on(el(id), 'input', invalidate);
  for (const id of ['from', 'to']) on(el(id), 'change', () => {
    if (el('from').value === el('to').value) {
      const other = id === 'from' ? 'to' : 'from';
      el(other).value = Object.keys({ BTC: 1, ETH: 1, BNB: 1 }).find(a => a !== el(id).value);
    }
    invalidate(); el('destination').value = ''; updateAssets();
  });
  on(el('reverse'), 'click', () => {
    const previous = el('from').value; el('from').value = el('to').value; el('to').value = previous;
    invalidate(); el('destination').value = ''; updateAssets();
  });
  on(el('connect'), 'click', connectSource);
  on(el('connect-destination'), 'click', connectDestination);
  on(el('review'), 'click', review);
  on(el('sign'), 'click', sign);
  const dispose = () => { disposed = true; abort?.abort(); detachWallet(); browser.clearInterval(expiryTimer); browser.clearInterval(stageTimer); listeners.forEach(remove => remove()); };
  on(browser, 'pagehide', dispose);
  root.querySelector('.swap-preview').textContent = enabled ? 'Assinatura na sua carteira' : 'Prévia · envio desativado';
  updateAssets(); updateButtons();
  return { dispose };
}

if (typeof document !== 'undefined' && typeof window !== 'undefined') startSwap(document, window);
