import { Wallet, JsonRpcProvider, ContractFactory, Contract } from 'ethers';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { root } from './compile.mjs';
import { parseJson, types, domain, networkHash, validateConfig, connect, journal, recordDecision, native, loadWallet, sortSignatures, prepareMint } from './bridge.mjs';
const read = filename => parseJson(readFileSync(filename, 'utf8'));
const output = value => console.log(JSON.stringify(value, (_, v) => typeof v === 'bigint' ? v.toString() : v, 2));
const deadlineCheck = value => { if (!Number.isSafeInteger(value) || value <= Date.now() / 1000 || value > Date.now() / 1000 + 600) throw Error('Use a common deadline within ten minutes.'); };
try {
  const [command, configFile, ...args] = process.argv.slice(2);
  if (command === 'create-operator') {
    if (!configFile || !process.env.BRIDGE_KEYSTORE_PASSWORD) throw Error('create-operator <encrypted-keystore-file>; set BRIDGE_KEYSTORE_PASSWORD locally.');
    const wallet = Wallet.createRandom(); writeFileSync(configFile, await wallet.encrypt(process.env.BRIDGE_KEYSTORE_PASSWORD), { mode: 0o600, flag: 'wx' }); output({ address: wallet.address });
  } else {
    const config = validateConfig(read(configFile));
    if (command === 'deploy') {
      const provider = new JsonRpcProvider(config.rpcUrl);
      if (config.bridge !== null || (await provider.getNetwork()).chainId !== BigInt(config.chainId)) throw Error('Deployment requires test chain and bridge:null.');
      const wallet = (await loadWallet(args[0])).connect(provider), artifact = read(path.join(root, 'build/UsdtVault.json'));
      const vault = await new ContractFactory(artifact.abi, artifact.bytecode, wallet).deploy(config.asset, networkHash(config), '0x' + config.nativeToken, config.operators, config.threshold, config.reserveCap, config.dailyReleaseCap);
      await vault.waitForDeployment(); output({ bridge: (await vault.getAddress()).toLowerCase(), transactionHash: vault.deploymentTransaction().hash, paused: true });
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
      output(native(config, 'operation', { operation: 'burn', amount: args[0], recipient: null, fee: Number(args[2]), pending: [] }, args[1]));
    } else if (command === 'bind-burn' || command === 'verify-burn') {
      await connect(config); output(native(config, command === 'bind-burn' ? 'bind' : 'verify', read(args[0]), args[1]));
    } else if (command === 'sign-release') {
      const { contract } = await connect(config), evidence = native(config, 'verify', read(args[0]));
      if (await contract.processedBurns(evidence.depositId)) throw Error('Burn already released.');
      const wallet = await loadWallet(args[1]);
      if (!config.operators.some(a => a.toLowerCase() === wallet.address.toLowerCase())) throw Error('Wallet is not an operator.');
      const deadline = Number(args[3]); deadlineCheck(deadline);
      const value = { burnId: evidence.depositId, recipient: evidence.recipient, nativeAmount: evidence.amount, deadline };
      const db = journal(args[2], config);
      try {
        db.exec('BEGIN IMMEDIATE'); recordDecision(db, value.burnId, { recipient: value.recipient, nativeAmount: value.nativeAmount }); db.exec('COMMIT');
        output({ value, signature: await wallet.signTypedData(domain(config), { Release: types.Release }, value), evidence });
      } finally { db.close(); }
    } else if (command === 'release') {
      const { contract } = await connect(config, await loadWallet(args[2])), evidence = native(config, 'verify', read(args[0]));
      const auth = read(args[1]), value = auth[0]?.value;
      if (!value || value.burnId !== evidence.depositId || value.recipient !== evidence.recipient || value.nativeAmount !== evidence.amount || auth.some(a => JSON.stringify(a.value) !== JSON.stringify(value))) throw Error('Release differs from canonical native burn.');
      const signatures = sortSignatures(config, 'Release', value, auth.map(a => a.signature));
      output({ transactionHash: (await (await contract.release(value.burnId, value.recipient, value.nativeAmount, value.deadline, signatures)).wait()).hash });
    } else if (command === 'sign-pause') {
      const { contract } = await connect(config), wallet = await loadWallet(args[0]);
      if (!['true', 'false'].includes(args[1])) throw Error('Pause must be true or false.');
      const value = { paused: args[1] === 'true', nonce: (await contract.pauseNonce()).toString(), deadline: Number(args[2]) }; deadlineCheck(value.deadline);
      output({ value, signature: await wallet.signTypedData(domain(config), { Pause: types.Pause }, value) });
    } else if (command === 'pause') {
      const { contract } = await connect(config, await loadWallet(args[1])), auth = read(args[0]), value = auth[0]?.value;
      if (!value || value.nonce !== (await contract.pauseNonce()).toString() || auth.some(a => JSON.stringify(a.value) !== JSON.stringify(value))) throw Error('Pause authorization mismatch.');
      const signatures = sortSignatures(config, 'Pause', value, auth.map(a => a.signature));
      output({ transactionHash: (await (await contract.setPaused(value.paused, value.deadline, signatures)).wait()).hash });
    } else throw Error('Commands: create-operator, deploy, deposit, configure-native, prepare-mint, prepare-burn, bind-burn, verify-burn, sign-release, release, sign-pause, pause. See README.md.');
  }
} catch (error) { console.error(error.shortMessage || error.message); process.exitCode = 1; }
