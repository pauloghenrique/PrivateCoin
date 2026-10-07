(function () {
    "use strict";
    if (!document.getElementById("povix-live-swap")) return;
    var quote = null, credentials = null, pending = null, busy = false;
    var storageKey = "povix-swap-pending-v1";
    function el(id) { return document.getElementById("live-" + id); }
    function error(message) { el("error").textContent = message || ""; el("error").hidden = !message; }
    function random() {
        return Array.from(window.crypto.getRandomValues(new Uint8Array(32)), function (v) { return v.toString(16).padStart(2, "0"); }).join("");
    }
    async function request(path, data, key) {
        var headers = { "Content-Type": "application/json" };
        if (key) headers["Idempotency-Key"] = key;
        var response = await fetch("/swap-api/" + path, { method: "POST", headers: headers, body: JSON.stringify(data), cache: "no-store", credentials: "omit" });
        var result;
        try { result = await response.json(); } catch (failure) { throw new Error("Serviço de swap indisponível. Seus dados de ordem foram preservados para tentar novamente."); }
        if (!response.ok || result.error) throw new Error(result.error || "Consulta indisponível.");
        return result;
    }
    function details(container, entries) {
        container.textContent = "";
        entries.forEach(function (entry) {
            var row = document.createElement("div"), label = document.createElement("dt"), value = document.createElement("dd");
            label.textContent = entry[0]; value.textContent = entry[1]; value.style.overflowWrap = "anywhere";
            row.appendChild(label); row.appendChild(value); container.appendChild(row);
        });
    }
    function renderQuote() {
        details(el("details"), [["Você paga", quote.amount + " " + quote.source], ["Você recebe", quote.received + " " + quote.target], ["Taxa incluída", quote.fee + " " + quote.source], ["Rede de destino", quote.network], ["Endereço de destino", quote.destination], ["Execução", quote.execution]]);
        el("quote").hidden = false;
    }
    function renderOrder(order) {
        var states = { awaiting_deposit: "Aguardando depósito", payment_review: "Depósito confirmado; aguardando pagamento da tesouraria", completed: "Pagamento confirmado na blockchain" };
        details(el("order-details"), [["Estado", states[order.state] || order.state], ["Enviar exatamente", order.amount + " " + order.source], ["Rede do depósito", order.network], ["Endereço do depósito", order.deposit_address], ["Confirmações exigidas", String(order.confirmations)], ["Você recebe", order.received + " " + order.target], ["Seu endereço de recebimento", order.destination], ["Depósito", order.deposit_tx || "Ainda não verificado"], ["Pagamento", order.payout_tx || "Ainda não verificado"]]);
        el("credentials").textContent = "Ordem: " + credentials.order_id + " · Código de acesso: " + credentials.token;
        el("order").hidden = false; el("deposit-form").hidden = order.state !== "awaiting_deposit";
        el("quote").hidden = true;
    }
    async function run(action) {
        if (busy) return;
        busy = true; error("");
        var buttons = document.querySelectorAll("#povix-live-swap button");
        buttons.forEach(function (button) { button.disabled = true; });
        try { await action(); } catch (failure) { error(failure.message || "Consulta indisponível."); }
        finally { busy = false; buttons.forEach(function (button) { button.disabled = false; }); }
    }
    function invalidate() { if (!pending) { quote = null; el("quote").hidden = true; } }
    ["source", "amount", "destination"].forEach(function (id) { el(id).addEventListener("input", invalidate); });
    el("form").addEventListener("submit", function (event) {
        event.preventDefault();
        if (pending) return;
        run(async function () {
            var data = { source: el("source").value, amount: el("amount").value.trim(), destination: el("destination").value.trim() };
            var result = await request("quote", data);
            if (data.source !== el("source").value || data.amount !== el("amount").value.trim() || data.destination !== el("destination").value.trim()) return;
            quote = result; renderQuote();
        });
    });
    el("create").addEventListener("click", function () {
        if (!quote && !pending) return;
        run(async function () {
            if (!pending) {
                if (Date.now() / 1000 >= quote.expires) { invalidate(); throw new Error("Cotação expirada. Consulte uma nova cotação."); }
                pending = { quote: quote, token: random(), key: random() };
                // Persist BEFORE issuing the request so a reload can retry the exact operation.
                try { sessionStorage.setItem(storageKey, JSON.stringify(pending)); }
                catch (failure) { pending = null; throw new Error("Permita o armazenamento de sessão do navegador para preservar sua ordem em caso de falha."); }
            }
            el("fields").disabled = true;
            var order = await request("order", { quote_id: pending.quote.quote_id, token: pending.token }, pending.key);
            credentials = { order_id: order.order_id, token: pending.token };
            sessionStorage.setItem("povix-swap-order-v1", JSON.stringify(credentials));
            sessionStorage.removeItem(storageKey); pending = null;
            renderOrder(order); el("fields").disabled = false;
        });
    });
    el("deposit-form").addEventListener("submit", function (event) {
        event.preventDefault(); if (!credentials) return;
        run(async function () { renderOrder(await request("order/deposit", { order_id: credentials.order_id, token: credentials.token, tx: el("tx").value.trim() })); });
    });
    el("refresh").addEventListener("click", function () {
        if (!credentials) return;
        run(async function () { renderOrder(await request("order/status", credentials)); });
    });
    el("resume").addEventListener("submit", function (event) {
        event.preventDefault();
        run(async function () {
            var candidate = { order_id: el("order-id").value.trim(), token: el("token").value.trim() };
            var order = await request("order/status", candidate); credentials = candidate; renderOrder(order);
        });
    });
    try {
        pending = JSON.parse(sessionStorage.getItem(storageKey) || "null");
        if (pending && pending.quote && pending.token && pending.key) {
            quote = pending.quote; renderQuote(); el("create").textContent = "Retomar confirmação da ordem"; el("fields").disabled = true;
        } else { pending = null; }
        var saved = JSON.parse(sessionStorage.getItem("povix-swap-order-v1") || "null");
        if (saved) { el("order-id").value = saved.order_id; el("token").value = saved.token; }
    } catch (failure) { pending = null; }
    fetch("/swap-api/status", { cache: "no-store", credentials: "omit" }).then(function (response) {
        if (!response.ok) throw new Error(); return response.json();
    }).then(function (status) {
        el("availability").textContent = status.message;
        el("fields").disabled = !status.ready || !!pending;
    }).catch(function () { el("availability").textContent = "Trocas indisponíveis. A tesouraria ainda não está conectada ao serviço."; });
}());
