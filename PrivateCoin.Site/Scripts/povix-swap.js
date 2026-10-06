(function () {
    "use strict";
    var root = document.getElementById("povix-swap");
    if (!root) return;
    var api = window.PovixSwap;
    var wallet = null;
    var currentQuote = null;
    var reviewedQuote = null;
    var history = [];
    var previousFrom;
    var previousTo;
    function element(name) { return document.getElementById("swap-" + name); }
    function text(name, value) { element(name).textContent = value; }
    var form = element("form");
    var amount = element("amount");
    var from = element("from");
    var to = element("to");
    var slippage = element("slippage");
    var review = element("review");

    function error(message) {
        text("error", message || "");
        element("error").hidden = !message;
    }

    function balanceText(symbol) {
        return "Saldo demo: " + (wallet ? api.formatAmount(wallet[symbol]) + " " + symbol : "—");
    }

    function renderWallet() {
        text("from-balance", balanceText(from.value));
        text("to-balance", balanceText(to.value));
        text("wallet-state", wallet ? "Demo ativa" : "Não ativada");
        text("wallet", wallet ? "Reiniciar demonstração" : "Ativar carteira demo");
        element("max").disabled = !wallet;
        var balances = element("balances");
        balances.textContent = "";
        api.symbols.forEach(function (symbol) {
            var row = document.createElement("div");
            var label = document.createElement("dt");
            var value = document.createElement("dd");
            label.textContent = symbol;
            value.textContent = wallet ? api.formatAmount(wallet[symbol]) : "—";
            row.appendChild(label);
            row.appendChild(value);
            balances.appendChild(row);
        });
    }

    function update() {
        currentQuote = null;
        amount.setAttribute("aria-invalid", "false");
        error("");
        renderWallet();
        var message = "";
        try {
            currentQuote = api.quote(from.value, to.value, amount.value, slippage.value);
            text("receive", api.formatAmount(currentQuote.received));
            text("rate", "1 " + from.value + " ≈ " + api.formatAmount(api.rate(from.value, to.value)) + " " + to.value);
            text("fee", api.formatAmount(currentQuote.fee) + " " + from.value);
            text("minimum", api.formatAmount(currentQuote.minimum) + " " + to.value);
            if (wallet && wallet[from.value] < currentQuote.amount) message = "Saldo demo insuficiente. Reduza o valor ou reinicie a demonstração.";
        } catch (failure) {
            text("receive", "—");
            text("rate", "—");
            text("fee", "—");
            text("minimum", "—");
            message = failure.message;
            amount.setAttribute("aria-invalid", "true");
        }
        if (message) error(message);
        text("submit", !wallet ? "Ativar carteira demo" : "Revisar simulação");
        element("submit").disabled = !!wallet && (!currentQuote || !!message);
    }

    function renderHistory() {
        var body = element("history");
        body.textContent = "";
        element("history-empty").hidden = history.length > 0;
        element("history-wrap").hidden = history.length === 0;
        history.forEach(function (entry) {
            var row = document.createElement("tr");
            var cells = [entry.time, api.formatAmount(entry.quote.amount) + " " + entry.quote.from, api.formatAmount(entry.quote.received) + " " + entry.quote.to, "Simulada"];
            cells.forEach(function (value, index) {
                var cell = document.createElement("td");
                cell.textContent = value;
                if (index === 3) cell.className = "swap-history-state";
                row.appendChild(cell);
            });
            body.appendChild(row);
        });
    }

    function activate() {
        wallet = api.createWallet();
        history = [];
        renderHistory();
        update();
        text("status", "Carteira de demonstração ativada. Todos os saldos são fictícios.");
    }

    function detail(label, value) {
        var row = document.createElement("div");
        var name = document.createElement("dt");
        var content = document.createElement("dd");
        name.textContent = label;
        content.textContent = value;
        row.appendChild(name);
        row.appendChild(content);
        element("review-details").appendChild(row);
    }

    function closeReview() {
        reviewedQuote = null;
        review.close();
    }

    if (!api || typeof review.showModal !== "function") {
        error("Seu navegador não suporta esta demonstração. Abra a página em uma versão atual do navegador.");
        return;
    }
    previousFrom = from.value;
    previousTo = to.value;
    element("wallet").disabled = false;
    element("wallet").addEventListener("click", activate);
    amount.addEventListener("input", update);
    slippage.addEventListener("change", update);
    from.addEventListener("change", function () {
        if (from.value === to.value) to.value = previousFrom;
        previousFrom = from.value;
        previousTo = to.value;
        update();
    });
    to.addEventListener("change", function () {
        if (to.value === from.value) from.value = previousTo;
        previousFrom = from.value;
        previousTo = to.value;
        update();
    });
    element("reverse").addEventListener("click", function () {
        var oldFrom = from.value;
        from.value = to.value;
        to.value = oldFrom;
        previousFrom = from.value;
        previousTo = to.value;
        update();
    });
    element("max").addEventListener("click", function () {
        if (!wallet) return;
        var available = wallet[from.value];
        amount.value = api.toInput(available > api.maximum ? api.maximum : available);
        update();
    });
    form.addEventListener("submit", function (event) {
        event.preventDefault();
        if (!wallet) { activate(); return; }
        update();
        if (!currentQuote || element("submit").disabled) return;
        reviewedQuote = currentQuote;
        element("review-details").textContent = "";
        detail("Você paga", api.formatAmount(reviewedQuote.amount) + " " + reviewedQuote.from);
        detail("Você recebe", api.formatAmount(reviewedQuote.received) + " " + reviewedQuote.to);
        detail("Taxa incluída (0,30%)", api.formatAmount(reviewedQuote.fee) + " " + reviewedQuote.from);
        detail("Recebimento mínimo", api.formatAmount(reviewedQuote.minimum) + " " + reviewedQuote.to);
        detail("Tolerância", (reviewedQuote.slippage / 100).toLocaleString("pt-BR") + "%");
        review.showModal();
        element("review-cancel").focus();
    });
    element("review-close").addEventListener("click", closeReview);
    element("review-cancel").addEventListener("click", closeReview);
    review.addEventListener("close", function () { reviewedQuote = null; element("submit").focus(); });
    element("confirm").addEventListener("click", function () {
        if (!reviewedQuote) return;
        var completed = reviewedQuote;
        try {
            wallet = api.execute(wallet, completed);
            history.unshift({ time: new Date().toLocaleTimeString("pt-BR"), quote: completed });
            history = history.slice(0, 10);
            closeReview();
            renderHistory();
            update();
            text("status", "Simulação concluída: " + api.formatAmount(completed.amount) + " " + completed.from + " → " + api.formatAmount(completed.received) + " " + completed.to + ". Saldos demo atualizados.");
        } catch (failure) { closeReview(); error(failure.message); }
    });
    update();
}());
