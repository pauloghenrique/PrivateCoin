"""Behavioral checks: fixtures are test-only; production adapters never simulate funds."""
import concurrent.futures
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
import threading
import urllib.request
import urllib.error
from http.server import ThreadingHTTPServer

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "PrivateCoin.Swap"))
from engine import Engine, SwapError, atomic
from chains import Chains, TRANSFER
from server import Handler

POVIX_DEST = "a" * 64
EVM_DEST = "0x" + "a" * 40
POVIX_BANK = "b" * 64
EVM_BANK = "0x" + "b" * 40
TX = "c" * 64
TOKEN = "t" * 40


def config():
    return {
        "enabled": True, "povix_usdt": "0.25", "fee_bps": 30,
        "minimum": {"POVIX": "0.01", "USDT": "0.01"},
        "maximum": {"POVIX": "10000", "USDT": "10000"},
        "assets": {
            "POVIX": {"network": "fixture POVIX", "network_id": "native", "genesis_hash": "g", "api_url": "http://fixture", "confirmations": 2, "treasury_addresses": [POVIX_BANK], "deposit_addresses": [POVIX_BANK]},
            "USDT": {"network": "fixture EVM", "rpc_url": "http://fixture", "chain_id": 1, "contract": "0x" + "d" * 40, "confirmations": 3, "treasury_addresses": [EVM_BANK], "deposit_addresses": [EVM_BANK]},
        },
    }


class FixtureChains:
    def __init__(self):
        self.balances = {"POVIX": 10**12, "USDT": 10**12}
        self.valid = True

    def balance(self, symbol):
        return self.balances[symbol]

    def bnb_wallet(self, destination):
        if not self.valid:
            raise SwapError("Contract wallet rejected")

    def address_balance(self, symbol, destination):
        return 0

    def verify(self, symbol, tx, destination, amount, purpose, created):
        if not self.valid:
            raise SwapError("Proof rejected")
        if not isinstance(tx, str):
            raise SwapError("Proof missing")
        return tx.lower()


class EngineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = self.directory.name + "/swap.db"
        self.config = config()
        self.chains = FixtureChains()
        self.engine = Engine(self.path, self.config, self.chains, clock=lambda: 1000)

    def tearDown(self):
        self.directory.cleanup()

    def quote(self, source="POVIX", amount="100"):
        return self.engine.quote(dict(source=source, amount=amount, destination=EVM_DEST if source == "POVIX" else POVIX_DEST))

    def order(self, quote=None, key="request_key_00001", token=TOKEN):
        return self.engine.order(dict(quote_id=(quote or self.quote())["quote_id"], token=token), key)

    def test_exact_fee_and_precision(self):
        q = self.quote()
        self.assertEqual(q["fee"], "0.30000000")
        self.assertEqual(q["received"], "24.925000")
        self.assertEqual(atomic("1,000001", "USDT"), 1000001)
        for bad in ("1.0000001", "0", "-1", "1e3", "NaN"):
            with self.assertRaises(SwapError):
                atomic(bad, "USDT")

    def test_disabled_and_unfunded_never_create_order(self):
        self.config["enabled"] = False
        with self.assertRaises(SwapError):
            self.quote()
        self.config["enabled"] = True
        self.chains.balances["USDT"] = 0
        with self.assertRaises(SwapError):
            self.quote()

    def test_quotes_expire_and_config_changes_invalidate(self):
        q = self.quote()
        self.engine.clock = lambda: 1120
        with self.assertRaises(SwapError):
            self.order(q)
        self.engine.clock = lambda: 1000
        self.config["povix_usdt"] = "0.30"
        with self.assertRaises(SwapError):
            self.order(q)

    def test_idempotency_survives_restart_and_protects_token(self):
        q = self.quote()
        first = self.order(q)
        self.engine = Engine(self.path, self.config, self.chains, clock=lambda: 5000)
        self.assertEqual(first, self.order(q))
        self.config["enabled"] = False
        self.assertEqual(first, self.order(q))
        with self.assertRaises(SwapError):
            self.order(q, token="x" * 40)
        with self.assertRaises(SwapError):
            self.engine.status(dict(order_id=first["order_id"], token="x" * 40))
        with sqlite3.connect(self.path) as db:
            self.assertNotEqual(db.execute("SELECT token_hash FROM orders").fetchone()[0], TOKEN)

    def test_concurrent_orders_cannot_overbook_reserves(self):
        self.config["assets"]["POVIX"]["treasury_addresses"] += ["e" * 64]
        self.config["assets"]["POVIX"]["deposit_addresses"] += ["e" * 64]
        self.chains.balances["USDT"] = 24925000
        q1, q2 = self.quote(), self.quote()
        def attempt(item):
            try:
                return self.order(item[0], key=item[1])["order_id"]
            except SwapError:
                return None
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(attempt, [(q1, "request_key_00001"), (q2, "request_key_00002")]))
        self.assertEqual(sum(r is not None for r in results), 1)

    def test_address_pool_is_never_reused(self):
        first = self.order()
        self.engine.deposit(dict(order_id=first["order_id"], token=TOKEN, tx=TX))
        self.engine.settle(dict(order_id=first["order_id"], tx="0x" + TX))
        with self.assertRaises(SwapError):
            self.order(self.quote(), key="request_key_00002")

    def test_deposit_and_payout_require_verified_proof(self):
        order = self.order()
        with self.assertRaises(SwapError):
            self.engine.settle(dict(order_id=order["order_id"], tx="0x" + TX))
        self.chains.valid = False
        with self.assertRaises(SwapError):
            self.engine.deposit(dict(order_id=order["order_id"], token=TOKEN, tx=TX))
        self.chains.valid = True
        self.engine.deposit(dict(order_id=order["order_id"], token=TOKEN, tx=TX))
        self.chains.valid = False
        with self.assertRaises(SwapError):
            self.engine.settle(dict(order_id=order["order_id"], tx="0x" + TX))
        self.chains.valid = True
        completed = self.engine.settle(dict(order_id=order["order_id"], tx="0x" + TX))
        self.assertEqual(completed["state"], "completed")
        self.assertEqual(completed, self.engine.settle(dict(order_id=order["order_id"], tx="0x" + TX)))
        with sqlite3.connect(self.path) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM audit").fetchone()[0], 3)

    def test_network_identity_cannot_change_for_existing_database(self):
        self.order()
        self.config["assets"]["USDT"]["chain_id"] = 56
        with self.assertRaises(SwapError):
            self.quote()

    def test_same_transaction_cannot_pay_two_orders(self):
        self.config["assets"]["POVIX"]["treasury_addresses"] += ["e" * 64]
        self.config["assets"]["POVIX"]["deposit_addresses"] += ["e" * 64]
        one = self.order()
        two = self.order(key="request_key_00002")
        self.engine.deposit(dict(order_id=one["order_id"], token=TOKEN, tx=TX))
        with self.assertRaises(SwapError):
            self.engine.deposit(dict(order_id=two["order_id"], token=TOKEN, tx=TX))


class ChainTests(unittest.TestCase):
    def setUp(self):
        self.config = config()
        self.chains = Chains(self.config)
        self.sender = "0x" + "e" * 40
        self.tx = "0x" + TX
        self.receipt = dict(transactionHash=self.tx, status="0x1", blockNumber="0x64", blockHash="0xabc", logs=[dict(address=self.config["assets"]["USDT"]["contract"], topics=[TRANSFER, "0x" + self.sender[2:].zfill(64), "0x" + EVM_BANK[2:].zfill(64)], data=hex(1000000))])
        self.responses = {"eth_chainId": "0x1", "eth_call": "0x6", "eth_blockNumber": "0x66", "eth_getTransactionReceipt": self.receipt, "eth_getBlockByNumber": dict(hash="0xabc", timestamp=hex(1000))}
        self.chains.rpc = lambda method, params: self.responses[method]

    def verify(self, purpose="deposit"):
        return self.chains.verify("USDT", self.tx, EVM_BANK, 1000000, purpose, 1000)

    def test_real_adapter_validates_contract_amount_destination_finality(self):
        self.assertEqual(self.verify(), self.tx)
        self.receipt["logs"][0]["address"] = "0x" + "f" * 40
        with self.assertRaises(SwapError):
            self.verify()
        self.receipt["logs"][0]["address"] = self.config["assets"]["USDT"]["contract"]
        self.receipt["logs"][0]["data"] = hex(999999)
        with self.assertRaises(SwapError):
            self.verify()

    def test_wrong_network_failed_tx_removed_logs_and_reorg_rejected(self):
        for method, changed in [("eth_chainId", "0x38"), ("eth_blockNumber", "0x65"), ("eth_getBlockByNumber", dict(hash="0xother", timestamp=hex(1000)))]:
            old = self.responses[method]
            self.responses[method] = changed
            with self.assertRaises(SwapError):
                self.verify()
            self.responses[method] = old
        self.receipt["status"] = "0x0"
        with self.assertRaises(SwapError):
            self.verify()
        self.receipt["status"] = "0x1"
        self.receipt["logs"][0]["removed"] = True
        with self.assertRaises(SwapError):
            self.verify()

    def test_payout_must_come_from_treasury_and_deposit_cannot_be_internal(self):
        with self.assertRaises(SwapError):
            self.verify("payout")
        self.receipt["logs"][0]["topics"][1] = "0x" + EVM_BANK[2:].zfill(64)
        self.assertEqual(self.verify("payout"), self.tx)
        with self.assertRaises(SwapError):
            self.verify("deposit")

    def test_native_requires_transfer_and_confirmed_matching_output(self):
        proof = dict(kind="transfer", confirmations=2, timestamp=1000, input_addresses=[POVIX_DEST], outputs=[dict(address=POVIX_BANK, amount_atomic="100000000")])
        self.chains.native = lambda action, body: proof
        self.assertEqual(self.chains.verify("POVIX", TX, POVIX_BANK, 100000000, "deposit", 1000), TX)
        for field, bad in [("kind", "issuance_or_stake"), ("confirmations", 1), ("timestamp", 999)]:
            old = proof[field]
            proof[field] = bad
            with self.assertRaises(SwapError):
                self.chains.verify("POVIX", TX, POVIX_BANK, 100000000, "deposit", 1000)
            proof[field] = old

def bnb_config():
    cfg = config()
    cfg["evm_asset"] = "BNB"
    cfg.pop("povix_usdt")
    cfg["povix_bnb"] = "0.00025"
    cfg["minimum"] = {"POVIX": "0.01", "BNB": "0.000001"}
    cfg["maximum"] = {"POVIX": "10000", "BNB": "5"}
    evm = cfg["assets"].pop("USDT")
    evm.pop("contract")
    evm["chain_id"] = 56
    evm["network"] = "BNB Smart Chain fixture"
    cfg["assets"]["BNB"] = evm
    return cfg


class BnbEngineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = self.directory.name + "/bnb.db"
        self.config = bnb_config()
        self.chains = FixtureChains()
        self.chains.balances["BNB"] = 10**18
        self.engine = Engine(self.path, self.config, self.chains, clock=lambda: 1000)

    def tearDown(self):
        self.directory.cleanup()

    def quote(self, source="POVIX", amount="100"):
        return self.engine.quote(dict(source=source, amount=amount, destination=EVM_DEST if source == "POVIX" else POVIX_DEST))

    def order(self, quote=None, key="request_key_00001"):
        return self.engine.order(dict(quote_id=(quote or self.quote())["quote_id"], token=TOKEN), key)

    def test_native_bnb_precision_and_bidirectional_quote(self):
        self.assertEqual(atomic("0,000000000000000001", "BNB"), 1)
        self.assertEqual(atomic("9.223372036854775807", "BNB"), 2**63 - 1)
        for bad in ("0.0000000000000000001", "9.223372036854775808", "10", "1e-18"):
            with self.assertRaises(SwapError):
                atomic(bad, "BNB")
        q = self.quote()
        self.assertEqual(q["target"], "BNB")
        self.assertEqual(q["received"], "0.024925000000000000")
        reverse = self.quote("BNB", "0.025")
        self.assertEqual(reverse["received"], "99.70000000")
        self.assertEqual(reverse["fee"], "0.000075000000000000")
        with self.assertRaises(SwapError):
            self.quote("USDT", "1")

    def test_bnb_rounding_never_mints_value(self):
        self.config["minimum"]["BNB"] = "0.000000000000000001"
        with self.assertRaises(SwapError):
            self.quote("BNB", "0.000000000000000001")
        self.config["povix_bnb"] = "0.000000000000000001"
        self.assertEqual(self.quote()["received"], "0.000000000000000099")

    def test_bnb_orders_settle_and_remain_idempotent_after_restart(self):
        for source, amount in (("POVIX", "100"), ("BNB", "0.025")):
            q = self.quote(source, amount)
            order = self.order(q, "request_key_" + source + "_00001")
            incoming = TX if source == "POVIX" else "0x" + "d" * 64
            outgoing = "0x" + TX if source == "POVIX" else "d" * 64
            self.engine.deposit(dict(order_id=order["order_id"], token=TOKEN, tx=incoming))
            completed = self.engine.settle(dict(order_id=order["order_id"], tx=outgoing))
            self.assertEqual(completed["state"], "completed")
            self.engine = Engine(self.path, self.config, self.chains, clock=lambda: 2000)
            self.assertEqual(completed, self.order(q, "request_key_" + source + "_00001"))

    def test_bnb_reserve_buffer_and_concurrent_orders(self):
        output = 24925000000000000
        self.config["reserve_buffer_atomic"] = {"BNB": 10**16}
        self.chains.balances["BNB"] = output + 10**16
        self.config["assets"]["POVIX"]["treasury_addresses"].append("e" * 64)
        self.config["assets"]["POVIX"]["deposit_addresses"].append("e" * 64)
        quotes = [self.quote(), self.quote()]
        def attempt(index):
            try:
                return self.order(quotes[index], "request_key_0000" + str(index))
            except SwapError:
                return None
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(attempt, [0, 1]))
        self.assertEqual(sum(r is not None for r in results), 1)

    def test_bnb_contract_destination_rejected_before_order(self):
        self.chains.valid = False
        with self.assertRaises(SwapError):
            self.quote()
        self.chains.valid = True
        q = self.quote()
        self.chains.valid = False
        with self.assertRaises(SwapError):
            self.order(q)
        with sqlite3.connect(self.path) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM orders").fetchone()[0], 0)

    def test_bnb_database_cannot_switch_asset_or_chain(self):
        self.order()
        self.config["assets"]["BNB"]["chain_id"] = 97
        with self.assertRaises(SwapError):
            self.quote()
        self.engine.config = config()
        with self.assertRaises(SwapError):
            self.engine.quote(dict(source="POVIX", amount="100", destination=EVM_DEST))

    def test_bnb_rejects_token_config_wrong_chain_and_disabled_service(self):
        for field, value in (("contract", "0x" + "d" * 40), ("chain_id", 1)):
            self.config["assets"]["BNB"][field] = value
            with self.assertRaises(SwapError):
                self.quote()
            self.config = bnb_config()
            self.engine.config = self.config
        self.config["enabled"] = False
        with self.assertRaises(SwapError):
            self.order()


class BnbChainTests(unittest.TestCase):
    def setUp(self):
        self.config = bnb_config()
        self.chains = Chains(self.config)
        self.sender = "0x" + "e" * 40
        self.tx = "0x" + TX
        self.receipt = dict(transactionHash=self.tx, status="0x1", blockNumber="0x64", blockHash="0xabc", logs=[])
        self.transfer = dict(hash=self.tx, blockNumber="0x64", blockHash="0xabc", to=EVM_BANK, value=hex(10**18), input="0x")
        self.transfer["from"] = self.sender
        self.responses = {"eth_chainId": "0x38", "eth_blockNumber": "0x66", "eth_getTransactionReceipt": self.receipt, "eth_getTransactionByHash": self.transfer, "eth_getBlockByNumber": dict(hash="0xabc", timestamp=hex(1000)), "eth_getCode": "0x"}
        self.calls = []
        def rpc(method, params):
            self.calls.append((method, params))
            if method == "eth_getBalance":
                return hex(2 * 10**18 if params[1] == "latest" else 3 * 10**18)
            return self.responses[method]
        self.chains.rpc = rpc

    def verify(self, purpose="deposit"):
        return self.chains.verify("BNB", self.tx, EVM_BANK, 10**18, purpose, 1000)

    def test_bnb_direct_native_transfer_and_balance_without_token_calls(self):
        self.assertEqual(self.verify(), self.tx)
        self.assertEqual(self.chains.balance("BNB"), 2 * 10**18)
        self.assertEqual(self.chains.address_balance("BNB", EVM_BANK), 2 * 10**18)
        self.assertFalse(any(method == "eth_call" for method, _ in self.calls))
        self.assertIn(("eth_getBalance", [EVM_BANK, "0x64"]), self.calls)

    def test_bnb_failed_pending_reorganized_old_and_wrong_chain_rejected(self):
        for method, bad in (("eth_chainId", "0x61"), ("eth_blockNumber", "0x65"), ("eth_getTransactionReceipt", None), ("eth_getTransactionByHash", None), ("eth_getBlockByNumber", dict(hash="0xother", timestamp=hex(1000))), ("eth_getBlockByNumber", dict(hash="0xabc", timestamp=hex(999)))):
            old = self.responses[method]
            self.responses[method] = bad
            with self.assertRaises(SwapError):
                self.verify()
            self.responses[method] = old
        self.receipt["status"] = "0x0"
        with self.assertRaises(SwapError):
            self.verify()

    def test_bnb_wrong_amount_destination_contract_calls_and_wrapped_tokens_rejected(self):
        for field, bad in (("value", "0x0"), ("value", hex(10**18 - 1)), ("to", EVM_DEST), ("to", None), ("hash", "0x" + "d" * 64), ("blockHash", "0xother"), ("blockNumber", "0x63"), ("input", "0xa9059cbb"), ("input", None)):
            old = self.transfer[field]
            self.transfer[field] = bad
            with self.assertRaises(SwapError):
                self.verify()
            self.transfer[field] = old
        self.responses["eth_getCode"] = "0x6000"
        with self.assertRaises(SwapError):
            self.verify()
        with self.assertRaises(SwapError):
            self.chains.bnb_wallet(EVM_DEST)
        with self.assertRaises(SwapError):
            self.chains.address_balance("BNB", EVM_BANK)

    def test_bnb_payout_origin_and_internal_deposits(self):
        with self.assertRaises(SwapError):
            self.verify("payout")
        self.transfer["from"] = EVM_BANK
        self.assertEqual(self.verify("payout"), self.tx)
        with self.assertRaises(SwapError):
            self.verify("deposit")


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        class TestHandler(Handler):
            def log_message(self, *args):
                pass
        self.handler = TestHandler
        cfg = config()
        cfg["enabled"] = False
        self.handler.engine = Engine(self.directory.name + "/orders.db", cfg, FixtureChains())
        self.handler.allowed_origin = "https://privatecoin.site"
        self.handler.admin_token = "admin" * 10
        self.http = ThreadingHTTPServer(("127.0.0.1", 0), self.handler)
        self.thread = threading.Thread(target=self.http.serve_forever, daemon=True)
        self.thread.start()
        self.base = "http://127.0.0.1:" + str(self.http.server_address[1])

    def tearDown(self):
        self.http.shutdown()
        self.http.server_close()
        self.thread.join()
        self.directory.cleanup()

    def post(self, path, headers=None):
        request = urllib.request.Request(self.base + path, data=b"{}", headers=headers or {"Content-Type": "application/json"})
        try:
            response = urllib.request.urlopen(request, timeout=5)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, json.load(response)

    def test_unconfigured_service_reports_unavailable_and_no_invoice(self):
        with urllib.request.urlopen(self.base + "/swap-api/status") as response:
            self.assertEqual(response.headers["Cache-Control"], "no-store")
            self.assertFalse(json.load(response)["ready"])
        code, payload = self.post("/swap-api/quote")
        self.assertEqual(code, 409)
        self.assertNotIn("deposit_address", payload)

    def test_bnb_status_reports_pair_and_precision_even_when_disabled(self):
        cfg = bnb_config()
        self.handler.engine.config = cfg
        self.handler.engine.chains.balances["BNB"] = 10**18
        for enabled in (False, True):
            cfg["enabled"] = enabled
            with urllib.request.urlopen(self.base + "/swap-api/status") as response:
                status = json.load(response)
            self.assertEqual(status["pair"], "POVIX/BNB")
            self.assertEqual(status["ready"], enabled)
            self.assertEqual(status["assets"][1]["decimals"], 18)
            self.assertEqual(status["settlement"], "manual_verified")
            self.assertNotIn("deposit_address", status)

    def test_admin_authentication_and_origin_enforcement(self):
        self.assertEqual(self.post("/swap-api/admin/queue")[0], 401)
        self.assertEqual(self.post("/swap-api/admin/queue", {"Content-Type": "application/json", "Authorization": "Bearer " + self.handler.admin_token})[0], 200)
        self.assertEqual(self.post("/swap-api/quote", {"Content-Type": "application/json", "Origin": "https://other.example"})[0], 403)
        self.assertEqual(self.post("/swap-api/quote", {"Content-Type": "text/plain"})[0], 415)

if __name__ == "__main__":
    unittest.main(verbosity=2)
