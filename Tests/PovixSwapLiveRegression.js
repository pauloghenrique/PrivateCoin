"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const script = fs.readFileSync(path.join(__dirname, "../PrivateCoin.Site/Scripts/povix-swap-live.js"), "utf8");
class Element {
    constructor(tag = "div") {
        this.tag = tag; this.children = []; this.listeners = {}; this.style = {};
        this.value = ""; this.hidden = true; this.disabled = false;
    }
    set textContent(value) { this.text = value; this.children = []; }
    get textContent() { return this.text || ""; }
    appendChild(child) {
        this.children.push(child);
        if (this.tag === "select" && this.children.length === 1) this.value = child.value;
    }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    fire(name) { this.listeners[name]?.({ preventDefault() {} }); }
}
async function scenario(symbol, ready) {
    const elements = {};
    const ids = [...script.matchAll(/el\("([\w-]+)"\)/g)].map(match => match[1]).concat(["source", "amount", "destination"]);
    ids.forEach(id => { elements["live-" + id] = new Element(id === "source" ? "select" : "div"); });
    elements["povix-live-swap"] = new Element();
    const assets = [
        { symbol: "POVIX", decimals: 8, network: "POVIX fixture" },
        { symbol, decimals: symbol === "BNB" ? 18 : 6, network: "EVM fixture" }
    ];
    const calls = [];
    const context = {
        document: { getElementById: id => elements[id], createElement: tag => new Element(tag), querySelectorAll: () => [] },
        sessionStorage: { getItem: () => null },
        window: {},
        fetch: async (url, options) => {
            calls.push({ url, options });
            if (url === "/swap-api/status") return { ok: true, json: async () => ({ ready, message: "fixture availability", assets }) };
            const data = JSON.parse(options.body);
            return { ok: true, json: async () => ({ source: data.source, target: "POVIX", amount: data.amount, received: "1.00000000", fee: "0.00000000", destination: data.destination, network: "POVIX fixture", execution: "Manual", expires: Date.now() / 1000 + 120 }) };
        }
    };
    vm.runInNewContext(script, context);
    await new Promise(resolve => setImmediate(resolve));
    const el = id => elements["live-" + id];
    assert.deepEqual(el("source").children.map(option => option.textContent), ["POVIX → " + symbol, symbol + " → POVIX"]);
    assert.equal(el("fields").disabled, !ready);
    if (!ready) { assert.equal(calls.length, 1); return; }
    assert.match(el("network-help").textContent, /EVM fixture/);
    if (symbol === "BNB") assert.match(el("network-help").textContent, /BNB nativo/);
    el("source").value = symbol;
    el("source").fire("change");
    assert.match(el("network-help").textContent, /POVIX fixture/);
    assert.equal(el("amount").maxLength, symbol === "BNB" ? 31 : 19);
    el("amount").value = "0.123456789012345678";
    el("destination").value = "a".repeat(64);
    el("form").fire("submit");
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(JSON.parse(calls[1].options.body).source, symbol);
    assert.equal(JSON.parse(calls[1].options.body).amount, el("amount").value);
    assert.equal(el("quote").hidden, false);
    el("source").fire("input");
    assert.equal(el("quote").hidden, true);
}
(async () => {
    await scenario("BNB", true);
    await scenario("BNB", false);
    await scenario("USDT", true);
    console.log("PASS live pair/network metadata, native BNB guidance, decimal preservation, disabled service and quote invalidation");
})().catch(error => { console.error(error); process.exitCode = 1; });
