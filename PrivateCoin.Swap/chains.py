"""Read-only chain verification. USDT is pinned to a configured chain and contract."""
import json
import os
import re
import urllib.error
import urllib.request
from engine import SwapError, address

TRANSFER = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"

class Chains:
    def __init__(self, config):
        self.config = config

    def request(self, url, body, native=False):
        headers = {"Content-Type": "application/json"}
        if native:
            token = os.environ.get("POVIX_SWAP_NETWORK_TOKEN")
            if not token or len(token) < 32:
                raise SwapError("Credencial do nó POVIX não configurada.")
            headers["Authorization"] = "Bearer " + token
        request = urllib.request.Request(url, data=json.dumps(body).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                payload = response.read(2_000_001)
                if len(payload) > 2_000_000:
                    raise SwapError("Resposta da rede excede o limite.")
                return json.loads(payload)
        except (urllib.error.URLError, ValueError, TimeoutError):
            raise SwapError("Não foi possível consultar a blockchain. Nenhuma operação foi confirmada.")

    def rpc(self, method, params):
        rpc_url = self.config["assets"]["USDT"]["rpc_url"]
        result = self.request(rpc_url, dict(jsonrpc="2.0", id=1, method=method, params=params))
        if not isinstance(result, dict) or result.get("error") or "result" not in result:
            raise SwapError("A consulta RPC falhou.")
        return result["result"]

    def evm_context(self):
        asset = self.config["assets"]["USDT"]
        contract = address(asset["contract"], "USDT")
        if int(self.rpc("eth_chainId", []), 16) != asset["chain_id"]:
            raise SwapError("O RPC está conectado a outra rede.")
        if int(self.rpc("eth_call", [{"to": contract, "data": "0x313ce567"}, "latest"]), 16) != 6:
            raise SwapError("O contrato configurado não usa 6 casas decimais.")
        head = int(self.rpc("eth_blockNumber", []), 16)
        safe = head - asset["confirmations"] + 1
        if safe < 0:
            raise SwapError("A rede não possui confirmações suficientes.")
        return asset, contract, head, safe

    def evm_balance(self, address_value, block, contract):
        return int(self.rpc("eth_call", [{"to": contract, "data": "0x70a08231" + address_value[2:].zfill(64)}, block]), 16)

    def native(self, action, body):
        asset = self.config["assets"]["POVIX"]
        result = self.request(asset["api_url"].rstrip("/") + "/" + action, body, native=True)
        if result.get("error") or result.get("network_id") != asset["network_id"] or result.get("genesis_hash") != asset["genesis_hash"] or result.get("connected_peers", 0) < 1:
            raise SwapError("Nó POVIX não sincronizado ou identidade de rede incorreta.")
        return result

    def balance(self, symbol):
        asset = self.config["assets"][symbol]
        if symbol == "POVIX":
            result = self.native("Balance", dict(addresses=sorted({address(a, symbol) for a in asset["treasury_addresses"]}), confirmations=asset["confirmations"]))
            return int(result["amount_atomic"])
        _, contract, _, safe = self.evm_context()
        return sum(self.evm_balance(address(a, symbol), hex(safe), contract) for a in {address(a, symbol) for a in asset["treasury_addresses"]})

    def address_balance(self, symbol, destination):
        if symbol == "POVIX":
            # Includes the current tip; an allocated address must be empty now too.
            return int(self.native("Balance", dict(addresses=[destination], confirmations=1))["amount_atomic"])
        _, contract, _, _ = self.evm_context()
        return self.evm_balance(destination, "latest", contract)

    def verify(self, symbol, tx, destination, amount, purpose, created):
        pattern = r"0x[0-9a-fA-F]{64}" if symbol == "USDT" else r"[0-9a-fA-F]{64}"
        if not isinstance(tx, str) or not re.fullmatch(pattern, tx):
            raise SwapError("Identificador de transação inválido.")
        tx = tx.lower()
        asset = self.config["assets"][symbol]
        treasury = {address(a, symbol) for a in asset["treasury_addresses"]}
        if symbol == "POVIX":
            proof = self.native("Transaction", dict(tx=tx))
            if proof.get("kind") != "transfer" or proof.get("confirmations", 0) < asset["confirmations"] or proof.get("timestamp", 0) < created:
                raise SwapError("Transferência POVIX ausente, anterior à ordem ou sem confirmações suficientes.")
            outputs = proof.get("outputs", [])
            inputs = proof.get("input_addresses", [])
            received = sum(int(o["amount_atomic"]) for o in outputs if o["address"].lower() == destination)
            if received != amount or not inputs:
                raise SwapError("Quantidade ou destino do pagamento POVIX incorretos.")
            if purpose == "payout" and not all(a.lower() in treasury for a in inputs):
                raise SwapError("O pagamento não saiu da tesouraria configurada.")
            if purpose == "deposit" and any(a.lower() in treasury for a in inputs):
                raise SwapError("Uma movimentação interna não pode ser usada como depósito.")
            return tx
        _, contract, head, safe = self.evm_context()
        receipt = self.rpc("eth_getTransactionReceipt", [tx])
        if not receipt or receipt.get("transactionHash", "").lower() != tx or receipt.get("status") != "0x1":
            raise SwapError("Transação ausente ou revertida.")
        height = int(receipt["blockNumber"], 16)
        block = self.rpc("eth_getBlockByNumber", [receipt["blockNumber"], False])
        if height > safe or not block or block["hash"].lower() != receipt["blockHash"].lower() or int(block["timestamp"], 16) < created:
            raise SwapError("Transação anterior à ordem ou sem confirmações canônicas suficientes.")
        received = 0
        for log in receipt.get("logs", []):
            topics = log.get("topics", [])
            if log.get("removed") or log.get("address", "").lower() != contract or len(topics) != 3 or topics[0].lower() != TRANSFER:
                continue
            if not all(re.fullmatch(r"0x[0-9a-fA-F]{64}", topic) for topic in topics):
                raise SwapError("Evento de transferência inválido.")
            sender, receiver = "0x" + topics[1][-40:].lower(), "0x" + topics[2][-40:].lower()
            if receiver != destination:
                continue
            if purpose == "payout" and sender not in treasury:
                raise SwapError("O pagamento não saiu da tesouraria configurada.")
            if purpose == "deposit" and sender in treasury:
                raise SwapError("Uma movimentação interna não pode ser usada como depósito.")
            received += int(log["data"], 16)
        if received != amount:
            raise SwapError("Contrato, quantidade ou destino do USDT incorretos.")
        return tx
