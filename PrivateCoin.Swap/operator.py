"""Local operator CLI: queue and confirm a payment already sent by the treasury."""
import argparse
import json
import os
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("action", choices=["queue", "settle"])
parser.add_argument("--order")
parser.add_argument("--tx")
parser.add_argument("--port", type=int, default=8787)
args = parser.parse_args()
token = os.environ.get("POVIX_SWAP_ADMIN_TOKEN")
if not token or len(token) < 32:
    raise SystemExit("Configure POVIX_SWAP_ADMIN_TOKEN no ambiente, nunca nos argumentos.")
if args.action == "settle" and not (args.order and args.tx):
    parser.error("settle exige --order e --tx")
request = urllib.request.Request(
    f"http://127.0.0.1:{args.port}/swap-api/admin/{args.action}",
    data=json.dumps(dict(order_id=args.order, tx=args.tx)).encode(),
    headers={"Content-Type": "application/json", "Authorization": "Bearer " + token})
try:
    with urllib.request.urlopen(request, timeout=60) as response:
        print(json.dumps(json.load(response), ensure_ascii=False, indent=2))
except urllib.error.HTTPError as error:
    print(error.read().decode())
    raise SystemExit(1)
