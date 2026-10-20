import solc from 'solc';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
export const root = fileURLToPath(new URL('../', import.meta.url));
export function compile(source = "contracts/UsdtVault.sol", name = "UsdtVault") {
  const input = { language: 'Solidity', sources: { [name + '.sol']: { content: readFileSync(path.join(root, source), 'utf8') } }, settings: { optimizer: { enabled: true, runs: 200 }, evmVersion: 'shanghai', outputSelection: { '*': { '*': ['abi', 'evm.bytecode.object'] } } } };
  const output = JSON.parse(solc.compile(JSON.stringify(input), { import: name => {
    try { return { contents: readFileSync(path.join(root, 'node_modules', name), 'utf8') }; }
    catch { return { error: `Import unavailable: ${name}` }; }
  } }));
  const errors = (output.errors ?? []).filter(e => e.severity === 'error');
  if (errors.length) throw new Error(errors.map(e => e.formattedMessage).join('\n'));
  const c = output.contracts[name + '.sol'][name];
  return { compiler: solc.version(), abi: c.abi, bytecode: '0x' + c.evm.bytecode.object };
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  mkdirSync(path.join(root, 'build'), { recursive: true });
  writeFileSync(path.join(root, 'build/UsdtVault.json'), JSON.stringify(compile(), null, 2));
  console.log('Compiled UsdtVault (8 decimals; test networks only).');
}
