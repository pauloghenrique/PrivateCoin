"""Run behind an HTTPS reverse proxy; default binding is loopback only."""
import argparse
import collections
import json
import logging
import os
import secrets
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Lock
from chains import Chains
from engine import Engine, SwapError

class Handler(BaseHTTPRequestHandler):
    engine = None
    admin_token = None
    allowed_origin = None
    buckets = collections.OrderedDict()
    bucket_lock = Lock()

    def setup(self):
        super().setup()
        self.connection.settimeout(15)

    def log_message(self, fmt, *args):
        # Do not log access codes, authorization headers, payloads or query strings.
        logging.info("HTTP %s", self.command)

    def reply(self, code, result):
        data = json.dumps(result, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path != "/swap-api/status":
            return self.reply(404, {"error": "Rota inexistente."})
        try:
            self.engine.ready()
            # Both chains must be reachable and correctly configured.
            for symbol in ("POVIX", "USDT"):
                balance = self.engine.chains.balance(symbol)
                if balance <= int(self.engine.config.get("reserve_buffer_atomic", {}).get(symbol, 0)):
                    raise SwapError("Reservas não financiadas.")
            ready, message = True, "Tesouraria configurada. Cada cotação depende da liquidez disponível."
        except (SwapError, KeyError, TypeError, ValueError):
            ready, message = False, "Trocas indisponíveis: configuração, rede ou reservas não verificadas."
        self.reply(200, dict(ready=ready, message=message, pair="POVIX/USDT", settlement="manual_verified"))

    def limited(self):
        # TRUSTED proxy must overwrite client IP; public exposure is discouraged.
        key = self.headers.get("X-Real-IP", self.client_address[0])
        now = time.monotonic()
        with self.bucket_lock:
            values = self.buckets.pop(key, [])
            values = [v for v in values if now - v < 60]
            values.append(now)
            self.buckets[key] = values
            while len(self.buckets) > 10000:
                self.buckets.popitem(last=False)
            return len(values) > 30

    def do_POST(self):
        if self.limited():
            return self.reply(429, {"error": "Muitas solicitações. Aguarde um minuto."})
        try:
            admin = self.path.startswith("/swap-api/admin/")
            if admin:
                supplied = self.headers.get("Authorization", "")
                if not self.admin_token or not secrets.compare_digest(supplied, "Bearer " + self.admin_token):
                    return self.reply(401, {"error": "Acesso restrito à tesouraria."})
            origin = self.headers.get("Origin")
            if origin and origin != self.allowed_origin:
                return self.reply(403, {"error": "Origem não autorizada."})
            if self.headers.get("Content-Type", "").split(";")[0].strip().lower() != "application/json":
                return self.reply(415, {"error": "Envie JSON."})
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 8192:
                return self.reply(413, {"error": "Tamanho de solicitação inválido."})
            data = json.loads(self.rfile.read(length))
            if not isinstance(data, dict):
                raise SwapError("Solicitação inválida.")
            routes = {
                "/swap-api/quote": lambda: self.engine.quote(data),
                "/swap-api/order": lambda: self.engine.order(data, self.headers.get("Idempotency-Key")),
                "/swap-api/order/status": lambda: self.engine.status(data),
                "/swap-api/order/deposit": lambda: self.engine.deposit(data),
                "/swap-api/admin/queue": lambda: {"orders": self.engine.queue()},
                "/swap-api/admin/settle": lambda: self.engine.settle(data),
            }
            if self.path not in routes:
                return self.reply(404, {"error": "Rota inexistente."})
            self.reply(200, routes[self.path]())
        except SwapError as error:
            self.reply(409, {"error": str(error)})
        except (json.JSONDecodeError, ValueError, TypeError, KeyError):
            self.reply(400, {"error": "Solicitação ou resposta de rede inválida."})
        except Exception:
            logging.exception("Falha interna no serviço de swap")
            self.reply(503, {"error": "Serviço temporariamente indisponível. Consulte sua ordem antes de tentar novamente."})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--database", required=True)
    parser.add_argument("--port", type=int, default=8787)
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text())
    Path(args.database).parent.mkdir(parents=True, exist_ok=True)
    Handler.engine = Engine(args.database, config, Chains(config))
    Handler.admin_token = os.environ.get("POVIX_SWAP_ADMIN_TOKEN")
    if Handler.admin_token and len(Handler.admin_token) < 32:
        raise SystemExit("POVIX_SWAP_ADMIN_TOKEN deve ter pelo menos 32 caracteres.")
    Handler.allowed_origin = config.get("public_origin")
    logging.basicConfig(level=logging.INFO)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.daemon_threads = True
    logging.info("Swap API em loopback, porta %s; operação habilitada: %s", args.port, config.get("enabled") is True)
    server.serve_forever()

if __name__ == "__main__":
    main()
