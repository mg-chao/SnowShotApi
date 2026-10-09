"""Benchmark verified recognition bundles with one serial inference slot per process.

Run with the service's isolated Python environment. The timed path includes WebP
validation, decoding, preprocessing, all models, and output generation. Startup
and one warmup per corpus image are excluded. HTTP/TLS is verified separately.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import multiprocessing as mp
import os
from pathlib import Path
import queue
import statistics
import time
import traceback


def corpus(root: Path, service: str):
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont

    if service == "latex":
        images = [(f"formula-{n}", Image.open(root / f"tests/test_files/{n}.png")) for n in (1, 2, 5, 6)]
    else:
        image = Image.new("RGB", (1000, 500), "white")
        draw = ImageDraw.Draw(image)
        font = ImageFont.load_default(size=28)
        for x in (30, 340, 650, 970):
            draw.line((x, 20, x, 480), fill="black", width=4)
        for y in (20, 135, 250, 365, 480):
            draw.line((30, y, 970, y), fill="black", width=4)
        for row, values in enumerate((("Product", "Quantity", "Price"), ("Notebook", "12", "5.00"), ("Marker", "6", "2.50"), ("Folder", "9", "3.25"))):
            for col, value in enumerate(values):
                draw.text((70 + col * 315, 60 + row * 115), value, fill="black", font=font)
        images = [("synthetic-wired", image)] + [(name, Image.open(root / f"tests/test_files/{path}")) for name, path in (
            ("upstream-wired", "wired/table1.png"),
            ("upstream-lineless", "lineless_table_recognition.jpg"),
            ("upstream-table", "table.jpg"),
        )]
    result = []
    for name, image in images:
        data = io.BytesIO()
        image.convert("RGB").save(data, format="WEBP", lossless=True)
        payload = data.getvalue()
        result.append((name, payload, {"name": name, "width": image.width, "height": image.height, "bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest()}))
        image.close()
    np.random.seed(0)
    return result


def worker(root, service, threads, index, ready, go, deadline, results, affinity, cycles):
    try:
        if affinity:
            os.sched_setaffinity(0, affinity)
        import cv2
        cv2.setNumThreads(1)
        if service == "table":
            from table_rec_service.engine import EngineBundle
            from table_rec_service.image_validation import decode_webp
        else:
            from latex_rec_service.engine import EngineBundle
            from latex_rec_service.image_validation import decode_webp
        root = Path(root)
        bundle = EngineBundle.load(root / "models", "CPUExecutionProvider", threads)
        samples = corpus(root, service)
        outputs = {}
        table_types = {}

        def extract(name, payload):
            result = bundle.extract(decode_webp(payload))
            text = result.html if service == "table" else result.latex
            if service == "table":
                table_types.setdefault(name, set()).add(result.table_type)
            if service == "table" and "<table>" not in text:
                raise ValueError(f"No table in {name}")
            outputs.setdefault(name, set()).add(hashlib.sha256(text.encode()).hexdigest())
            return text

        for name, payload, _ in samples:
            extract(name, payload)
        # Inspect every active ONNX session, including nested wrappers.
        if service == "latex":
            sessions = [bundle.model.image_resizer.session, bundle.model.encoder_decoder.encoder.session, bundle.model.encoder_decoder.decoder.session.session]
        else:
            components = [bundle.classifier.table_engine.table_cls, bundle.ocr.text_det, bundle.ocr.text_cls, bundle.ocr.text_rec, bundle.wired.table_structure.session, bundle.lineless.table_structure.det_session, bundle.lineless.table_structure.process_session]
            sessions = []
            for obj in components:
                while not hasattr(obj, "get_session_options"):
                    obj = getattr(obj, "session", None) or getattr(obj, "engine")
                sessions.append(obj)
        ready.put({"pid": os.getpid(), "providers": bundle.provider_summary, "session_threads": [s.get_session_options().intra_op_num_threads for s in sessions], "corpus": [s[2] for s in samples], "table_types": {k: sorted(v) for k, v in table_types.items()}})
        go.wait()
        started = time.perf_counter()
        cpu = time.process_time()
        records = []
        errors = []
        i = index
        while (len(records) + len(errors) < cycles * len(samples)) if cycles else (time.perf_counter() < deadline.value):
            name, payload, _ = samples[i % len(samples)]
            t = time.perf_counter()
            try:
                extract(name, payload)
                records.append([name, time.perf_counter() - t])
            except Exception as exc:
                errors.append({"fixture": name, "error": repr(exc)})
            i += 1
        results.put({"records": records, "errors": errors, "wall_seconds": time.perf_counter() - started, "cpu_seconds": time.process_time() - cpu, "outputs": {k: sorted(v) for k, v in outputs.items()}})
    except BaseException:
        ready.put({"error": traceback.format_exc()})


def percentile(values, p):
    values = sorted(values)
    return values[min(len(values) - 1, int((len(values) - 1) * p))] if values else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--service", choices=("table", "latex"), required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--threads", type=int, default=1)
    parser.add_argument("--seconds", type=float, default=25)
    parser.add_argument("--cycles", type=int, default=3, help="Balanced corpus passes per worker; 0 uses --seconds instead")
    parser.add_argument("--start-at", type=float, default=0, help="Unix time for synchronized mixed-service runs")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--affinity", default="")
    args = parser.parse_args()
    if not 1 <= args.workers <= 16 or not 0 <= args.threads <= 256 or args.seconds <= 0 or args.cycles < 0:
        parser.error("Invalid worker, thread, cycle, or duration count")
    os.environ.update(OMP_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1", MKL_NUM_THREADS="1", NUMEXPR_NUM_THREADS="1")
    import sys
    sys.path.insert(0, str(args.root.resolve()))
    ctx = mp.get_context("spawn")
    ready, results = ctx.Queue(), ctx.Queue()
    go, deadline = ctx.Event(), ctx.Value("d", 0)
    affinity = [int(x) for x in args.affinity.split(",") if x]
    processes = [ctx.Process(target=worker, args=(str(args.root), args.service, args.threads, i, ready, go, deadline, results, affinity, args.cycles)) for i in range(args.workers)]
    try:
        for p in processes:
            p.start()
        reports = [ready.get(timeout=240) for _ in processes]
        if any("error" in r for r in reports):
            raise RuntimeError(reports)
        while time.time() < args.start_at:
            time.sleep(min(0.2, args.start_at - time.time()))
        started = time.perf_counter()
        deadline.value = started + args.seconds
        go.set()
        peak_rss = 0
        peak_temperature = 0
        temperature_file = next((p / "temp1_input" for p in Path("/sys/class/hwmon").glob("hwmon*") if (p / "name").read_text().strip() == "k10temp"), None)
        collected = []
        while any(p.is_alive() for p in processes):
            rss = 0
            for p in processes:
                try:
                    status = Path(f"/proc/{p.pid}/status").read_text()
                    rss += int(next(line.split()[1] for line in status.splitlines() if line.startswith("VmRSS:"))) * 1024
                except (FileNotFoundError, StopIteration):
                    pass
            peak_rss = max(peak_rss, rss)
            if temperature_file:
                peak_temperature = max(peak_temperature, int(temperature_file.read_text()) / 1000)
            # Drain results before joining: large queue payloads otherwise block exit.
            if len(collected) < args.workers:
                try:
                    collected.append(results.get(timeout=0.05))
                except queue.Empty:
                    pass
            time.sleep(0.05)
        while len(collected) < args.workers:
            collected.append(results.get(timeout=10))
        elapsed = max(r["wall_seconds"] for r in collected)
        records = [record for r in collected for record in r["records"]]
        latencies = [r[1] for r in records]
        per_fixture = {}
        for name in {r[0] for r in records}:
            values = [r[1] for r in records if r[0] == name]
            per_fixture[name] = {"count": len(values), "mean_ms": statistics.mean(values) * 1000, "p95_ms": percentile(values, 0.95) * 1000}
        report = {"service": args.service, "workers": args.workers, "threads": args.threads, "affinity": affinity, "requested_seconds": args.seconds, "elapsed_seconds": elapsed, "successes": len(records), "throughput_rps": len(records) / elapsed, "mean_ms": statistics.mean(latencies) * 1000 if latencies else None, "p95_ms": percentile(latencies, 0.95) * 1000 if latencies else None, "peak_worker_rss_bytes": peak_rss, "cpu_seconds": sum(r["cpu_seconds"] for r in collected), "errors": [e for r in collected for e in r["errors"]], "readiness": reports, "per_fixture": per_fixture, "worker_outputs": [r["outputs"] for r in collected]}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        report["cycles_per_worker"] = args.cycles
        report["peak_cpu_temperature_c"] = peak_temperature or None
        args.output.write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps({k: report[k] for k in ("service", "workers", "threads", "throughput_rps", "mean_ms", "p95_ms", "peak_worker_rss_bytes", "errors")}))
    finally:
        for p in processes:
            if p.is_alive():
                p.terminate()
            p.join(timeout=5)


if __name__ == "__main__":
    main()
