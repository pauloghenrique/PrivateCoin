import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { Trie } from '@ethereumjs/trie';
import { encodeRlp, keccak256, getBytes, hexlify, id, zeroPadValue, toBeHex } from 'ethers';
import { root } from '../src/paths.mjs';

const cli = path.join(root, 'bin/CSharp/Release/Povix.Bridge.exe');
const mono = process.env.POVIX_MONO || 'mono';
const vault = '0x' + '11'.repeat(20), recipient = '0x' + '22'.repeat(32);
const sender = '0x' + '33'.repeat(20), depositId = '0x' + '44'.repeat(32);
const word = n => zeroPadValue(toBeHex(n), 32);
const key = n => getBytes(encodeRlp(n === 0 ? '0x' : toBeHex(n)));
const clone = x => structuredClone(x);
function receipt({type = 2, status = '0x01', emitter = vault, topics, data, bloom = '0x' + '00'.repeat(256), gas = '0x5208'} = {}) {
  const encoded = encodeRlp([status, gas, bloom, [[emitter, topics || [id('Deposited(bytes32,address,bytes32,uint256,uint256)'), depositId, zeroPadValue(sender, 32), recipient], data || word(100000000n) + word(1000000000000000000n).slice(2)]]]);
  return type === 0 ? encoded : toBeHex(type, 1) + encoded.slice(2);
}
async function makeProof(value = receipt(), index = 0, populate = false) {
  const trie = new Trie();
  if (populate) for (let i = 0; i < 260; i++) await trie.put(key(i), getBytes(receipt({type:i % 3})));
  await trie.put(key(index), getBytes(value));
  const proof = await trie.createProof(key(index));
  assert.equal(hexlify(await trie.verifyProof(trie.root(), key(index), proof)), value);
  return {format:'povix-receipt-inclusion-v1', receiptsRoot:hexlify(trie.root()), receipt:value, proof:proof.map(hexlify), transactionIndex:index, receiptLogIndex:0, vault, assetDecimals:18};
}
function invoke(input, valid = true) {
  const dir = mkdtempSync(path.join(tmpdir(), 'povix-receipt-proof-'));
  try {
    const file = path.join(dir, 'proof.json'); writeFileSync(file, JSON.stringify(input));
    const result = spawnSync(mono, [cli, 'inspect-receipt-proof', file], {encoding:'utf8', timeout:20000});
    assert.ifError(result.error);
    if (!valid) { assert.equal(result.status, 1, result.stdout + result.stderr); assert.equal(result.stdout.trim(), ''); return result.stderr; }
    assert.equal(result.status, 0, result.stderr); return JSON.parse(result.stdout);
  } finally { rmSync(dir, {recursive:true, force:true}); }
}

test('C# receipt proof agrees with EthereumJS across receipt types and RLP index boundaries', async () => {
  for (const [index, type] of [[0,0],[1,1],[127,2],[128,2],[255,1],[256,0],[512,2]]) {
    const result = invoke(await makeProof(receipt({type}), index, true));
    assert.equal(result.inclusionVerified, true);
    assert.equal(result.consensusVerified, false);
    assert.equal(result.usableForMint, false);
    assert.equal(result.receiptType, type);
    assert.equal(result.nativeAmount, '100000000');
    assert.equal(result.assetAmount, '1000000000000000000');
    assert.equal(result.nativeRecipient, recipient);
    assert.equal(result.sender, sender);
  }
});
test('a fabricated self-consistent root NEVER authorizes mint or claims consensus', async () => {
  const result = invoke(await makeProof());
  assert.equal(result.consensusVerified, false); assert.equal(result.usableForMint, false);
  assert.match(result.warning, /unauthenticated/);
});
test('reject root, receipt, path, node, order, and receipt-local event-index tampering', async () => {
  const p = await makeProof(receipt(), 128, true);
  for (const edit of [
    x => x.receiptsRoot = '0x' + '00'.repeat(32),
    x => x.receipt = x.receipt.slice(0,-2) + 'ff',
    x => x.transactionIndex = 129,
    x => x.proof.pop(),
    x => x.proof.push(x.proof[0]),
    x => x.proof.reverse(),
    x => x.proof[1] = x.proof[1].slice(0,-2) + 'ff',
    x => x.receiptLogIndex = 1,
    x => x.vault = '0x' + '99'.repeat(20),
    x => x.assetDecimals = 17,
    x => x.assetDecimals = 19,
    x => x.transactionIndex = '128',
    x => x.transactionIndex = -1,
    x => x.transactionIndex = 2147483648,
    x => x.receiptsRoot = x.receiptsRoot.toUpperCase(),
    x => x.format = 'unknown'
  ]) { const changed = clone(p); edit(changed); invoke(changed, false); }
});
test('reject bad events even if proven against a matching root', async () => {
  const goodTopics = [id('Deposited(bytes32,address,bytes32,uint256,uint256)'), depositId, zeroPadValue(sender,32), recipient];
  const badTopics = [...goodTopics]; badTopics[2] = '0x01' + '00'.repeat(31);
  for (const bad of [
    receipt({status:'0x'}), receipt({type:3}), receipt({emitter:sender}),
    receipt({topics:[id('OtherEvent()'), ...goodTopics.slice(1)]}),
    receipt({topics:goodTopics.slice(0,3)}), receipt({topics:badTopics}),
    receipt({topics:[goodTopics[0], '0x'+'00'.repeat(32), goodTopics[2], recipient]}),
    receipt({topics:[...goodTopics.slice(0,3), '0x'+'00'.repeat(32)]}),
    receipt({data:word(0n)+word(0n).slice(2)}),
    receipt({data:word(100000000n)+word(1n).slice(2)}),
    receipt({data:word(2n**63n)+word((2n**63n)*10n**10n).slice(2)}),
    receipt({bloom:'0x00'}), receipt({gas:'0x005208'})
  ]) invoke(await makeProof(bad), false);
});
function leafProof(encodedNode, value = receipt()) {
  return {format:'povix-receipt-inclusion-v1', receiptsRoot:keccak256(encodedNode), proof:[encodedNode], receipt:value, transactionIndex:0, receiptLogIndex:0, vault, assetDecimals:18};
}
test('reject noncanonical RLP, invalid paths, truncated data and oversized proofs', async () => {
  const r = receipt();
  for (const node of [
    encodeRlp(['0x2081', r]), // wrong key
    encodeRlp(['0x4080', r]), // reserved HP flags
    encodeRlp(['0x2180', r]), // nonzero even-path padding
    encodeRlp(['0x00', r]), // empty extension
    encodeRlp(['0x2080', r, '0x']), // arity
    encodeRlp(['0x2080', r])+'00', // trailing bytes
    encodeRlp(['0x2080', r]).slice(0,-2), // truncation
    '0xf90001c0', // long form for a short list
    '0xf800', // zero long length
    '0xc3812080' // noncanonical short scalar
  ]) invoke(leafProof(node), false);
  const p = await makeProof();
  for (const edit of [x => x.proof = [], x => x.proof = Array(65).fill('0x'), x => x.proof = ['0x'+'00'.repeat(65537)], x => x.receipt = '0x'+'00'.repeat(262145)]) {
    const bad=clone(p); edit(bad); invoke(bad, false);
  }
});
test('CLI enforces file size and rejects duplicate JSON properties', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'povix-receipt-json-'));
  try {
    const file = path.join(dir, 'proof.json'), p = await makeProof();
    for (const text of [' '.repeat(2*1024*1024+1), JSON.stringify(p).replace('"format":', '"format":"other","format":')]) {
      writeFileSync(file,text);
      const r = spawnSync(mono,[cli,'inspect-receipt-proof',file],{encoding:'utf8',timeout:20000});
      assert.ifError(r.error); assert.equal(r.status,1); assert.equal(r.stdout.trim(),'');
    }
  } finally { rmSync(dir,{recursive:true,force:true}); }
});
