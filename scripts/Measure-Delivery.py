"""Isolated BE-014 acceptance measurement. Python 3.11+, Docker Compose; no packages.

Creates its own Compose project, credentials and database. Never uses the developer's
database. Removes only that project and its volumes in finally. Results contain no secrets.
"""
import base64
import concurrent.futures
import datetime as dt
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import secrets
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "artifacts" / "be014-delivery"
PROJECT = "exactledger-delivery-" + uuid.uuid4().hex[:10]
ENV_FILE = OUTPUT / (PROJECT + ".env")


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    api_port, pg_port = free_port(), free_port()
    settings = {
        "POSTGRES_PASSWORD": secrets.token_urlsafe(24),
        "MIGRATION_PASSWORD": secrets.token_urlsafe(24),
        "APP_PASSWORD": secrets.token_urlsafe(24),
        "SEED_PASSWORD": secrets.token_urlsafe(24),
        "JWT_SIGNING_KEY": base64.b64encode(secrets.token_bytes(48)).decode(),
        "API_PORT": str(api_port), "POSTGRES_PORT": str(pg_port),
        "CLEARING_SCHEDULE_ENABLED": "false", "ESCROW_SCHEDULE_ENABLED": "false",
    }
    ENV_FILE.write_text("".join(f"{k}={v}\n" for k, v in settings.items()), encoding="utf-8")
    environment = os.environ.copy()
    for key in settings:
        environment.pop(key, None)
    compose = ["docker", "compose", "--project-name", PROJECT, "--env-file", str(ENV_FILE), "--profile", "app"]

    def docker(*args, **kwargs):
        return subprocess.run(compose + list(args), cwd=ROOT, env=environment, check=True, **kwargs)

    base = f"http://127.0.0.1:{api_port}"

    def request(method, path, body=None, token=None, key=None, raw=False):
        headers = {}
        if token:
            headers["Authorization"] = "Bearer " + token
        if key:
            headers["Idempotency-Key"] = key
        if body is not None:
            headers["Content-Type"] = "text/csv" if isinstance(body, bytes) else "application/json"
            body = body if isinstance(body, bytes) else json.dumps(body).encode()
        started = time.perf_counter()
        with urllib.request.urlopen(urllib.request.Request(base + path, body, headers, method=method), timeout=120) as response:
            content = response.read()
        elapsed = time.perf_counter() - started
        return (content if raw else json.loads(content) if content else None), elapsed

    def percentile(values):
        return round(sorted(values)[math.ceil(len(values) * .95) - 1] * 1000, 2)

    try:
        docker("up", "--build", "-d", stdout=subprocess.DEVNULL)
        deadline = time.monotonic() + 120
        while True:
            try:
                request("GET", "/health/live", raw=True)
                break
            except (OSError, urllib.error.URLError):
                if time.monotonic() >= deadline:
                    raise TimeoutError("Isolated API did not become live")
                time.sleep(1)
        tokens = {user: request("POST", "/v1/auth/login", {"username": user, "password": settings["SEED_PASSWORD"]})[0]["accessToken"]
                  for user in ("integrator", "finance", "admin", "auditor", "payee-seller-1")}
        profiles, _ = request("GET", "/v1/fee-profiles", token=tokens["admin"])
        profile = next(p["content"] for p in profiles if p["content"]["corporateCode"] == "DEMO-SIMPLE")
        seller = "625ef6d0-ce43-3186-d39e-d28c006d70c9"
        today = dt.datetime.now(dt.timezone.utc).date().isoformat()
        occurred = today + "T00:00:00Z"
        latencies = []

        def settle(index):
            trade = {"tradeId": f"PERF-{index:05d}", "occurredAt": occurred, "currency": "USD",
                     "gross": {"amount": "250.00", "currency": "USD"}, "regionCode": "DEMO-NONE",
                     "profileId": profile["profileId"], "corporateCode": profile["corporateCode"],
                     "beneficiaries": [{"lineId": "seller", "partyId": seller, "kind": "Remainder", "weight": 0, "rateUnits": 0}]}
            return request("POST", "/v1/settlements", trade, tokens["integrator"], f"perf-{index}")[1]

        def load(start, stop):
            with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
                for count, elapsed in enumerate(pool.map(settle, range(start, stop)), start=start):
                    latencies.append(elapsed)
                    if (count + 1) % 1000 == 0:
                        print(f"Created {count + 1}/10000 trades", flush=True)

        load(0, 1000)
        batch, prepare_time = request("POST", "/v1/clearing-batches", {"businessDate": today, "currency": "USD"}, tokens["finance"])
        assert batch["entryCount"] == 1000 and batch["total"]["amount"] == "247500.00"
        batch_id = batch["id"]
        _, export_time = request("POST", f"/v1/clearing-batches/{batch_id}/export", token=tokens["finance"])
        before, _ = request("GET", f"/v1/reports/batch-summary?batchId={batch_id}", token=tokens["auditor"])
        assert before["rows"][0]["reserved"] == "247500.00" and before["rows"][0]["paid"] == "0.00"
        statement, _ = request("POST", f"/v1/clearing-batches/{batch_id}/simulate-statement", token=tokens["finance"], raw=True)
        request("POST", f"/v1/clearing-batches/{batch_id}/confirm-simulated", statement, tokens["finance"])
        after, _ = request("GET", f"/v1/reports/batch-summary?batchId={batch_id}", token=tokens["auditor"])
        assert after["rows"][0]["isReconciled"] == "true" and after["rows"][0]["bankCredits"] == "247500.00"
        load(1000, 10000)
        reads = {}
        for kind, user in [("journal-register", "auditor"), ("trial-balance", "auditor"), ("account-balances", "payee-seller-1"),
                           ("payee-statement", "payee-seller-1"), ("batch-summary", "auditor"), ("payout-instructions", "payee-seller-1")]:
            # One warm-up, then thirty sequential HTTP requests on the completed dataset.
            request("GET", f"/v1/reports/{kind}", token=tokens[user])
            samples = [request("GET", f"/v1/reports/{kind}", token=tokens[user])[1] for _ in range(30)]
            reads[kind] = {"samples": len(samples), "p95_ms": percentile(samples)}
            print(f"{kind}: p95 {reads[kind]['p95_ms']} ms", flush=True)
        artifact, _ = request("POST", "/v1/reports/journal-register/exports", {}, tokens["finance"], "final-journal")
        csv, _ = request("GET", artifact["downloadUrl"], token=tokens["auditor"], raw=True)
        assert hashlib.sha256(csv).hexdigest() == artifact["sha256"]
        assert artifact["recordCount"] == 30002
        trial, _ = request("GET", "/v1/reports/trial-balance", token=tokens["auditor"])
        assert all(row["difference"] == "0.00" for row in trial["rows"])
        # Binary backup avoids PowerShell's text redirection corrupting a custom-format dump.
        backup = OUTPUT / "synthetic.dump"
        with backup.open("wb") as output:
            docker("exec", "-T", "postgres", "pg_dump", "-U", "postgres", "-d", "exactledger", "-Fc", stdout=output)
        docker("exec", "-T", "postgres", "createdb", "-U", "postgres", "-O", "exactledger_migrations", "exactledger_restore")
        with backup.open("rb") as source:
            docker("exec", "-T", "postgres", "pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--role=exactledger_migrations", "-d", "exactledger_restore", stdin=source)
        sql = "SELECT count(*) FROM ledger.settlements; SELECT count(*) FROM ledger.journal_entries; SELECT count(*) FROM ledger.report_exports; SELECT coalesce(sum(CASE WHEN side=1 THEN amount_minor::numeric ELSE -amount_minor::numeric END),0) FROM ledger.journal_lines;"
        restored = docker("exec", "-T", "postgres", "psql", "-U", "postgres", "-d", "exactledger_restore", "-At", "-c", sql,
                          capture_output=True, text=True).stdout.strip().splitlines()
        assert restored == ["10000", "10001", "1", "0"], restored
        results = {"measuredAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(), "platform": platform.platform(),
                   "processor": platform.processor(), "logicalCpuCount": os.cpu_count(), "python": platform.python_version(),
                   "docker": subprocess.check_output(["docker", "version", "--format", "{{.Server.Version}}"], text=True).strip(),
                   "dataset": {"trades": 10000, "grossUsdPerTrade": "250.00", "concurrentClients": 5, "dailyBatchTrades": 1000},
                   "settlement": {"samples": len(latencies), "p95_ms": percentile(latencies)}, "reads": reads,
                   "batch": {"prepare_ms": round(prepare_time * 1000, 2), "export_ms": round(export_time * 1000, 2)},
                   "checks": {"prePostReconciliation": True, "trialBalance": True, "csvHash": True, "backupRestore": True},
                   "targetsMet": percentile(latencies) < 1000 and all(r["p95_ms"] < 1000 for r in reads.values()) and max(prepare_time, export_time) < 30}
        (OUTPUT / "results.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(results, indent=2), flush=True)
    finally:
        docker("down", "--volumes", "--remove-orphans", stdout=subprocess.DEVNULL)
        ENV_FILE.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
