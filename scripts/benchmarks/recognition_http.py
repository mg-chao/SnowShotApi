"""Measure balanced table/formula demand through verified local mTLS endpoints."""
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

from recognition_cpu import corpus, corpus_omissions, memory, percentile, temperature, validate_output, write_json


class LoopbackHTTPS(http.client.HTTPSConnection):
    def connect(self):
        sock = socket.create_connection(("127.0.0.1", self.port), self.timeout)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


def connection(kind, port, pki_directory):
    pki = Path(pki_directory) / f"{kind}-communication"
    context = ssl.create_default_context(cafile=str(pki / "worker-server-ca.pem"))
    context.load_cert_chain(str(pki / "client.pem"), str(pki / "client-key.pem"))
    return LoopbackHTTPS(f"snowshot-{kind}-rec", port=port, context=context, timeout=60)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--pki-dir", type=Path)
    parser.add_argument("--seconds", type=float, default=180)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--table-port", type=int, default=18080)
    parser.add_argument("--latex-port", type=int, default=18081)
    parser.add_argument("--table-pid", type=int)
    parser.add_argument("--latex-pid", type=int)
    args = parser.parse_args()
    if args.seconds <= 0 or any(not 1 <= port <= 65535 for port in (args.table_port, args.latex_port)):
        parser.error("Duration must be positive and ports must be between 1 and 65535")
    roots = {"table": args.root / "services/TableStructureRec", "latex": args.root / "services/FormulaRecognition"}
    samples = {kind: corpus(root, kind) for kind, root in roots.items()}
    pki = args.pki_dir or args.root / "probe-pki"
    clients = {kind: connection(kind, port, pki) for kind, port in (("table", args.table_port), ("latex", args.latex_port))}
    for client in clients.values():
        try:
            client.request("GET", "/health/ready", headers={"Connection": "close"})
            response = client.getresponse()
            body = response.read()
            if response.status != 200:
                raise RuntimeError(f"Worker is not ready: {response.status} {body!r}")
        finally:
            client.close()
    records, outputs = {"table": [], "latex": []}, {"table": {}, "latex": {}}
    errors, memory_samples = [], []
    failed = threading.Event()

    def request(kind, index, offered=None, timed=True):
        name, payload, metadata = samples[kind][index % len(samples[kind])]
        started = time.perf_counter()
        try:
            clients[kind].request("POST", f"/v2/{kind}/extract", body=payload,
                                 headers={"Content-Type": "image/webp", "X-Request-ID": f"benchmark-{kind}-{index}", "Connection": "close"})
            response = clients[kind].getresponse()
            data = json.loads(response.read())
            if response.status != 200:
                raise RuntimeError(f"HTTP {response.status}: {data}")
            text = data["html" if kind == "table" else "latex"]
            validate_output(text, metadata, kind, roots[kind])
            outputs[kind].setdefault(name, set()).add(hashlib.sha256(text.encode()).hexdigest())
            finished = time.perf_counter()
            if timed:
                records[kind].append({"fixture": name, "seconds": finished - started,
                                      "queue_seconds": started - offered, "offered_seconds": finished - offered})
            return True
        except Exception as exc:
            errors.append({"service": kind, "fixture": name, "exception": repr(exc)})
            failed.set()
            return False
        finally:
            clients[kind].close()

    for kind in clients:
        for index in range(len(samples[kind])):
            print(f"warmup {kind}/{samples[kind][index][0]}", flush=True)
            if not request(kind, index, timed=False):
                write_json(args.output, {"errors": errors, "phase": "warmup"})
                raise SystemExit(1)
    jobs = queue.Queue(maxsize=8)

    def formulas():
        while True:
            job = jobs.get()
            if job is None:
                return
            index, offered = job
            if not request("latex", index, offered):
                return

    sample_stop = threading.Event()
    started = time.perf_counter()
    def sample_memory():
        while not sample_stop.wait(.5):
            row = {"seconds": time.perf_counter() - started}
            row["cpu_temperature_c"] = temperature()
            for kind in clients:
                pid = getattr(args, f"{kind}_pid")
                if pid:
                    row[kind] = memory(pid)
            memory_samples.append(row)

    formula_thread = threading.Thread(target=formulas, daemon=True)
    sampler = threading.Thread(target=sample_memory, daemon=True)
    formula_thread.start()
    sampler.start()
    offered_count = 0
    peak_backlog = 0
    try:
        while time.perf_counter() - started < args.seconds and not failed.is_set():
            offered = time.perf_counter()
            while not failed.is_set():
                try:
                    jobs.put((offered_count, offered), timeout=.2)
                    break
                except queue.Full:
                    continue
            if failed.is_set():
                break
            peak_backlog = max(peak_backlog, jobs.qsize())
            request("table", offered_count, offered)
            offered_count += 1
    finally:
        # A failed consumer must never leave the producer blocked on a full queue.
        while formula_thread.is_alive():
            try:
                jobs.put(None, timeout=.2)
                break
            except queue.Full:
                continue
        formula_thread.join(timeout=60 * (jobs.maxsize + 2))
        if formula_thread.is_alive():
            errors.append({"service": "latex", "exception": "Formula drain exceeded bounded deadline"})
        elapsed = time.perf_counter() - started
        sample_stop.set()
        sampler.join(timeout=2)
        for client in clients.values():
            client.close()
    report = {"elapsed_seconds": elapsed, "requested_seconds": args.seconds, "offered_pairs": offered_count,
              "mix": "one formula per table", "maximum_formula_backlog": 8, "peak_formula_backlog": peak_backlog,
              "transport": "verified mutual TLS over loopback; fresh TLS connection per request", "p95_method": "nearest-rank",
              "errors": errors, "services": {}, "memory_samples": memory_samples,
              "corpus": {kind: [sample[2] for sample in values] for kind, values in samples.items()},
              "corpus_omissions": {kind: corpus_omissions(root, kind) for kind, root in roots.items()},
              "output_hashes": {kind: {name: sorted(values) for name, values in rows.items()} for kind, rows in outputs.items()}}
    for kind, rows in records.items():
        durations = [row["seconds"] for row in rows]
        offered = [row["offered_seconds"] for row in rows]
        waits = [row["queue_seconds"] for row in rows]
        report["services"][kind] = {"successes": len(rows), "throughput_rps": len(rows) / elapsed,
            "mean_ms": statistics.mean(durations) * 1000 if durations else None,
            "p95_ms": percentile(durations, .95) * 1000 if durations else None,
            "offered_p95_ms": percentile(offered, .95) * 1000 if offered else None,
            "queue_p95_ms": percentile(waits, .95) * 1000 if waits else None, "samples": rows}
    report["balanced_pairs_rps"] = min(len(rows) for rows in records.values()) / elapsed
    report["combined_successful_extractions_rps"] = sum(len(rows) for rows in records.values()) / elapsed
    write_json(args.output, report)
    print(json.dumps({key: report[key] for key in ("elapsed_seconds", "balanced_pairs_rps", "errors", "services")}))
    if errors or len(records["table"]) != len(records["latex"]) or not records["table"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
