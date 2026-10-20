import { JsonRpcProvider, Contract, keccak256 } from 'ethers';
import { connect, validateConfig, verifyDeposit, native, recordDecision, parseJson, rpcProvider, representationId, nativeSymbol } from './bridge.mjs';
import { loadNativeOperator, nativeAddress, verifyNative } from './native-keystore.mjs';
const canonical = value => JSON.stringify(value);
export function requireProduction(config) {
 validateConfig(config);
 if(config.mode!=='production')throw Error('Explicit production configuration required.');
 if(!Array.isArray(config.nativeSnapshots)||config.nativeSnapshots.length<2||new Set(config.nativeSnapshots).size!==config.nativeSnapshots.length||
    !config.nativeSnapshots.includes(config.nativeSnapshot)||config.nativeSnapshots.some(p=>typeof p!=='string'||!p))throw Error('Configure snapshots from two independently operated POVIX nodes.');
 return config;
}
export async function verifyProductionDeposit(config,reference,dependencies={}) {
 requireProduction(config);
 const providers=config.rpcUrls.map(url=>dependencies.provider?dependencies.provider(url):rpcProvider(url));
 try {
  const results=await Promise.allSettled(providers.map(async(provider,i)=>{
   const {contract}=await (dependencies.connect||connect)({...config,rpcUrl:config.rpcUrls[i]},null,provider);
   return (dependencies.verifyDeposit||verifyDeposit)(provider,contract,config,reference.transactionHash,reference.logIndex);
  }));
  const rejected=results.find(result=>result.status==='rejected');
  if(rejected)throw rejected.reason;
  const deposits=results.map(result=>result.value);
  if(deposits.some(value=>canonical(value)!==canonical(deposits[0])))throw Error('Independent BNB RPCs disagree on deposit evidence. Stop issuance.');
  return deposits[0];
 }finally{await Promise.all(providers.map(provider=>provider.destroy()));}
}
export function reviewOnNativeNodes(config,proposal,command='production-review',nativeCall=native) {
 const results=config.nativeSnapshots.map(snapshot=>nativeCall({...config,nativeSnapshot:snapshot},command,proposal));
 if(results.some(value=>canonical(value)!==canonical(results[0])))throw Error('Independent POVIX nodes disagree. Stop signing.');
 return results[0];
}
export async function propose(config,kind,reference,db) {
 requireProduction(config);
 const deposit=kind==='mint'?await verifyProductionDeposit(config,reference):null;
 if(!['mint','register','pause','resume'].includes(kind))throw Error('Expected mint, register, pause or resume.');
 if(kind!=='mint') {const {provider}=await connect(config);await provider.destroy();}
 db.exec('CREATE TABLE IF NOT EXISTS production_proposals (id TEXT PRIMARY KEY, proposal TEXT NOT NULL)');
 const controlDraft=['pause','resume'].includes(kind)?native(config,'production-draft',{kind}):null;
 const id=kind==='mint'?deposit.depositId:kind+':'+config.nativeToken+(controlDraft?':'+controlDraft.TokenCommand.Revision:'');
 db.exec('BEGIN IMMEDIATE');
 try {
  const old=db.prepare('SELECT proposal FROM production_proposals WHERE id=?').get(id);
  if(old){db.exec('COMMIT');return parseJson(old.proposal);}
  const proposal={kind,deposit,reference:kind==='mint'?reference:null};
  proposal.draft=controlDraft||native(config,'production-draft',proposal);
  reviewOnNativeNodes(config,proposal);
  db.prepare('INSERT INTO production_proposals VALUES (?,?)').run(id,canonical(proposal));db.exec('COMMIT');return proposal;
 }catch(error){db.exec('ROLLBACK');throw error;}
}
export async function review(config,proposal) {
 requireProduction(config);
 if(proposal.kind==='mint') {
  const evidence=await verifyProductionDeposit(config,proposal.reference);
  if(canonical(evidence)!==canonical(proposal.deposit))throw Error('Proposal differs from independently verified deposit.');
 }else if(['register','pause','resume'].includes(proposal.kind)) {const {provider}=await connect(config);await provider.destroy();}
 else throw Error('Unknown proposal kind.');
 return reviewOnNativeNodes(config,proposal);
}
export async function signProposal(config,proposal,keystore,db) {
 const payload=Buffer.from(await review(config,proposal),'base64');
 const operator=loadNativeOperator(keystore,process.env.NATIVE_KEYSTORE_PASSWORD||'');
 if(!proposal.draft.TokenAuthorizations.some(proof=>proof.PublicKey===operator.publicKey))throw Error('This operator is not in the selected signing quorum.');
 const decision=proposal.kind==='mint'?proposal.deposit:{token:proposal.draft.Token||null,command:proposal.draft.TokenCommand};
 // Each operator owns a separate durable journal. Never silently replace conflicting facts.
 db.exec('BEGIN IMMEDIATE');
 try {recordDecision(db,proposal.kind+':'+(proposal.deposit?.depositId||(config.nativeToken+':'+proposal.draft.TokenCommand.Revision)),decision);db.exec('COMMIT');}
 catch(error){db.exec('ROLLBACK');throw error;}
 return {address:operator.address,signature:operator.sign(payload),payloadHash:keccak256(payload)};
}
export async function complete(config,proposal,signatures) {
 const payload=Buffer.from(await review(config,proposal),'base64');
 const addresses=new Set();
 for(const item of signatures){
  const proof=proposal.draft.TokenAuthorizations.find(proof=>nativeAddress(proof.PublicKey)===item.address);
  if(!proof||addresses.has(item.address)||item.payloadHash!==keccak256(payload)||!verifyNative(proof.PublicKey,payload,item.signature))throw Error('Invalid, duplicate or mismatched operator signature.');
  addresses.add(item.address);
 }
 if(addresses.size!==proposal.draft.TokenAuthorizations.length)throw Error('Collect all signatures of the chosen quorum.');
 return reviewOnNativeNodes(config,{...proposal,signatures},'production-complete');
}
export function verifyProductionBurn(config,binding) {
 requireProduction(config);
 const results=config.nativeSnapshots.map(snapshot=>native({...config,nativeSnapshot:snapshot},'verify',binding));
 const facts=value=>canonical({depositId:value.depositId,recipient:value.recipient,amount:value.amount,blockHash:value.blockHash});
 if(results.some(value=>facts(value)!==facts(results[0])))throw Error('Independent native nodes disagree about the burn.');
 return results[0];
}

export async function audit(config) {
 requireProduction(config);
 const nativeState=reviewOnNativeNodes(config,{},'production-status');
 const observations=await Promise.all(config.rpcUrls.map(async rpcUrl=>{
  const {provider,contract}=await connect({...config,rpcUrl});
  try {
   const token=new Contract(config.asset,['function balanceOf(address) view returns(uint256)'],provider);
   const [balance,reserve,scale,reserved,paused]=await Promise.all([token.balanceOf(config.bridge),contract.accountedReserve(),contract.scale(),contract.reservedForRelease(),contract.paused()]);
   return {balance:balance.toString(),reserve:reserve.toString(),scale:scale.toString(),reserved:reserved.toString(),paused};
  }finally{await provider.destroy();}
 }));
 if(observations.some(value=>canonical(value)!==canonical(observations[0])))throw Error('BNB providers disagree on reserve state. Investigate before operating.');
 const state=observations[0];
 const backed=BigInt(typeof nativeState.supply === 'object' ? nativeState.supply.rawJSON : nativeState.supply)<=BigInt(state.reserve)&&BigInt(state.balance)>=BigInt(state.reserve)*BigInt(state.scale)&&BigInt(state.reserved)<=BigInt(state.reserve);
 return {backed,native:nativeState,bnb:state,observedAt:new Date().toISOString()};
}

export const assetTypes={Asset:[{name:'asset',type:'address'},{name:'codeHash',type:'bytes32'},{name:'cap',type:'uint256'},{name:'dailyCap',type:'uint256'},{name:'deadline',type:'uint256'}]};
export const assetDomain=config=>({name:'POVIX Asset Factory',version:'2',chainId:config.chainId,verifyingContract:config.factory});
export async function reviewAsset(config,asset) {
 requireProduction(config);
 if(!config.factory||!/^0x[a-f0-9]{40}$/.test(asset.asset)||!/^0x[a-f0-9]{64}$/.test(asset.assetCodeHash)||
    !/^[1-9][0-9]*$/.test(asset.cap)||!/^[1-9][0-9]*$/.test(asset.dailyCap)||BigInt(asset.cap)>9223372036854775807n||BigInt(asset.dailyCap)>BigInt(asset.cap))throw Error('Specify the reviewed source contract, code hash and per-asset atomic limits.');
 const results=await Promise.allSettled(config.rpcUrls.map(async rpcUrl=>{
  const {provider,contract}=await connect({...config,rpcUrl});
  try{if(keccak256(await provider.getCode(asset.asset))!==asset.assetCodeHash)throw Error('Reviewed source bytecode mismatch.');
   const token=new Contract(asset.asset,['function decimals() view returns(uint8)'],provider);const decimals=Number(await token.decimals());
   if(decimals>18)throw Error('Unsupported token precision.');return decimals;
  }finally{await provider.destroy();}
 }));
 const rejected=results.find(r=>r.status==='rejected');if(rejected)throw rejected.reason;
 if(results.some(r=>r.value!==results[0].value))throw Error('Providers disagree about asset precision.');
 return {asset:asset.asset,codeHash:asset.assetCodeHash,cap:asset.cap,dailyCap:asset.dailyCap,deadline:asset.deadline};
}

export async function approvedAssetConfig(base,asset) {
 await reviewAsset(base,asset);
 const {provider}=await connect(base);
 try {
  const factory=new Contract(base.factory,['function vaults(address) view returns(address)','function representation(address) view returns(bytes32)'],provider);
  const vault=(await factory.vaults(asset.asset)).toLowerCase();if(/^0x0{40}$/.test(vault))throw Error('Asset has no quorum-approved vault.');
  const token=new Contract(asset.asset,['function decimals() view returns(uint8)','function symbol() view returns(string)'],provider);
  const nativeDecimals=Math.min(8,Number(await token.decimals()));let symbol;try{symbol=await token.symbol();}catch{symbol='TOKEN';}
  const resolved={...base,asset:asset.asset,assetCodeHash:asset.assetCodeHash,bridge:vault,vaultCodeHash:keccak256(await provider.getCode(vault)),nativeToken:representationId(base.chainId,asset.asset),nativeDecimals,nativeSymbol:nativeSymbol(symbol),reserveCap:asset.cap,dailyReleaseCap:asset.dailyCap};
  if(await factory.representation(asset.asset)!=='0x'+resolved.nativeToken)throw Error('Wrong native representation.');
  for(const rpcUrl of base.rpcUrls){const checked=await connect({...resolved,rpcUrl});await checked.provider.destroy();}
  return resolved;
 }finally{await provider.destroy();}
}

export async function refreshProposal(config,proposal,db) {
 requireProduction(config);
 if(!['mint','register','pause','resume'].includes(proposal.kind))throw Error('Unknown proposal kind.');
 if(proposal.kind==='mint') {
  const evidence=await verifyProductionDeposit(config,proposal.reference);
  if(canonical(evidence)!==canonical(proposal.deposit))throw Error('Deposit facts changed; stop and investigate before refreshing.');
 }else {const checked=await connect(config);await checked.provider.destroy();}
 const key=p=>p.kind==='mint'?p.deposit.depositId:p.kind+':'+config.nativeToken+(['pause','resume'].includes(p.kind)?':'+p.draft.TokenCommand.Revision:'');
 const next={...proposal,draft:native(config,'production-draft',proposal)};delete next.signatures;
 reviewOnNativeNodes(config,next);
 db.exec('CREATE TABLE IF NOT EXISTS production_proposals (id TEXT PRIMARY KEY, proposal TEXT NOT NULL); CREATE TABLE IF NOT EXISTS proposal_history (hash TEXT PRIMARY KEY, proposal TEXT NOT NULL, replacedAt TEXT NOT NULL); BEGIN IMMEDIATE;');
 try {
  const old=db.prepare('SELECT proposal FROM production_proposals WHERE id=?').get(key(proposal));
  if(!old||old.proposal!==canonical(proposal))throw Error('Refresh requires the exact proposal stored by this coordinator.');
  db.prepare('INSERT OR IGNORE INTO proposal_history VALUES (?,?,?)').run(keccak256(Buffer.from(old.proposal)),old.proposal,new Date().toISOString());
  db.prepare('INSERT OR REPLACE INTO production_proposals VALUES (?,?)').run(key(next),canonical(next));db.exec('COMMIT');return next;
 }catch(error){db.exec('ROLLBACK');throw error;}
}
