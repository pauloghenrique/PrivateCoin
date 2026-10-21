import { Wallet, JsonRpcProvider, ContractFactory, Contract, keccak256, verifyTypedData, getAddress } from 'ethers';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { root } from './paths.mjs';
import { parseJson, types, domain, networkHash, validateConfig, connect, journal, recordDecision, native, loadWallet, sortSignatures, prepareMint, resolveAssetConfig, signingTypes } from './bridge.mjs';
import { createNativeOperator } from './native-keystore.mjs';
import { propose, signProposal, complete, verifyProductionBurn, requireProduction, audit, reviewAsset, assetTypes, assetDomain, approvedAssetConfig, refreshProposal } from './production.mjs';
const read = filename => parseJson(readFileSync(filename, 'utf8'));
const output = value => console.log(JSON.stringify(value, (_, v) => typeof v === 'bigint' ? v.toString() : v, 2));
const deadlineCheck = value => { if (!Number.isSafeInteger(value) || value <= Date.now() / 1000 || value > Date.now() / 1000 + 600) throw Error('Use a common deadline within ten minutes.'); };
try {
  const [command, configFile, ...args] = process.argv.slice(2);
  if (command === 'create-native-operator') {
    output(createNativeOperator(configFile, process.env.NATIVE_KEYSTORE_PASSWORD || ''));
  } else if (command === 'create-operator') {
    if (!configFile || !process.env.BRIDGE_KEYSTORE_PASSWORD || process.env.BRIDGE_KEYSTORE_PASSWORD.length < 16) throw Error('create-operator <encrypted-keystore-file>; set BRIDGE_KEYSTORE_PASSWORD locally.');
    const wallet = Wallet.createRandom(); writeFileSync(configFile, await wallet.encrypt(process.env.BRIDGE_KEYSTORE_PASSWORD), { mode: 0o600, flag: 'wx' }); output({ address: wallet.address });
  } else {
    const config = validateConfig(read(configFile));
    if (command === 'refresh-native') {
      requireProduction(config); const db = journal(args[1], config);
      try { output(await refreshProposal(config, read(args[0]), db)); } finally { db.close(); }
    } else if (command === 'production-asset-config') {
      output(await approvedAssetConfig(config, read(args[0])));
    } else if (command === 'sign-asset') {
      const asset = read(args[0]), value = await reviewAsset(config, asset); deadlineCheck(value.deadline);
      const wallet = await loadWallet(args[1]);
      if (!config.operators.map(getAddress).includes(wallet.address)) throw Error('Not a factory operator.');
      const db = journal(args[2], config);
      try { recordDecision(db, 'asset:' + value.asset, { asset: value.asset, assetCodeHash: asset.assetCodeHash, cap: value.cap, dailyCap: value.dailyCap });
        output({ value, signature: await wallet.signTypedData(assetDomain(config), assetTypes, value) }); } finally { db.close(); }
    } else if (command === 'create-asset') {
      const asset = read(args[0]), value = await reviewAsset(config, asset), auth = read(args[1]); deadlineCheck(value.deadline);
      if (auth.some(item => JSON.stringify(item.value) !== JSON.stringify(value))) throw Error('Asset authorization differs from reviewed limits.');
      const signatures = auth.map(item => ({ signature: item.signature, signer: verifyTypedData(assetDomain(config), assetTypes, value, item.signature) }));
      signatures.sort((a,b) => BigInt(a.signer) < BigInt(b.signer) ? -1 : 1);
      if (signatures.length < config.threshold || new Set(signatures.map(item => item.signer)).size !== signatures.length || signatures.some(item => !config.operators.map(getAddress).includes(item.signer))) throw Error('Invalid asset quorum.');
      const { provider } = await connect(config), wallet = (await loadWallet(args[2])).connect(provider);
      const artifact = read(path.join(root, 'build/ProductionVaultFactory.json')), factory = new Contract(config.factory, artifact.abi, wallet);
      const receipt = await (await factory.createVault(value.asset, value.codeHash, value.cap, value.dailyCap, value.deadline, signatures.map(item => item.signature))).wait();
      output({ transactionHash: receipt.hash, vault: (await factory.vaults(value.asset)).toLowerCase() });
    } else if (command === 'audit') {
      const result = await audit(config); output(result); if (!result.backed) process.exitCode = 2;
    } else if (command === 'propose-native') {
      requireProduction(config); const db = journal(args[2], config);
      try { output(await propose(config, args[0], args[0] === 'mint' ? read(args[1]) : null, db)); } finally { db.close(); }
    } else if (command === 'sign-native') {
      requireProduction(config); const db = journal(args[2], config);
      try { output(await signProposal(config, read(args[0]), args[1], db)); } finally { db.close(); }
    } else if (command === 'complete-native') {
      requireProduction(config); output(await complete(config, read(args[0]), read(args[1])));
    } else if (command === 'execute-release' || command === 'cancel-release' || command === 'emergency-pause') {
      requireProduction(config); const wallet = await loadWallet(args[command === 'emergency-pause' ? 0 : 1]);
      const { contract } = await connect(config, wallet);
      if (command !== 'emergency-pause' && !/^0x[a-f0-9]{64}$/.test(args[0])) throw Error('Specify the burn ID.');
      const tx = command === 'execute-release' ? await contract.executeRelease(args[0]) : command === 'cancel-release' ? await contract.cancelRelease(args[0]) : await contract.emergencyPause();
      output({ transactionHash: (await tx.wait()).hash });
    } else if (command === 'deploy-factory') {
      const { provider } = await connect(config), wallet = (await loadWallet(args[0])).connect(provider);
      const artifact = read(path.join(root, config.mode === 'production' ? 'build/ProductionVaultFactory.json' : 'build/AssetVaultFactory.json'));
      const factory = await new ContractFactory(artifact.abi, artifact.bytecode, wallet).deploy(config.bridge);
      await factory.waitForDeployment(); output({ factory: (await factory.getAddress()).toLowerCase(), transactionHash: factory.deploymentTransaction().hash, factoryCodeHash: keccak256(await provider.getCode(await factory.getAddress())) });
    } else if (command === 'asset-config') {
      output(await resolveAssetConfig(config, args[0]));
    } else if (command === 'prepare-asset-mint') {
      const resolved = await resolveAssetConfig(config, args[0]), { provider, contract } = await connect(resolved);
      const fee = Number(args[4]); if (fee !== 0) throw Error('First asset issuance uses zero native fee.');
      const db = journal(args[2], resolved);
      try { output(await prepareMint(db, provider, contract, resolved, read(args[1]), args[3], fee)); } finally { db.close(); }
    } else if (command === 'deploy') {
      const provider = new JsonRpcProvider(config.rpcUrl);
      if (config.bridge !== null || (await provider.getNetwork()).chainId !== BigInt(config.chainId)) throw Error('Deployment requires the configured chain and bridge:null.');
      if (config.mode === 'production') {
        requireProduction(config);
        for (const url of config.rpcUrls) {
          const independent = new JsonRpcProvider(url);
          try { if ((await independent.getNetwork()).chainId !== BigInt(config.chainId) || keccak256(await independent.getCode(config.asset)) !== config.assetCodeHash) throw Error('Independent RPC source code/chain mismatch.'); }
          finally { await independent.destroy(); }
        }
      }
      const wallet = (await loadWallet(args[0])).connect(provider), artifact = read(path.join(root, config.mode === 'production' ? 'build/ProductionVault.json' : 'build/UsdtVault.json'));
      const vault = await new ContractFactory(artifact.abi, artifact.bytecode, wallet).deploy(config.asset, networkHash(config), '0x' + config.nativeToken, config.operators, config.threshold, config.reserveCap, config.dailyReleaseCap);
      await vault.waitForDeployment(); output({ bridge: (await vault.getAddress()).toLowerCase(), transactionHash: vault.deploymentTransaction().hash, vaultCodeHash: keccak256(await provider.getCode(await vault.getAddress())), paused: true });
    } else if (command === 'deposit') {
      const wallet = await loadWallet(args[2]), { contract } = await connect(config, wallet);
      if (!/^[1-9][0-9]*$/.test(args[0]) || !/^[a-f0-9]{64}$/.test(args[1])) throw Error('deposit <native-atomic-amount> <native-recipient> <evm-keystore>');
      const amount = BigInt(args[0]) * await contract.scale();
      const asset = new Contract(config.asset, ['function allowance(address,address) view returns(uint256)', 'function approve(address,uint256) returns(bool)'], contract.runner);
      if (await asset.allowance(wallet.address, config.bridge) < amount) {
        if (await asset.allowance(wallet.address, config.bridge) !== 0n) await (await asset.approve(config.bridge, 0)).wait();
        await (await asset.approve(config.bridge, amount)).wait();
      }
      const receipt = await (await contract.deposit(amount, '0x' + args[1])).wait();
      output({ transactionHash: receipt.hash, logIndex: receipt.logs.find(l => { try { return contract.interface.parseLog(l)?.name === 'Deposited'; } catch { return false; } }).index });
    } else if (command === 'configure-native') {
      await connect(config); output(native(config, 'configure', {}, args[0]));
    } else if (command === 'prepare-mint') {
      const { provider, contract } = await connect(config), db = journal(args[1], config), fee = Number(args[3]);
      if (!Number.isSafeInteger(fee) || fee < 0) throw Error('Specify the native fee in atomic units.');
      try { output(await prepareMint(db, provider, contract, config, read(args[0]), args[2], fee, native)); } finally { db.close(); }
    } else if (command === 'prepare-burn') {
      await connect(config);
      if (!/^[1-9][0-9]*$/.test(args[0]) || !Number.isSafeInteger(Number(args[2]))) throw Error('prepare-burn <atomic-amount> <holder-keys-file> <fee>');
      if (BigInt(args[0]) > BigInt(config.dailyReleaseCap)) throw Error('Split redemption into burns no larger than the daily release cap.');
      output(native(config, 'operation', { operation: 'burn', amount: args[0], recipient: null, fee: Number(args[2]), pending: [] }, args[1]));
    } else if (command === 'bind-burn' || command === 'verify-burn') {
      await connect(config); output(native(config, command === 'bind-burn' ? 'bind' : 'verify', read(args[0]), args[1]));
    } else if (command === 'sign-release') {
      const { contract } = await connect(config), evidence = config.mode === 'production' ? verifyProductionBurn(config, read(args[0])) : native(config, 'verify', read(args[0]));
      if (await contract.processedBurns(evidence.depositId)) throw Error('Burn already released.');
      const wallet = await loadWallet(args[1]);
      if (!config.operators.some(a => a.toLowerCase() === wallet.address.toLowerCase())) throw Error('Wallet is not an operator.');
      const deadline = Number(args[3]); deadlineCheck(deadline);
      const value = { burnId: evidence.depositId, recipient: evidence.recipient, nativeAmount: evidence.amount, deadline };
      if (config.mode === 'production') value.nonce = (await contract.releaseNonces(value.burnId)).toString();
      const db = journal(args[2], config);
      try {
        db.exec('BEGIN IMMEDIATE'); recordDecision(db, value.burnId, { recipient: value.recipient, nativeAmount: value.nativeAmount }); db.exec('COMMIT');
        output({ value, signature: await wallet.signTypedData(domain(config), signingTypes(config, 'Release'), value), evidence });
      } finally { db.close(); }
    } else if (command === 'release') {
      const { contract } = await connect(config, await loadWallet(args[2])), evidence = config.mode === 'production' ? verifyProductionBurn(config, read(args[0])) : native(config, 'verify', read(args[0]));
      const auth = read(args[1]), value = auth[0]?.value;
      if (!value || value.burnId !== evidence.depositId || value.recipient !== evidence.recipient || value.nativeAmount !== evidence.amount || auth.some(a => JSON.stringify(a.value) !== JSON.stringify(value))) throw Error('Release differs from canonical native burn.');
      const signatures = sortSignatures(config, 'Release', value, auth.map(a => a.signature));
      output({ transactionHash: (await (await contract.release(value.burnId, value.recipient, value.nativeAmount, value.deadline, signatures)).wait()).hash, stage: config.mode === 'production' ? 'queued-24h' : 'released' });
    } else if (command === 'sign-pause') {
      const { contract } = await connect(config), wallet = await loadWallet(args[0]);
      if (config.mode === 'production' && args[1] === 'false' && !(await audit(config)).backed) throw Error('Reserve audit failed; keep bridge paused.');
      if (!['true', 'false'].includes(args[1])) throw Error('Pause must be true or false.');
      const value = { paused: args[1] === 'true', nonce: (await contract.pauseNonce()).toString(), deadline: Number(args[2]) }; deadlineCheck(value.deadline);
      output({ value, signature: await wallet.signTypedData(domain(config), signingTypes(config, 'Pause'), value) });
    } else if (command === 'pause') {
      const { contract } = await connect(config, await loadWallet(args[1])), auth = read(args[0]), value = auth[0]?.value;
      if (!value || value.nonce !== (await contract.pauseNonce()).toString() || auth.some(a => JSON.stringify(a.value) !== JSON.stringify(value))) throw Error('Pause authorization mismatch.');
      const signatures = sortSignatures(config, 'Pause', value, auth.map(a => a.signature));
      output({ transactionHash: (await (await contract.setPaused(value.paused, value.deadline, signatures)).wait()).hash });
    } else throw Error('Commands: audit, sign-asset, create-asset, production-asset-config, refresh-native, create-native-operator, propose-native, sign-native, complete-native, execute-release, cancel-release, emergency-pause, create-operator, deploy, deploy-factory, asset-config, deposit, configure-native, prepare-mint, prepare-asset-mint, prepare-burn, bind-burn, verify-burn, sign-release, release, sign-pause, pause. See README.md.');
  }
} catch (error) { console.error(error.shortMessage || error.message); process.exitCode = 1; }
