"""Custodial cross-chain POVIX/USDT or POVIX/BNB broker. No private keys and no simulated funds."""
import hashlib
import json
import re
import secrets
import sqlite3
import time
from contextlib import contextmanager
from decimal import Decimal, InvalidOperation

DECIMALS = {"POVIX": 8, "USDT": 6, "BNB": 18}
SQLITE_MAX = 2**63 - 1


def evm_symbol(config):
    symbol = config.get("evm_asset", "USDT")
    if symbol not in ("USDT", "BNB"):
        raise SwapError("Ativo EVM não suportado.")
    return symbol

class SwapError(Exception):
    pass


def atomic(value, symbol):
    if not isinstance(value, str) or not re.fullmatch(r"\d{1,12}(?:[.,]\d{1," + str(max(8, DECIMALS[symbol])) + r"})?", value):
        raise SwapError("Quantidade inválida; use ponto ou vírgula decimal, sem milhares.")
    number = Decimal(value.replace(",", ".")) * 10 ** DECIMALS[symbol]
    if number != number.to_integral_value() or not 0 < number <= (SQLITE_MAX if symbol == "BNB" else 10**15):
        raise SwapError("Quantidade fora do limite ou com casas decimais excessivas.")
    return int(number)


def display(amount, symbol):
    unit = 10 ** DECIMALS[symbol]
    return f"{amount // unit}.{amount % unit:0{DECIMALS[symbol]}d}"


def address(value, symbol):
    pattern = r"0x[0-9a-fA-F]{40}" if symbol in ("USDT", "BNB") else r"[0-9a-fA-F]{64}"
    if not isinstance(value, str) or not re.fullmatch(pattern, value):
        raise SwapError("Endereço de destino inválido para a rede selecionada.")
    if int(value.removeprefix("0x"), 16) == 0:
        raise SwapError("O endereço zero não é aceito.")
    return value.lower()


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


class Engine:
    def __init__(self, path, config, chains, clock=time.time):
        self.path, self.config, self.chains, self.clock = path, config, chains, clock
        with self.transaction() as db:
            db.executescript('''
                CREATE TABLE IF NOT EXISTS settings (name TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS quotes (
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, target TEXT NOT NULL,
                    amount INTEGER NOT NULL, output INTEGER NOT NULL, fee INTEGER NOT NULL,
                    destination TEXT NOT NULL, expires INTEGER NOT NULL, fingerprint TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS orders (
                    id TEXT PRIMARY KEY, token_hash TEXT NOT NULL, quote_id TEXT UNIQUE NOT NULL,
                    request_hash TEXT UNIQUE NOT NULL, source TEXT NOT NULL, target TEXT NOT NULL,
                    amount INTEGER NOT NULL, output INTEGER NOT NULL, destination TEXT NOT NULL,
                    deposit_address TEXT NOT NULL, state TEXT NOT NULL, created INTEGER NOT NULL,
                    deposit_tx TEXT, payout_tx TEXT,
                    UNIQUE(source, deposit_address));
                CREATE TABLE IF NOT EXISTS proofs (
                    asset TEXT NOT NULL, tx TEXT NOT NULL, purpose TEXT NOT NULL,
                    order_id TEXT NOT NULL, PRIMARY KEY(asset, tx));
                CREATE TABLE IF NOT EXISTS audit (
                    sequence INTEGER PRIMARY KEY, at INTEGER NOT NULL,
                    order_id TEXT NOT NULL, event TEXT NOT NULL, details TEXT NOT NULL);
            ''')

    @contextmanager
    def transaction(self):
        db = sqlite3.connect(self.path, timeout=10, isolation_level=None)
        db.row_factory = sqlite3.Row
        try:
            db.execute("PRAGMA journal_mode=WAL")
            db.execute("PRAGMA synchronous=FULL")
            db.execute("BEGIN IMMEDIATE")
            yield db
            db.commit()
        except Exception:
            db.rollback()
            raise
        finally:
            db.close()

    def fingerprint(self):
        return digest(json.dumps(self.config, sort_keys=True))

    def ready(self):
        if self.config.get("enabled") is not True:
            raise SwapError("Operação indisponível: tesouraria ainda não ativada.")
        try:
            evm_asset = evm_symbol(self.config)
            price = atomic(self.config["povix_" + evm_asset.lower()], "BNB" if evm_asset == "BNB" else "POVIX")
            fee = self.config["fee_bps"]
            if not 0 < price <= (SQLITE_MAX if evm_asset == "BNB" else 10**14) or type(fee) is not int or not 0 <= fee <= 1000:
                raise ValueError()
            for symbol in ("POVIX", evm_asset):
                data = self.config["assets"][symbol]
                treasury = {address(a, symbol) for a in data["treasury_addresses"]}
                deposits = {address(a, symbol) for a in data["deposit_addresses"]}
                if len(treasury) != len(data["treasury_addresses"]):
                    raise ValueError()
                if not treasury or not deposits or not deposits.issubset(treasury):
                    raise ValueError()
                if len(deposits) != len(data["deposit_addresses"]):
                    raise ValueError()
                if type(data["confirmations"]) is not int or data["confirmations"] < 1:
                    raise ValueError()
                atomic(self.config["minimum"][symbol], symbol)
                atomic(self.config["maximum"][symbol], symbol)
            evm = self.config["assets"][evm_asset]
            if evm_asset == "USDT":
                address(evm["contract"], "USDT")
            elif evm.get("contract") is not None or evm["chain_id"] not in (56, 97):
                raise ValueError()
            if type(evm["chain_id"]) is not int or evm["chain_id"] < 1 or not evm["rpc_url"]:
                raise ValueError()
            if not self.config["assets"]["POVIX"]["api_url"]:
                raise ValueError()
            return price, fee
        except (KeyError, ValueError, TypeError, InvalidOperation):
            raise SwapError("Configuração incompleta da tesouraria.")

    def bind_networks(self, db):
        symbol = evm_symbol(self.config)
        native, evm = self.config["assets"]["POVIX"], self.config["assets"][symbol]
        # Keep the legacy USDT binding; native BNB has its own identity.
        binding = json.dumps([native["network_id"], native["genesis_hash"], evm["chain_id"], evm["contract"].lower() if symbol == "USDT" else "native:BNB"])
        existing = db.execute("SELECT value FROM settings WHERE name='networks'").fetchone()
        if existing and existing[0] != binding:
            raise SwapError("A rede deste banco de ordens não pode ser alterada.")
        db.execute("INSERT OR IGNORE INTO settings VALUES ('networks',?)", (binding,))

    def funds(self, db, symbol, needed):
        self.bind_networks(db)
        # Live, finalized on-chain reserves, queried inside the reservation lock.
        balance = self.chains.balance(symbol)
        if not isinstance(balance, int) or balance < 0:
            raise SwapError("Não foi possível verificar as reservas.")
        reserved = db.execute("SELECT COALESCE(SUM(output),0) FROM orders WHERE target=? AND state!='completed'", (symbol,)).fetchone()[0]
        buffer = int(self.config.get("reserve_buffer_atomic", {}).get(symbol, 0))
        if buffer < 0 or balance - reserved - buffer < needed:
            raise SwapError("Liquidez disponível insuficiente para esta troca.")

    def quote(self, data):
        price, fee_bps = self.ready()
        source = data.get("source")
        evm_asset = evm_symbol(self.config)
        if source not in ("POVIX", evm_asset):
            raise SwapError("Par não suportado.")
        target = evm_asset if source == "POVIX" else "POVIX"
        destination = address(data.get("destination"), target)
        if target == "BNB":
            self.chains.bnb_wallet(destination)
        amount = atomic(data.get("amount"), source)
        if not atomic(self.config["minimum"][source], source) <= amount <= atomic(self.config["maximum"][source], source):
            raise SwapError("Quantidade fora dos limites da operação.")
        fee = (amount * fee_bps + 9999) // 10000
        net = amount - fee
        # Price is 1 POVIX in the selected EVM asset. Round down once.
        price_scale = 10**(18 if evm_asset == "BNB" else 8)
        evm_unit = 10**DECIMALS[evm_asset]
        output = net * price * evm_unit // (10**8 * price_scale) if source == "POVIX" else net * 10**8 * price_scale // (price * evm_unit)
        if output <= 0 or output > (SQLITE_MAX if target == "BNB" else 10**15):
            raise SwapError("Recebimento fora dos limites.")
        identity = secrets.token_urlsafe(32)
        expires = int(self.clock()) + 120
        with self.transaction() as db:
            self.funds(db, target, output)
            db.execute("INSERT INTO quotes VALUES (?,?,?,?,?,?,?,?,?)", (identity, source, target, amount, output, fee, destination, expires, self.fingerprint()))
        return dict(quote_id=identity, source=source, target=target, amount=display(amount, source), received=display(output, target), fee=display(fee, source), destination=destination, expires=expires, network=self.config["assets"][target]["network"], execution="Pagamento pela tesouraria, após depósito confirmado")

    def order(self, data, request_key):
        if not isinstance(request_key, str) or not re.fullmatch(r"[a-zA-Z0-9_-]{16,128}", request_key):
            raise SwapError("Chave de idempotência inválida.")
        token = data.get("token")
        if not isinstance(token, str) or not re.fullmatch(r"[a-zA-Z0-9_-]{32,128}", token):
            raise SwapError("Código de acesso inválido.")
        with self.transaction() as db:
            existing = db.execute("SELECT * FROM orders WHERE request_hash=?", (digest(request_key),)).fetchone()
            if existing:
                if existing["quote_id"] != data.get("quote_id") or not secrets.compare_digest(existing["token_hash"], digest(token)):
                    raise SwapError("Chave de idempotência já utilizada.")
                return self.public(existing)
            self.ready()
            quote = db.execute("SELECT * FROM quotes WHERE id=?", (data.get("quote_id"),)).fetchone()
            if not quote or quote["expires"] <= self.clock() or quote["fingerprint"] != self.fingerprint():
                raise SwapError("Cotação expirada. Solicite uma nova cotação.")
            if db.execute("SELECT 1 FROM orders WHERE quote_id=?", (quote["id"],)).fetchone():
                raise SwapError("Cotação já utilizada.")
            if db.execute("SELECT COUNT(*) FROM orders WHERE state!='completed'").fetchone()[0] >= self.config.get("max_open_orders", 100):
                raise SwapError("Limite de ordens abertas atingido. Tente mais tarde.")
            if quote["target"] == "BNB":
                self.chains.bnb_wallet(quote["destination"])
            self.funds(db, quote["target"], quote["output"])
            used = {row[0] for row in db.execute("SELECT deposit_address FROM orders WHERE source=?", (quote["source"],))}
            available = [address(a, quote["source"]) for a in self.config["assets"][quote["source"]]["deposit_addresses"] if address(a, quote["source"]) not in used]
            if not available:
                raise SwapError("Não há endereços de depósito disponíveis. Tente mais tarde.")
            deposit = available[0]
            # Prevent preexisting balances from being claimed as this order's deposit.
            if self.chains.address_balance(quote["source"], deposit) != 0:
                raise SwapError("Endereço de depósito não está vazio. A tesouraria deve substituir este endereço.")
            identity = secrets.token_urlsafe(24)
            db.execute("INSERT INTO orders VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)", (identity, digest(token), quote["id"], digest(request_key), quote["source"], quote["target"], quote["amount"], quote["output"], quote["destination"], deposit, "awaiting_deposit", int(self.clock()), None, None))
            self.audit(db, identity, "order_created", "Reserva de liquidez e endereço exclusivos")
            return self.public(db.execute("SELECT * FROM orders WHERE id=?", (identity,)).fetchone())

    def audit(self, db, identity, event, details):
        db.execute("INSERT INTO audit(at,order_id,event,details) VALUES (?,?,?,?)", (int(self.clock()), identity, event, details))

    def public(self, row):
        return dict(order_id=row["id"], source=row["source"], target=row["target"], amount=display(row["amount"], row["source"]), received=display(row["output"], row["target"]), destination=row["destination"], deposit_address=row["deposit_address"], state=row["state"], deposit_tx=row["deposit_tx"], payout_tx=row["payout_tx"], network=self.config["assets"][row["source"]]["network"], confirmations=self.config["assets"][row["source"]]["confirmations"])

    def authorized(self, db, identity, token):
        row = db.execute("SELECT * FROM orders WHERE id=?", (identity,)).fetchone()
        if not row or not isinstance(token, str) or not secrets.compare_digest(row["token_hash"], digest(token)):
            raise SwapError("Ordem ou código de acesso inválido.")
        return row

    def status(self, data):
        with self.transaction() as db:
            return self.public(self.authorized(db, data.get("order_id"), data.get("token")))

    def deposit(self, data):
        with self.transaction() as db:
            self.bind_networks(db)
            row = self.authorized(db, data.get("order_id"), data.get("token"))
            if row["state"] != "awaiting_deposit":
                return self.public(row)
            tx = self.chains.verify(row["source"], data.get("tx"), row["deposit_address"], row["amount"], "deposit", row["created"])
            try:
                db.execute("INSERT INTO proofs VALUES (?,?,?,?)", (row["source"], tx, "deposit", row["id"]))
            except sqlite3.IntegrityError:
                raise SwapError("Transação já vinculada a uma ordem.")
            db.execute("UPDATE orders SET state='payment_review', deposit_tx=? WHERE id=?", (tx, row["id"]))
            self.audit(db, row["id"], "deposit_verified", tx)
            return self.public(db.execute("SELECT * FROM orders WHERE id=?", (row["id"],)).fetchone())

    def queue(self):
        with self.transaction() as db:
            return [self.public(row) for row in db.execute("SELECT * FROM orders WHERE state='payment_review' ORDER BY created")]

    def settle(self, data):
        # Operator sends from a separate wallet. This service only verifies finality.
        with self.transaction() as db:
            self.bind_networks(db)
            row = db.execute("SELECT * FROM orders WHERE id=?", (data.get("order_id"),)).fetchone()
            if not row or row["state"] not in ("payment_review", "completed"):
                raise SwapError("A ordem não possui depósito confirmado.")
            if row["state"] == "completed":
                if not isinstance(data.get("tx"), str) or row["payout_tx"] != data["tx"].lower():
                    raise SwapError("Ordem já liquidada com outra transação.")
                return self.public(row)
            # Recheck the incoming proof immediately before accepting settlement.
            self.chains.verify(row["source"], row["deposit_tx"], row["deposit_address"], row["amount"], "deposit", row["created"])
            tx = self.chains.verify(row["target"], data.get("tx"), row["destination"], row["output"], "payout", row["created"])
            try:
                db.execute("INSERT INTO proofs VALUES (?,?,?,?)", (row["target"], tx, "payout", row["id"]))
            except sqlite3.IntegrityError:
                raise SwapError("Pagamento já utilizado em outra ordem.")
            db.execute("UPDATE orders SET state='completed', payout_tx=? WHERE id=?", (tx, row["id"]))
            self.audit(db, row["id"], "payout_verified", tx)
            return self.public(db.execute("SELECT * FROM orders WHERE id=?", (row["id"],)).fetchone())
