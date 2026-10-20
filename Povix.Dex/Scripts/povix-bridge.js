/* BEP-20 deposits use an explicitly configured chain and pinned production contracts. Native minting remains the federation's responsibility. */
(function () {
    "use strict";
    const protocolToken = "c834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d";
    let precision = 8, selectedSymbol = "pUSDT", assetSymbol = "USDT";
    const vaultAbi = ["function bridgeVersion() view returns(uint256)", "function releaseDelay() view returns(uint256)","function asset() view returns(address)", "function nativeNetwork() view returns(bytes32)",
        "function nativeToken() view returns(bytes32)", "function scale() view returns(uint256)",
        "function paused() view returns(bool)", "function accountedReserve() view returns(uint256)",
        "function reserveCap() view returns(uint256)", "function deposit(uint256,bytes32)",
        "event Deposited(bytes32 indexed depositId,address indexed sender,bytes32 indexed nativeRecipient,uint256 nativeAmount,uint256 assetAmount)"];
    const tokenAbi = ["function decimals() view returns(uint8)", "function balanceOf(address) view returns(uint256)",
        "function allowance(address,address) view returns(uint256)", "function approve(address,uint256) returns(bool)"];
    function parseAmount(value, decimals = 8) {
        const text = value.trim().replace(",", ".");
        if (!Number.isInteger(decimals) || decimals < 0 || decimals > 8 || !(new RegExp("^\\d{1,20}" + (decimals ? "(\\.\\d{1," + decimals + "})?" : "") + "$" )).test(text)) throw new Error("Informe um valor positivo com a precisão permitida pelo token, sem separador de milhar.");
        const parts = text.split(".");
        const amount = BigInt(parts[0]) * 10n ** BigInt(decimals) + BigInt((parts[1] || "").padEnd(decimals, "0") || "0");
        if (amount <= 0n || amount > 9223372036854775807n) throw new Error("Valor fora do limite da ponte.");
        return amount;
    }
    function recipient(value) {
        if (!/^[a-fA-F0-9]{64}$/.test(value) || /^0+$/.test(value)) throw new Error("Informe um endereço público POVIX válido com 64 caracteres.");
        return "0x" + value.toLowerCase();
    }
    window.PovixBridge = Object.freeze({ parseAmount, recipient });
    const app = document.getElementById("bridge-app");
    if (!app) return;
    const el = id => document.getElementById("bridge-" + id);
    let baseConfig, config, provider, signer, account, busy = false, invalidated = false, sent = false;
    const status = message => { el("status").textContent = message; };
    function controls() {
        const enabled = !!(config && config.enabled);
        el("connect").disabled = !enabled || busy;
        if (el("load")) el("load").disabled = !enabled || !account || busy || sent;
        ["amount", "recipient", "submit"].forEach(id => { el(id).disabled = !enabled || !account || busy || sent; });
    }
    function fail(error) {
        const code = error && (error.code || (error.info && error.info.error && error.info.error.code));
        status(code === 4001 || code === "ACTION_REJECTED" ? "Solicitação cancelada na carteira." :
            "Não foi possível concluir: " + (error.shortMessage || error.message || "verifique sua carteira e tente novamente."));
    }
    function reference(hash, label = "Depósito BNB") {
        const p = el("reference"); p.hidden = false; p.textContent = label + ": ";
        const link = document.createElement("a"); link.href = (config.chainId === 56 ? "https://bscscan.com/tx/" : "https://testnet.bscscan.com/tx/") + hash;
        link.textContent = hash; link.target = "_blank"; link.rel = "noopener noreferrer"; p.appendChild(link);
    }
    async function assertSession() {
        if (invalidated || Number(await window.ethereum.request({ method: "eth_chainId" })) !== config.chainId)
            throw new Error("A rede ou a conta mudou. Conecte novamente na rede BNB configurada.");
        const accounts = await window.ethereum.request({ method: "eth_accounts" });
        if (!accounts[0] || accounts[0].toLowerCase() !== account.toLowerCase()) throw new Error("A conta mudou. Conecte novamente.");
    }
    async function contracts() {
        await assertSession();
        const vault = new ethers.Contract(config.vault, vaultAbi, signer);
        const token = new ethers.Contract(config.asset, tokenAbi, signer);
        const values = await Promise.all([provider.getCode(config.vault), provider.getCode(config.asset),
            vault.asset(), vault.nativeNetwork(), vault.nativeToken(), vault.scale(), token.decimals(), vault.paused()]);
        const decimals = Number(values[6]);
        const hash = ethers.sha256(ethers.toUtf8Bytes(config.network));
        if (values[0] === "0x" || values[1] === "0x" || values[2].toLowerCase() !== config.asset.toLowerCase()
            || values[3].toLowerCase() !== hash || values[4].toLowerCase() !== "0x" + config.token
            || decimals < 0 || decimals > 18 || precision !== Math.min(8, decimals) || values[5] !== 10n ** BigInt(decimals - precision))
            throw new Error("O cofre não corresponde à configuração desta rede POVIX.");
        if (config.production) {
            if (ethers.keccak256(values[0]) !== config.vaultCodeHash || ethers.keccak256(values[1]) !== config.assetCodeHash ||
                await vault.bridgeVersion() !== 2n || await vault.releaseDelay() !== 86400n)
                throw new Error("O código ou a versão de produção do cofre diverge da configuração aprovada.");
        }
        if (values[7]) throw new Error("A ponte está pausada pelos operadores.");
        return { vault, token, scale: values[5] };
    }
    el("connect").addEventListener("click", async () => {
        busy = true; controls();
        try {
            if (!window.ethereum) throw new Error("Instale uma carteira BNB compatível, como MetaMask ou Rabby.");
            const accounts = await window.ethereum.request({ method: "eth_requestAccounts" });
            if (Number(await window.ethereum.request({ method: "eth_chainId" })) !== config.chainId)
                await window.ethereum.request({ method: "wallet_switchEthereumChain", params: [{ chainId: "0x" + config.chainId.toString(16) }] });
            provider = new ethers.BrowserProvider(window.ethereum, "any");
            signer = await provider.getSigner(accounts[0]); account = await signer.getAddress(); invalidated = false;
            await contracts();
            el("account").textContent = (config.chainId === 56 ? "BNB Mainnet · " : "BNB Testnet · ") + account;
            status("Carteira conectada. Informe o token, o valor e o endereço público POVIX de destino.");
        } catch (error) { account = null; fail(error); }
        finally { busy = false; controls(); }
    });
    if (window.ethereum && window.ethereum.on) {
        const changed = () => { invalidated = true; account = null; controls();
            el("account").textContent = "Reconecte sua carteira BNB";
            if (!busy && !sent) status("A conta ou a rede mudou. Conecte novamente."); };
        window.ethereum.on("accountsChanged", changed); window.ethereum.on("chainChanged", changed);
    }
    if (el("load")) el("load").addEventListener("click", async () => {
        if (busy || sent || !account || !baseConfig.factory) return;
        busy = true; controls(); el("submit").disabled = true;
        try {
            await assertSession();
            const asset = ethers.getAddress(el("asset").value.trim()).toLowerCase();
            if (/^0x0{40}$/.test(asset)) throw new Error("Contrato inválido.");
            const factory = new ethers.Contract(baseConfig.factory, ["function nativeNetwork() view returns(bytes32)", "function vaults(address) view returns(address)", "function representation(address) view returns(bytes32)", "function createVault(address) returns(address)"], signer);
            if (await provider.getCode(baseConfig.factory) === "0x" || await factory.nativeNetwork() !== ethers.sha256(ethers.toUtf8Bytes(config.network)) ||
                (await factory.vaults(baseConfig.asset)).toLowerCase() !== baseConfig.vault.toLowerCase()) throw new Error("Factory divergente da ponte configurada.");
            if (config.production && ethers.keccak256(await provider.getCode(baseConfig.factory)) !== baseConfig.factoryCodeHash)
                throw new Error("Código da factory divergente da configuração aprovada.");
            const token = new ethers.Contract(asset, ["function decimals() view returns(uint8)", "function symbol() view returns(string)"], signer);
            const decimals = Number(await token.decimals());
            if (decimals < 0 || decimals > 18) throw new Error("Precisão não suportada.");
            let symbol; try { symbol = await token.symbol(); } catch (_) { symbol = "TOKEN"; }
            assetSymbol = (String(symbol).toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 9) || "TOKEN");
            const id = asset === baseConfig.asset.toLowerCase() ? protocolToken : "b9b0" + ethers.sha256(ethers.toUtf8Bytes("povix-bnb-token-v1|" + config.chainId + "|" + asset)).slice(6);
            if ((await factory.representation(asset)).toLowerCase() !== "0x" + id) throw new Error("Identificador de origem divergente.");
            let vault = await factory.vaults(asset);
            if (/^0x0{40}$/.test(vault)) {
                if (config.production) throw new Error("Novo ativo: os operadores precisam aprovar os limites e criar o cofre antes de aceitar depósitos.");
                status("Token novo: confirme a criação do cofre na carteira. Não será feito depósito nesta etapa.");
                await assertSession();
                const created = await factory.createVault(asset); reference(created.hash, "Criação do cofre BNB"); await created.wait(1);
                vault = await factory.vaults(asset);
            }
            const approved = config.production && asset !== baseConfig.asset.toLowerCase() ? (baseConfig.approvedAssets || []).find(item => item.asset === asset && item.vault.toLowerCase() === vault.toLowerCase()) : null;
            if (config.production && asset !== baseConfig.asset.toLowerCase() && !approved)
                throw new Error("Cofre criado ou localizado. Aguarde a aprovação do ativo pelos operadores antes de depositar.");
            config = { ...baseConfig, ...(approved || {}), asset, vault, token: id }; precision = Math.min(8, decimals);
            selectedSymbol = id === protocolToken ? "pUSDT" : "p" + assetSymbol;
            el("selected").textContent = assetSymbol + " → " + selectedSymbol + " · " + asset;
            el("amount").value = ""; el("quote").textContent = "— " + selectedSymbol;
            await contracts();
            status("Token selecionado. A representação será registrada, se necessário, após o depósito confirmado. Até " + precision + " casas decimais; sem arredondamento.");
        } catch (error) {
            invalidated = true; fail(error); account = null; config = { ...baseConfig };
            precision = 8; selectedSymbol = "pUSDT"; assetSymbol = "USDT";
            el("selected").textContent = "USDT → pUSDT · " + baseConfig.asset;
            el("amount").value = ""; el("quote").textContent = "— pUSDT";
        }
        finally { busy = false; controls(); }
    });
    el("amount").addEventListener("input", () => {
        try { el("quote").textContent = ethers.formatUnits(parseAmount(el("amount").value, precision), precision) + " " + selectedSymbol; }
        catch (_) { el("quote").textContent = "— " + selectedSymbol; }
    });
    el("form").addEventListener("submit", async event => {
        event.preventDefault(); if (busy || sent || !account || !config.enabled) return;
        busy = true; controls(); let hash;
        try {
            const amount = parseAmount(el("amount").value, precision), destination = recipient(el("recipient").value.trim());
            const { vault, token, scale } = await contracts(), assetAmount = amount * scale;
            const depositor = account;
            const [balance, reserve, cap] = await Promise.all([token.balanceOf(account), vault.accountedReserve(), vault.reserveCap()]);
            if (balance < assetAmount) throw new Error("Saldo do token insuficiente.");
            if (reserve + amount > cap) throw new Error("O depósito excede o limite de reserva da ponte.");
            const allowance = await token.allowance(account, config.vault);
            if (allowance < assetAmount) {
                if (allowance > 0n) {
                    status("Confirme na carteira a redefinição da autorização anterior."); await assertSession();
                    const reset = await token.approve(config.vault, 0n); await reset.wait(1);
                }
                status("Autorize na carteira somente o valor deste depósito."); await assertSession();
                const approval = await token.approve(config.vault, assetAmount); await approval.wait(1);
            }
            await contracts(); // Check network, account and pause again after approval.
            status("Confirme o depósito do token na carteira BNB."); await assertSession();
            const tx = await vault.deposit(assetAmount, destination); hash = tx.hash; sent = true; reference(hash);
            status("Depósito enviado. Aguardando " + config.confirmations + " confirmações BNB. Não envie novamente.");
            let receipt;
            try { receipt = await tx.wait(config.confirmations, 180000); }
            catch (error) {
                if (error.code === "TRANSACTION_REPLACED" && !error.cancelled) receipt = error.receipt;
                else throw error;
            }
            if (!receipt || receipt.status !== 1) throw new Error("O depósito falhou na BNB. Confira a transação.");
            if (receipt.hash !== hash) { hash = receipt.hash; reference(hash); }
            const deposit = receipt.logs.filter(log => log.address.toLowerCase() === config.vault.toLowerCase()).map(log => {
                try { return vault.interface.parseLog(log); } catch (_) { return null; }
            }).find(log => log && log.name === "Deposited" && log.args.sender.toLowerCase() === depositor.toLowerCase()
                && log.args.nativeRecipient.toLowerCase() === destination && log.args.nativeAmount === amount && log.args.assetAmount === assetAmount);
            if (!deposit) throw new Error("O recibo não comprova este depósito. Contate os operadores com o hash da transação.");
            status("Depósito confirmado na BNB. Aguardando validação dos operadores e registro/emissão do token representado na rede POVIX. Confirmação BNB ainda não significa crédito na POVIX.");
            el("reference").appendChild(document.createTextNode(" · Identificador: " + deposit.args.depositId));
        } catch (error) {
            if (hash) status("Transação enviada: acompanhe o link BNB e encaminhe o hash aos operadores. O crédito na POVIX ainda não foi confirmado. Não repita o depósito.");
            else fail(error);
        } finally { busy = false; controls(); }
    });
    fetch(app.dataset.configUrl, { cache: "no-store", credentials: "same-origin" }).then(response => {
        if (!response.ok) throw new Error("Configuração indisponível."); return response.json();
    }).then(value => {
        if (![56, 97].includes(value.chainId) || value.chainId === 56 && (!value.production || value.confirmations < 64) || value.confirmations < 15) throw new Error("Configuração de teste inválida.");
        baseConfig = value; config = { ...value };
        if (el("environment")) el("environment").textContent = value.production ? "BNB Smart Chain · Mainnet" : "Ambiente de teste · BNB Testnet";
        if (el("environment-note")) el("environment-note").textContent = value.production ?
            "Depósitos usam fundos reais. Resgates dependem da maioria dos operadores e têm espera de 24 horas após autorização. Tokens com taxas ou saldo variável não são suportados." :
            "Use somente tokens de teste. A emissão depende das confirmações e da validação pelos operadores.";
        if (el("asset-picker")) el("asset-picker").hidden = !value.factory;
        status(value.enabled ? (value.production ? "Ponte mainnet configurada. Conecte sua carteira BNB." : "Ponte de teste disponível. Conecte sua carteira BNB Testnet.") :
            "Ponte em preparação. O contrato e o ativo de teste ainda não foram configurados. Depósitos desabilitados."); controls();
    }).catch(fail);
}());
