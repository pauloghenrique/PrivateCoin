/* Demonstration only: fixed example prices, no network or real wallet access. */
(function (root, factory) {
    "use strict";
    var api = factory();
    if (typeof module === "object" && module.exports) module.exports = api;
    else root.PovixSwap = api;
}(typeof window !== "undefined" ? window : this, function () {
    "use strict";
    var unit = BigInt(100000000);
    var basis = BigInt(10000);
    var maximum = BigInt(1000000) * unit;
    // Illustrative values in cents. These are not fetched market prices.
    var prices = { POVIX: BigInt(25), USDT: BigInt(100), BNB: BigInt(30000), ETH: BigInt(200000), BTC: BigInt(5000000) };
    var symbols = Object.freeze(Object.keys(prices));

    function parseAmount(value) {
        var text = String(value).trim();
        if (!/^\d{1,7}([.,]\d{1,8})?$/.test(text)) {
            throw new Error("Informe um valor positivo com até 8 casas decimais, sem separador de milhar.");
        }
        var parts = text.replace(",", ".").split(".");
        var amount = BigInt(parts[0]) * unit + BigInt(((parts[1] || "") + "00000000").slice(0, 8));
        if (amount <= BigInt(0)) throw new Error("Informe uma quantidade maior que zero.");
        if (amount > maximum) throw new Error("O limite por simulação é de 1.000.000 de unidades.");
        return amount;
    }

    function toInput(amount) {
        var fraction = (amount % unit).toString().padStart(8, "0").replace(/0+$/, "");
        return (amount / unit).toString() + (fraction ? "," + fraction : "");
    }

    function formatAmount(amount) {
        var parts = toInput(amount).split(",");
        return parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, ".") + "," + (parts[1] || "").padEnd(2, "0");
    }

    function validatePair(from, to) {
        if (!Object.prototype.hasOwnProperty.call(prices, from) || !Object.prototype.hasOwnProperty.call(prices, to)) {
            throw new Error("Selecione uma moeda disponível na demonstração.");
        }
        if (from === to) throw new Error("Escolha moedas diferentes para a troca.");
    }

    function quote(from, to, input, slippage) {
        validatePair(from, to);
        var tolerance = Number(slippage);
        if ([10, 50, 100, 300].indexOf(tolerance) === -1) throw new Error("Selecione uma tolerância disponível.");
        var amount = parseAmount(input);
        // Round fees up and receipts down to the atomic unit; never create dust.
        var fee = (amount * BigInt(30) + basis - BigInt(1)) / basis;
        var received = (amount - fee) * prices[from] / prices[to];
        var minimum = received * (basis - BigInt(tolerance)) / basis;
        if (minimum <= BigInt(0)) throw new Error("Valor muito pequeno para este par. Aumente a quantidade.");
        return Object.freeze({ from: from, to: to, amount: amount, fee: fee, received: received, minimum: minimum, slippage: tolerance });
    }

    function rate(from, to) {
        validatePair(from, to);
        return unit * prices[from] / prices[to];
    }

    function createWallet() {
        return { POVIX: parseAmount("2500"), USDT: parseAmount("1000"), BNB: parseAmount("2"), ETH: parseAmount("0.5"), BTC: parseAmount("0.02") };
    }

    function execute(wallet, reviewedQuote) {
        // Recompute instead of accepting a caller-supplied fee or receipt.
        var checked = quote(reviewedQuote.from, reviewedQuote.to, toInput(reviewedQuote.amount), reviewedQuote.slippage);
        if (!wallet || typeof wallet[checked.from] !== "bigint" || typeof wallet[checked.to] !== "bigint") {
            throw new Error("Ative a carteira demo para simular a troca.");
        }
        if (wallet[checked.from] < checked.amount) throw new Error("Saldo demo insuficiente. Reduza o valor ou reinicie a demonstração.");
        var updated = Object.assign({}, wallet);
        updated[checked.from] -= checked.amount;
        updated[checked.to] += checked.received;
        return updated;
    }

    return Object.freeze({ symbols: symbols, maximum: maximum, parseAmount: parseAmount, toInput: toInput, formatAmount: formatAmount, quote: quote, rate: rate, createWallet: createWallet, execute: execute });
}));
