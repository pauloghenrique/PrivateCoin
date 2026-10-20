import test from 'node:test';
import assert from 'node:assert/strict';
import ganache from 'ganache';
import { BrowserProvider, Wallet, ContractFactory, keccak256, toUtf8Bytes } from 'ethers';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { compile, root } from '../src/compile.mjs';
import { types, domain, networkHash, sortSignatures, journal, recordDecision, native, prepareMint, verifyDeposit, validateConfig } from '../src/bridge.mjs';
const mono = process.env.POVIX_MONO || 'mono';
const read = file => JSON.parse(readFileSync(file, 'utf8'));
const hash = text => keccak256(toUtf8Bytes(text));

test('USDT BNB → native POVIX token → USDT BNB', { timeout: 900000 }, async t => {
  const dir = mkdtempSync(path.join(tmpdir(), 'povix-usdt-test-'));
  const rpc = ganache.provider({ chain: { chainId: 31337 }, logging: { quiet: true }, wallet: { totalAccounts: 6 } });
  const provider = new BrowserProvider(rpc, undefined, { cacheTimeout: -1 });
  let db;
  try {
    console.log('Preparing real native POVIX chain fixture');
    console.log(execFileSync(mono, [path.join(root, 'build/native/BridgeNativeRegression.exe'), 'setup', dir], { encoding: 'utf8', timeout: 240000 }).trim());
    const artifact = compile(), mockArtifact = compile('test/MockUsdt.sol', 'MockUsdt');
    const wallets = Object.values(rpc.getInitialAccounts()).map(a => new Wallet(a.secretKey));
    const sender = await provider.getSigner(0), receiver = wallets[4].address;
    const mock = await new ContractFactory(mockArtifact.abi, mockArtifact.bytecode, sender).deploy(); await mock.waitForDeployment();
    let config = { ...read(path.join(dir, 'native-config.json')), rpcUrl: 'http://127.0.0.1:8545', nativeSnapshot: path.join(dir, 'chain.json'), asset: (await mock.getAddress()).toLowerCase(),
      operators: wallets.slice(0, 3).map(w => w.address), threshold: 2, bscConfirmations: 15, reserveCap: '1000000000', dailyReleaseCap: '100000000' };
    const factory = new ContractFactory(artifact.abi, artifact.bytecode, sender);
    const vault = await factory.deploy(config.asset, networkHash(config), '0x' + config.nativeToken, config.operators, 2, config.reserveCap, config.dailyReleaseCap); await vault.waitForDeployment();
    config.bridge = (await vault.getAddress()).toLowerCase(); validateConfig(config);
    writeFileSync(path.join(dir, 'config.json'), JSON.stringify(config));
    execFileSync(mono, [path.join(root, 'build/native/BridgeNativeRegression.exe'), 'configure', dir], { encoding: 'utf8', timeout: 240000 });
    const holder = readFileSync(path.join(dir, 'holder.txt'), 'utf8');
    const deadline = () => Math.floor(Date.now() / 1000) + 600;
    const sign = async (kind, value, members = wallets.slice(0, 2), c = config) => Promise.all(members.map(w => w.signTypedData(domain(c), { [kind]: types[kind] }, value)));
    const signed = async (kind, value) => sortSignatures(config, kind, value, await sign(kind, value));
    const pause = async paused => { const v = { paused, nonce: await vault.pauseNonce(), deadline: deadline() }; await (await vault.setPaused(paused, v.deadline, await signed('Pause', v))).wait(); };
    const release = async (v, s) => (await vault.release(v.burnId, v.recipient, v.nativeAmount, v.deadline, s ?? await signed('Release', v))).wait();
    const claim = (id = 'claim', amount = '50000000') => ({ burnId: hash(id), recipient: receiver, nativeAmount: amount, deadline: deadline() });
    const deposit = async amount => {
      await (await mock.approve(config.bridge, amount)).wait();
      const receipt = await (await vault.deposit(amount, '0x' + holder)).wait();
      const log = receipt.logs.find(l => { try { return vault.interface.parseLog(l)?.name === 'Deposited'; } catch { return false; } });
      return { transactionHash: receipt.hash, logIndex: log.index };
    };
    const confirmBsc = async () => { for (let i = 0; i < 14; i++) await rpc.request({ method: 'evm_mine', params: [] }); };
    db = journal(path.join(dir, 'bridge.sqlite'), config);
    await t.test('paused deployment, precision, dust, reserve cap and transfer-tax rejection', async () => {
      assert.equal(await vault.paused(), true); assert.equal(await vault.accountedReserve(), 0n); assert.equal(await vault.scale(), 10000000000n);
      await assert.rejects(vault.deposit(1000000000000000000n, '0x' + holder)); await pause(false);
      await assert.rejects(vault.deposit(1, '0x' + holder));
      await assert.rejects(vault.deposit(100000000000000000000n, '0x' + holder));
      await (await mock.setFee(true)).wait(); await assert.rejects(deposit(1000000000000000000n)); await (await mock.setFee(false)).wait();
    });
    await t.test('first deposit issues protocol pUSDT from zero supply and cannot be reminted', async () => {
      const ref = await deposit(1000000000000000000n);
      await assert.rejects(verifyDeposit(provider, vault, config, ref.transactionHash, ref.logIndex)); await confirmBsc();
      const evidence = await verifyDeposit(provider, vault, config, ref.transactionHash, ref.logIndex);
      assert.equal(evidence.amount, '100000000');
      const tx = await prepareMint(db, provider, vault, config, ref, path.join(dir, 'admin-keys.local.json'), 0);
      assert.equal(tx.TokenCommand.Amount, 100000000);
      assert.equal(tx.Inputs.length, 0);
      const file = path.join(dir, 'first-mint.json'); writeFileSync(file, JSON.stringify(tx));
      execFileSync(mono, [path.join(root, 'build/native/BridgeNativeRegression.exe'), 'confirm', dir, file], { timeout: 240000 });
      const repeated = await prepareMint(db, provider, vault, config, ref, 'missing', 0);
      assert.equal(repeated.Id, tx.Id);
    });
    await t.test('second deposit issues native token once; restart preserves exact signed transaction', async () => {
      const ref = await deposit(2000000000000000000n); await confirmBsc();
      await assert.rejects(verifyDeposit(provider, vault, { ...config, bridge: receiver }, ref.transactionHash, ref.logIndex));
      const tx = await prepareMint(db, provider, vault, config, ref, path.join(dir, 'admin-keys.local.json'), 0);
      assert.equal(tx.TokenCommand.Operation, 'mint'); assert.equal(tx.TokenCommand.Amount, 200000000); assert.equal(tx.TokenAuthorizations.length, 2);
      db.close(); db = journal(path.join(dir, 'bridge.sqlite'), config);
      assert.equal((await prepareMint(db, provider, vault, config, ref, 'missing', 0)).Id, tx.Id);
      const file = path.join(dir, 'mint.json'); writeFileSync(file, JSON.stringify(tx));
      execFileSync(mono, [path.join(root, 'build/native/BridgeNativeRegression.exe'), 'confirm', dir, file], { timeout: 240000 });
      assert.equal(await vault.accountedReserve(), 300000000n);
    });
    await t.test('native burn binds BSC receiver with owner signatures, policy and canonical confirmations', () => {
      console.log(execFileSync(mono, [path.join(root, 'build/native/BridgeNativeRegression.exe'), 'burn', dir, receiver.toLowerCase()], { encoding: 'utf8', timeout: 240000 }).trim());
      const evidence = native(config, 'verify', read(path.join(dir, 'binding.json'))); assert.equal(evidence.amount, '50000000');
    });
    await t.test('quorum rejects missing, repeated, foreign and cross-domain signatures', async () => {
      const v = claim();
      await assert.rejects(release(v, await sign('Release', v, [wallets[0]])));
      const s = (await sign('Release', v, [wallets[0]]))[0]; await assert.rejects(release(v, [s, s]));
      await assert.rejects(release(v, await sign('Release', v, wallets.slice(3, 5))));
      await assert.rejects(release(v, await sign('Release', v, wallets.slice(0, 2), { ...config, chainId: 97 })));
      await assert.rejects(release(v, await sign('Release', v, wallets.slice(0, 2), { ...config, bridge: receiver })));
      const signatures = await signed('Release', v); await assert.rejects(release({ ...v, nativeAmount: '1' }, signatures));
      await assert.rejects(release({ ...v, deadline: 1 }));
      await assert.rejects(release(claim('over-reserve', '400000000')));
      await assert.rejects(release(claim('daily-cap', '100000001')));
    });
    await t.test('canonical native burn releases exact USDT and cannot pay twice', async () => {
      const evidence = native(config, 'verify', read(path.join(dir, 'binding.json')));
      const v = { burnId: evidence.depositId, recipient: evidence.recipient, nativeAmount: evidence.amount, deadline: deadline() };
      await release(v); assert.equal(await mock.balanceOf(receiver), 500000000000000000n); assert.equal(await vault.accountedReserve(), 250000000n);
      await assert.rejects(release(v)); await assert.rejects(release({ ...v, recipient: wallets[5].address }));
    });
    await t.test('quorum pause and nonce prevent replay', async () => {
      const v = { paused: true, nonce: await vault.pauseNonce(), deadline: deadline() }, signatures = await signed('Pause', v);
      await (await vault.setPaused(true, v.deadline, signatures)).wait(); await assert.rejects(vault.setPaused(true, v.deadline, signatures));
      await assert.rejects(release(claim())); await assert.rejects(vault.deposit(10000000000n, '0x' + holder)); await pause(false);
    });
    await t.test('persistent decisions forbid conflicting recipients and journal reuse', () => {
      recordDecision(db, 'burn', { recipient: receiver, nativeAmount: '1' }); recordDecision(db, 'burn', { recipient: receiver, nativeAmount: '1' });
      assert.throws(() => recordDecision(db, 'burn', { recipient: receiver, nativeAmount: '2' }));
      assert.throws(() => journal(path.join(dir, 'bridge.sqlite'), { ...config, nativeToken: 'e'.repeat(64) }));
      assert.throws(() => validateConfig({ ...config, chainId: 56 }));
    });
    await t.test('unsafe constructor quorum and duplicate operators rejected', async () => {
      await assert.rejects(factory.deploy(config.asset, networkHash(config), '0x' + config.nativeToken, config.operators, 1, 10, 10));
      await assert.rejects(factory.deploy(config.asset, networkHash(config), '0x' + config.nativeToken, [wallets[0].address, wallets[0].address, wallets[1].address], 2, 10, 10));
    });
  } finally { db?.close(); await rpc.disconnect(); rmSync(dir, { recursive: true, force: true }); }
});

test('signed int64 JSON fields retain their exact digits', async () => {
  const { parseJson } = await import('../src/bridge.mjs');
  const original = '{"TimestampUtcTicks":639000000000000001,"Amount":100000000}';
  assert.equal(JSON.stringify(parseJson(original)), original);
});
