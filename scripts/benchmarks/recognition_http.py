"""Measure equal table/formula demand through the deployed mTLS endpoints."""
from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import queue
import socket
import ssl
import statistics
import threading
import time
from pathlib import Path

from recognition_cpu import corpus, percentile


class LoopbackHTTPS(http.client.HTTPSConnection):
    def connect(self):
        sock = socket.create_connection(("127.0.0.1", self.port), self.timeout)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--seconds", type=float, default=90)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--table-port", type=int, default=18080)
    parser.add_argument("--latex-port", type=int, default=18081)
    args = parser.parse_args()
    if args.seconds <= 0 or any(not 1 <= port <= 65_535 for port in (args.table_port, args.latex_port)):
        parser.error("Duration must be positive and ports must be between 1 and 65535")
    samples = {kind: corpus(args.root / "services" / service, kind) for kind, service in (("table", "TableStructureRec"), ("latex", "RapidLaTeXOCR"))}
    clients = {}
    for kind, port in (("table", args.table_port), ("latex", args.latex_port)):
        pki = args.root / "probe-pki" / f"{kind}-communication"
        context = ssl.create_default_context(cafile=str(pki / "worker-server-ca.pem"))
        context.load_cert_chain(str(pki / "client.pem"), str(pki / "client-key.pem"))
        clients[kind] = LoopbackHTTPS(f"snowshot-{kind}-rec", port=port, context=context, timeout=60)
        clients[kind].request("GET", "/health/ready", headers={"Connection": "close"})
        response = clients[kind].getresponse()
        assert response.status == 200, response.read()
        response.read()
        clients[kind].close()
    records = {"table": [], "latex": []}
    errors = []
    outputs = {"table": {}, "latex": {}}

    def request(kind, index, timed=True):
        name, payload, _ = samples[kind][index % len(samples[kind])]
        started = time.perf_counter()
        clients[kind].request("POST", f"/v2/{kind}/extract", body=payload,
                              headers={"Content-Type": "image/webp", "X-Request-ID": f"benchmark-{kind}-{index}", "Connection": "close"})
        response = clients[kind].getresponse()
        data = json.loads(response.read())
        clients[kind].close()
        if response.status != 200:
            errors.append({"service": kind, "fixture": name, "status": response.status, "response": data})
            return
        text = data["html" if kind == "table" else "latex"]
        assert text.strip()
        if kind == "table":
            assert "<table>" in text
        elif name == "formula-2":
            assert text == r"x^{2}+y^{2}=1"
        elif name == "formula-6":
            assert text == r"{\frac{x^{2}}{a^{2}}}-{\frac{y^{2}}{b^{2}}}=1"
        outputs[kind].setdefault(name, set()).add(hashlib.sha256(text.encode()).hexdigest())
        if timed:
            records[kind].append({"fixture": name, "seconds": time.perf_counter() - started})

    # Warm every fixture through HTTP before measuring steady state.
    for kind in clients:
        for index in range(len(samples[kind])):
            print(f"warmup {kind}/{samples[kind][index][0]}", flush=True)
            request(kind, index, timed=False)
    if errors:
        raise RuntimeError(errors)
    formula_jobs = queue.Queue()

    def formulas():
        try:
            while (index := formula_jobs.get()) is not None:
                request("latex", index)
        except BaseException as exc:
            errors.append({"service": "latex", "exception": repr(exc)})

    thread = threading.Thread(target=formulas)
    started = time.perf_counter()
    thread.start()
    index = 0
    try:
        while time.perf_counter() - started < args.seconds:
            # Exactly one formula per offered table request; table keeps its slot
            # occupied while formulas may finish sooner or accumulate in the driver.
            formula_jobs.put(index)
            request("table", index)
            index += 1
    except BaseException as exc:
        errors.append({"service": "table", "exception": repr(exc)})
    finally:
        formula_jobs.put(None)
        thread.join(timeout=120)
        if thread.is_alive():
            raise RuntimeError("Formula benchmark did not finish")
        elapsed = time.perf_counter() - started
        for client in clients.values():
            client.close()
    report = {"elapsed_seconds": elapsed, "requested_seconds": args.seconds, "mix": "one formula per table", "transport": "verified mutual TLS over loopback; fresh TLS connection per request", "errors": errors, "services": {}, "output_hashes": {k: {n: sorted(v) for n, v in d.items()} for k, d in outputs.items()}}
    for kind, rows in records.items():
        durations = [r["seconds"] for r in rows]
        report["services"][kind] = {"successes": len(rows), "throughput_rps": len(rows) / elapsed,
            "mean_ms": statistics.mean(durations) * 1000 if durations else None,
            "p95_ms": percentile(durations, 0.95) * 1000 if durations else None, "samples": rows}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({k: report[k] for k in ("elapsed_seconds", "errors", "services")}))
    if errors or len(records["table"]) != len(records["latex"]):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
