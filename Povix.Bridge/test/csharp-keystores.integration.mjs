import test from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { root } from '../src/paths.mjs';
import { createNativeOperator, loadNativeOperator, verifyNative } from '../src/native-keystore.mjs';
const invoke=promisify(execFile),mono=process.env.POVIX_MONO||'mono';
test('native keystores and RSA signatures interoperate in both C# and JavaScript directions',{timeout:180000},async()=>{
 const dir=mkdtempSync(path.join(tmpdir(),'povix-csharp-keys-')),password='test-only-cross-runtime-native-keystore',env={...process.env,NATIVE_KEYSTORE_PASSWORD:password};
 const cli=path.join(root,'bin/CSharp/Release/Povix.Bridge.exe'),helper=path.join(root,'bin/Tests/Release/CSharpRegression.exe');
 try {
  const first=path.join(dir,'csharp.local.json'),second=path.join(dir,'node.local.json'),payload=Buffer.from('povix-cross-runtime-signature-test');
  const created=JSON.parse((await invoke(mono,[cli,'create-native-operator',first],{encoding:'utf8',timeout:120000,env})).stdout);
  const fromCsharp=loadNativeOperator(first,password);assert.equal(fromCsharp.address,created.address);assert.equal(fromCsharp.publicKey,created.publicKey);
  const fromNode=createNativeOperator(second,password);
  for(const [file,expected] of [[first,created],[second,fromNode]]) {
   const result=JSON.parse((await invoke(mono,[helper,'native-sign',file,payload.toString('base64')],{encoding:'utf8',timeout:120000,env})).stdout);
   assert.equal(result.publicKey,expected.publicKey);assert.equal(result.address,expected.address);
   assert.equal(verifyNative(result.publicKey,payload,result.signature),true);assert.equal(verifyNative(result.publicKey,Buffer.from('tampered'),result.signature),false);
  }
  await assert.rejects(invoke(mono,[helper,'native-sign',first,payload.toString('base64')],{encoding:'utf8',timeout:120000,env:{...env,NATIVE_KEYSTORE_PASSWORD:'wrong-password'}}));
 } finally {rmSync(dir,{recursive:true,force:true});}
});
