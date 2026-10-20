import { generateKeyPairSync, createPublicKey, createPrivateKey, randomBytes, scryptSync, createCipheriv, createDecipheriv, sign, verify, createHash } from 'node:crypto';
import { readFileSync, writeFileSync, statSync } from 'node:fs';
const scrypt = (password, salt) => scryptSync(password, salt, 32, { N: 131072, r: 8, p: 1, maxmem: 256 * 1024 * 1024 });
export function publicXml(key) {
 const jwk=createPublicKey(key).export({format:'jwk'});
 return '<RSAKeyValue><Modulus>'+Buffer.from(jwk.n,'base64url').toString('base64')+'</Modulus><Exponent>'+Buffer.from(jwk.e,'base64url').toString('base64')+'</Exponent></RSAKeyValue>';
}
export const nativeAddress = xml => createHash('sha256').update(xml).digest('hex');
export function createNativeOperator(filename,password) {
 if(typeof password!=='string'||password.length<16)throw Error('Use a native keystore password with at least sixteen characters.');
 const {privateKey}=generateKeyPairSync('rsa',{modulusLength:3072,publicExponent:65537});
 const publicKey=publicXml(privateKey),salt=randomBytes(32),iv=randomBytes(12),key=scrypt(password,salt);
 const cipher=createCipheriv('aes-256-gcm',key,iv);
 const pem=privateKey.export({format:'der',type:'pkcs8'});
 try {
  const ciphertext=Buffer.concat([cipher.update(pem),cipher.final()]);
  writeFileSync(filename,JSON.stringify({version:1,kdf:'scrypt-N131072-r8-p1',salt:salt.toString('hex'),iv:iv.toString('hex'),tag:cipher.getAuthTag().toString('hex'),ciphertext:ciphertext.toString('hex'),publicKey}),{mode:0o600,flag:'wx'});
  return {address:nativeAddress(publicKey),publicKey};
 } finally {key.fill(0);pem.fill(0);}
}
export function loadNativeOperator(filename,password) {
 if(process.platform!=='win32'&&(statSync(filename).mode&0o077)!==0)throw Error('Native keystore must be accessible only to its operator (chmod 600).');
 const data=JSON.parse(readFileSync(filename,'utf8'));
 if(data.version!==1||data.kdf!=='scrypt-N131072-r8-p1'||!/^([a-f0-9]{2}){32}$/.test(data.salt)||!/^([a-f0-9]{2}){12}$/.test(data.iv)||!/^([a-f0-9]{2}){16}$/.test(data.tag)||typeof data.ciphertext!=='string'||data.ciphertext.length>32768||!/^[a-f0-9]+$/.test(data.ciphertext))throw Error('Invalid native keystore.');
 const key=scrypt(password,Buffer.from(data.salt,'hex'));let plaintext;
 try {
  const decipher=createDecipheriv('aes-256-gcm',key,Buffer.from(data.iv,'hex'));decipher.setAuthTag(Buffer.from(data.tag,'hex'));
  plaintext=Buffer.concat([decipher.update(Buffer.from(data.ciphertext,'hex')),decipher.final()]);
  const privateKey=createPrivateKey({key:plaintext,type:'pkcs8',format:'der'}),publicKey=publicXml(privateKey);
  if(publicKey!==data.publicKey)throw Error('Native keystore public key mismatch.');
  return {address:nativeAddress(publicKey),publicKey,sign:payload=>sign('RSA-SHA256',payload,privateKey).toString('base64')};
 } finally {key.fill(0);if(plaintext)plaintext.fill(0);}
}
export function verifyNative(publicKey,payload,signature) {
 const match=/^<RSAKeyValue><Modulus>([A-Za-z0-9+/=]+)<\/Modulus><Exponent>([A-Za-z0-9+/=]+)<\/Exponent><\/RSAKeyValue>$/.exec(publicKey);
 if(!match)return false;
 try {const key=createPublicKey({format:'jwk',key:{kty:'RSA',n:Buffer.from(match[1],'base64').toString('base64url'),e:Buffer.from(match[2],'base64').toString('base64url')}});
  return verify('RSA-SHA256',payload,key,Buffer.from(signature,'base64'));
 }catch{return false;}
}
