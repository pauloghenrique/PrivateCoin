/* USDT testnet deposits only. Native minting remains the federation's responsibility. */
(function () {
    "use strict";
    const vaultAbi = ["function asset() view returns(address)", "function nativeNetwork() view returns(bytes32)",
        "function nativeToken() view returns(bytes32)", "function scale() view returns(uint256)",
        "function paused() view returns(bool)", "function accountedReserve() view returns(uint256)",
        "function reserveCap() view returns(uint256)", "function deposit(uint256,bytes32)",
        "event Deposited(bytes32 indexed depositId,address indexed sender,bytes32 indexed nativeRecipient,uint256 nativeAmount,uint256 assetAmount)"];
    const tokenAbi = ["function decimals() view returns(uint8)", "function balanceOf(address) view returns(uint256)",
        "function allowance(address,address) view returns(uint256)", "function approve(address,uint256) returns(bool)"];
    function parseAmount(value) {
        const text = value.trim().replace(",", ".");
        if (!/^\d{1,20}(\.\d{1,8})?$/.test(text)) throw new Error("Informe um valor positivo com até 8 casas decimais, sem separador de milhar.");
        const parts = text.split(".");
        const amount = BigInt(parts[0]) * 100000000n + BigInt((parts[1] || "").padEnd(8, "0"));
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
    let config, provider, signer, account, busy = false, invalidated = false, sent = false;
    const status = message => { el("status").textContent = message; };
    function controls() {
        const enabled = !!(config && config.enabled);
        el("connect").disabled = !enabled || busy;
        ["amount", "recipient", "submit"].forEach(id => { el(id).disabled = !enabled || !account || busy || sent; });
    }
    function fail(error) {
        const code = error && (error.code || (error.info && error.info.error && error.info.error.code));
        status(code === 4001 || code === "ACTION_REJECTED" ? "Solicitação cancelada na carteira." :
            "Não foi possível concluir: " + (error.shortMessage || error.message || "verifique sua carteira e tente novamente."));
    }
    function reference(hash) {
        const p = el("reference"); p.hidden = false; p.textContent = "Depósito BNB: ";
        const link = document.createElement("a"); link.href = "https://testnet.bscscan.com/tx/" + hash;
        link.textContent = hash; link.target = "_blank"; link.rel = "noopener noreferrer"; p.appendChild(link);
    }
    async function assertSession() {
        if (invalidated || Number(await window.ethereum.request({ method: "eth_chainId" })) !== config.chainId)
            throw new Error("A rede ou a conta mudou. Conecte novamente na BNB Smart Chain Testnet.");
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
            || decimals < 8 || decimals > 18 || values[5] !== 10n ** BigInt(decimals - 8))
            throw new Error("O cofre não corresponde à configuração desta rede POVIX.");
        if (values[7]) throw new Error("A ponte está pausada pelos operadores.");
        return { vault, token, scale: values[5] };
    }
    el("connect").addEventListener("click", async () => {
        busy = true; controls();
        try {
            if (!window.ethereum) throw new Error("Instale uma carteira BNB compatível, como MetaMask ou Rabby.");
            const accounts = await window.ethereum.request({ method: "eth_requestAccounts" });
            if (Number(await window.ethereum.request({ method: "eth_chainId" })) !== config.chainId)
                await window.ethereum.request({ method: "wallet_switchEthereumChain", params: [{ chainId: "0x61" }] });
            provider = new ethers.BrowserProvider(window.ethereum, "any");
            signer = await provider.getSigner(accounts[0]); account = await signer.getAddress(); invalidated = false;
            await contracts();
            el("account").textContent = "BNB Testnet · " + account;
            status("Carteira conectada. Informe o valor e o endereço público POVIX de destino.");
        } catch (error) { account = null; fail(error); }
        finally { busy = false; controls(); }
    });
    if (window.ethereum && window.ethereum.on) {
        const changed = () => { invalidated = true; account = null; controls();
            el("account").textContent = "Reconecte sua carteira BNB";
            if (!busy && !sent) status("A conta ou a rede mudou. Conecte novamente."); };
        window.ethereum.on("accountsChanged", changed); window.ethereum.on("chainChanged", changed);
    }
    el("amount").addEventListener("input", () => {
        try { el("quote").textContent = ethers.formatUnits(parseAmount(el("amount").value), 8) + " pUSDT"; }
        catch (_) { el("quote").textContent = "— pUSDT"; }
    });
    el("form").addEventListener("submit", async event => {
        event.preventDefault(); if (busy || sent || !account || !config.enabled) return;
        busy = true; controls(); let hash;
        try {
            const amount = parseAmount(el("amount").value), destination = recipient(el("recipient").value.trim());
            const { vault, token, scale } = await contracts(), assetAmount = amount * scale;
            const depositor = account;
            const [balance, reserve, cap] = await Promise.all([token.balanceOf(account), vault.accountedReserve(), vault.reserveCap()]);
            if (balance < assetAmount) throw new Error("Saldo de USDT de teste insuficiente.");
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
            status("Confirme o depósito de USDT na carteira BNB."); await assertSession();
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
            status("Depósito confirmado na BNB. Aguardando validação dos operadores e emissão de pUSDT na rede POVIX. Confirmação BNB ainda não significa crédito de pUSDT.");
            el("reference").appendChild(document.createTextNode(" · Identificador: " + deposit.args.depositId));
        } catch (error) {
            if (hash) status("Transação enviada: acompanhe o link BNB e encaminhe o hash aos operadores. O crédito de pUSDT ainda não foi confirmado. Não repita o depósito.");
            else fail(error);
        } finally { busy = false; controls(); }
    });
    fetch(app.dataset.configUrl, { cache: "no-store", credentials: "same-origin" }).then(response => {
        if (!response.ok) throw new Error("Configuração indisponível."); return response.json();
    }).then(value => {
        if (value.chainId !== 97 || value.confirmations < 15) throw new Error("Configuração de teste inválida.");
        config = value;
        status(value.enabled ? "Ponte de teste disponível. Conecte sua carteira BNB Testnet." :
            "Ponte em preparação. O contrato e o ativo de teste ainda não foram configurados. Depósitos desabilitados."); controls();
    }).catch(fail);
}());
