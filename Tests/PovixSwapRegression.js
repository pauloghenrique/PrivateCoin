"use strict";
const assert = require("node:assert/strict");
const swap = require("../PrivateCoin.Site/Scripts/povix-swap-core.js");
let checks = 0;
function test(name, action) {
    action();
    checks++;
    console.log("PASS " + name);
}

test("comma and dot inputs keep all eight decimal places", () => {
    assert.equal(swap.parseAmount("123,00000001"), 12300000001n);
    assert.equal(swap.parseAmount("123.00000001"), 12300000001n);
    assert.equal(swap.toInput(12300000001n), "123,00000001");
    assert.equal(swap.formatAmount(123456789001n), "1.234,56789001");
});

test("ambiguous, negative, excessive and non-finite inputs are rejected", () => {
    ["", "0", "-1", "NaN", "Infinity", "1e3", "1.000,50", "1,000.50", "0.000000001", "1000000.00000001", "9999999", "<script>"].forEach(value => assert.throws(() => swap.parseAmount(value)));
    assert.equal(swap.parseAmount("1000000"), 100000000000000n);
});

test("example conversion includes the fee once and applies tolerance", () => {
    const quote = swap.quote("USDT", "POVIX", "100", 50);
    assert.equal(quote.fee, 30000000n);
    assert.equal(quote.received, 39880000000n);
    assert.equal(quote.minimum, 39680600000n);
    assert.equal(swap.rate("USDT", "POVIX"), 400000000n);
    assert.equal(swap.rate("POVIX", "USDT"), 25000000n);
});

test("small amounts cannot create free receipts or a zero minimum", () => {
    assert.throws(() => swap.quote("USDT", "POVIX", "0.00000001", 50));
    assert.throws(() => swap.quote("POVIX", "BTC", "0.0001", 50));
    const quote = swap.quote("POVIX", "USDT", "0.000001", 50);
    assert.equal(quote.fee, 1n);
    assert.equal(quote.received, 24n);
    assert.equal(quote.minimum, 23n);
});

test("invalid pairs and unsupported tolerances cannot be quoted", () => {
    assert.throws(() => swap.quote("POVIX", "POVIX", "100", 50));
    ["DOGE", "constructor", "__proto__"].forEach(symbol => assert.throws(() => swap.quote(symbol, "POVIX", "100", 50)));
    [0, -50, 10000, "NaN"].forEach(tolerance => assert.throws(() => swap.quote("USDT", "POVIX", "100", tolerance)));
});

test("confirmation debits the payment, credits the receipt and leaves other balances intact", () => {
    const before = swap.createWallet();
    const quote = swap.quote("USDT", "POVIX", "100", 50);
    const after = swap.execute(before, quote);
    assert.equal(after.USDT, 90000000000n);
    assert.equal(after.POVIX, 289880000000n);
    assert.equal(after.BTC, before.BTC);
    assert.equal(after.ETH, before.ETH);
    assert.equal(after.BNB, before.BNB);
    assert.equal(before.USDT, 100000000000n);
});

test("insufficient balance fails atomically and maximum balance can be spent", () => {
    const before = swap.createWallet();
    const snapshot = Object.assign({}, before);
    assert.throws(() => swap.execute(before, swap.quote("USDT", "POVIX", "1000.00000001", 50)), /Saldo demo insuficiente/);
    assert.deepEqual(before, snapshot);
    const after = swap.execute(before, swap.quote("USDT", "POVIX", "1000", 50));
    assert.equal(after.USDT, 0n);
    assert.throws(() => swap.execute(null, swap.quote("USDT", "POVIX", "100", 50)), /Ative a carteira/);
});

test("execution recomputes caller-supplied receipts instead of minting arbitrary balances", () => {
    const quote = swap.quote("USDT", "POVIX", "100", 50);
    const forged = Object.assign({}, quote, { received: 999999999999999999n, fee: 0n });
    assert.deepEqual(swap.execute(swap.createWallet(), forged), swap.execute(swap.createWallet(), quote));
});

test("all demo pairs preserve atomic rounding and round trips lose only example fees", () => {
    swap.symbols.forEach(from => swap.symbols.filter(to => to !== from).forEach(to => {
        const forward = swap.quote(from, to, "100", 10);
        assert(forward.received > 0n);
        assert(forward.minimum <= forward.received);
        assert(forward.fee > 0n);
        if (forward.received <= swap.maximum) {
            const reverse = swap.quote(to, from, swap.toInput(forward.received), 10);
            assert(reverse.received < forward.amount);
        }
    }));
});

test("a new demo wallet restores initial balances without retaining previous state", () => {
    const first = swap.createWallet();
    first.USDT = 0n;
    assert.equal(swap.createWallet().USDT, 100000000000n);
});

console.log(checks + " Povix Swap regression checks passed.");
