import test from 'node:test';
import assert from 'node:assert/strict';
import { Interface, keccak256, toUtf8Bytes } from 'ethers';
import { readFileSync } from 'node:fs';
import { verifyDeposit, journal, prepareMint } from '../src/bridge.mjs';

const hash = text => keccak256(toUtf8Bytes(text));
const config = { chainId: 97, bridge: '0x' + '1'.repeat(40), bscConfirmations: 15 };
const abi = JSON.parse(readFileSync(new URL('../build/UsdtVault.json', import.meta.url))).abi;
const iface = new Interface(abi);
const encoded = iface.encodeEventLog(iface.getEvent('Deposited'), [hash('deposit'), '0x' + '2'.repeat(40), '0x' + '3'.repeat(64), 100n, 1000000000000n]);
const receipt = { status: 1, blockNumber: 10, blockHash: hash('block'), logs: [{ index: 0, address: config.bridge, ...encoded }] };
const provider = { getNetwork: async () => ({ chainId: 97n }), getTransactionReceipt: async () => receipt, getBlockNumber: async () => 24, getBlock: async () => ({ hash: hash('block') }) };
const contract = { interface: iface, scale: async () => 10000000000n };

test('deposit evidence rejects failed, orphaned, wrong-chain and insufficiently confirmed receipts', async () => {
  const verify = p => verifyDeposit(p, contract, config, hash('transaction'), 0);
  assert.equal((await verify(provider)).amount, '100');
  await assert.rejects(verify({ ...provider, getTransactionReceipt: async () => ({ ...receipt, status: 0 }) }));
  await assert.rejects(verify({ ...provider, getBlock: async () => ({ hash: hash('reorg') }) }));
  await assert.rejects(verify({ ...provider, getNetwork: async () => ({ chainId: 56n }) }));
  await assert.rejects(verify({ ...provider, getBlockNumber: async () => 23 }));
  await assert.rejects(verify({ ...provider, getTransactionReceipt: async () => ({ ...receipt, logs: [{ ...receipt.logs[0], address: '0x' + '4'.repeat(40) }] }) }));
});

test('failed native preparation rolls back; retry issues one stored transaction', async () => {
  const db = journal(':memory:'); const ref = { transactionHash: hash('transaction'), logIndex: 0 }; let calls = 0;
  try {
    await assert.rejects(prepareMint(db, provider, contract, config, ref, null, 1, () => { throw Error('No quorum'); }));
    assert.equal(db.prepare('SELECT COUNT(*) AS n FROM mints').get().n, 0);
    const native = () => { calls++; return { Id: 'signed-native-transaction' }; };
    assert.equal((await prepareMint(db, provider, contract, config, ref, null, 1, native)).Id, 'signed-native-transaction');
    await prepareMint(db, provider, contract, config, ref, null, 1, native);
    assert.equal(calls, 1); assert.equal(db.prepare('SELECT COUNT(*) AS n FROM mints').get().n, 1);
  } finally { db.close(); }
});
