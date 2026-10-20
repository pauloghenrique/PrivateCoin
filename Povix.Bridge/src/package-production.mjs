import { mkdirSync, readFileSync, copyFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { root } from './paths.mjs';
const output=path.resolve(process.argv[2]||path.join(root,'../work/bridge-production-runtime'));
const meta=JSON.parse(readFileSync(path.join(root,'package.json'),'utf8'));
const runtime=JSON.parse(readFileSync(path.join(root,'deploy/runtime/package.json'),'utf8'));
if(runtime.dependencies.ethers!==meta.dependencies.ethers||runtime.version!==meta.version)throw Error('Runtime package version differs from reviewed source.');
const files=['deploy/runtime/package.json','deploy/runtime/package-lock.json','production.config.example.json','PRODUCTION.md',
 ...['cli.mjs','bridge.mjs','production.mjs','native-keystore.mjs','paths.mjs'].map(f=>'src/'+f),
 'build/ProductionVault.json','build/ProductionVaultFactory.json','build/native/BridgeNative.exe','build/native/PrivateCoin.Core.dll'];
// Validate every input before creating the destination. Never overwrite a previous release.
const data=files.map(file=>({file,bytes:readFileSync(path.join(root,file))}));
mkdirSync(output,{mode:0o700});const manifest={version:meta.version,candidate:true,files:{}};
for(const entry of data){const relative=entry.file.startsWith('deploy/runtime/')?path.basename(entry.file):entry.file;
 const destination=path.join(output,relative);mkdirSync(path.dirname(destination),{recursive:true,mode:0o700});
 copyFileSync(path.join(root,entry.file),destination);manifest.files[relative]=createHash('sha256').update(entry.bytes).digest('hex');}
writeFileSync(path.join(output,'manifest.sha256.json'),JSON.stringify(manifest,null,2)+'\n');
console.log('Production runtime bundle created: '+output);
