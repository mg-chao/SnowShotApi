"""Measure verified bundles with one serial native slot per process."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import multiprocessing as mp
import os
from pathlib import Path
import queue
import statistics
import sys
import time
import traceback


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def percentile(values, p):
    values = sorted(values)
    return values[max(0, math.ceil(len(values) * p) - 1)] if values else None


def memory(pid):
    try:
        entries = dict(line.split(":", 1) for line in Path(f"/proc/{pid}/status").read_text().splitlines() if ":" in line)
        return {key: int(entries.get(key, "0 kB").split()[0]) * 1024 for key in ("VmRSS", "VmHWM", "VmSwap")}
    except FileNotFoundError:
        return {key: 0 for key in ("VmRSS", "VmHWM", "VmSwap")}


def temperature():
    for directory in Path("/sys/class/hwmon").glob("hwmon*"):
        try:
            if (directory / "name").read_text().strip() in ("k10temp", "coretemp"):
                return int((directory / "temp1_input").read_text()) / 1000
        except (OSError, ValueError):
            continue
    return None


def interleave_formula_entries(entries):
    """Visit each formula family before its next manifest-ordered variant."""
    groups = {}
    for entry in entries:
        base = entry["id"]
        if entry.get("category") != "historical":
            for suffix in ("-light", "-dark", "-scaled", "-transparent"):
                if base.endswith(suffix):
                    base = base[:-len(suffix)]
                    break
        groups.setdefault(base, []).append(entry)
    return [group[index] for index in range(max(map(len, groups.values()), default=0))
            for group in groups.values() if index < len(group)]


def corpus(root: Path, service: str):
    from PIL import Image, ImageDraw, ImageFont
    expectations = {}
    formula_paths = {}
    if service == "latex":
        manifest = root / "tests/corpus/manifest.json"
        entries = json.loads(manifest.read_text(encoding="utf-8"))
        if isinstance(entries, dict):
            entries = entries["fixtures"]
        entries = interleave_formula_entries([entry for entry in entries if entry.get("benchmark", True) is not False])
        images = [(entry["id"], Image.open(manifest.parent / entry["image"])) for entry in entries]
        formula_paths = {entry["id"]: manifest.parent / entry["image"] for entry in entries}
        expectations = {entry["id"]: entry for entry in entries}
    else:
        image = Image.new("RGB", (1000, 500), "white")
        draw, font = ImageDraw.Draw(image), ImageFont.load_default(size=28)
        for x in (30, 340, 650, 970):
            draw.line((x, 20, x, 480), fill="black", width=4)
        for y in (20, 135, 250, 365, 480):
            draw.line((30, y, 970, y), fill="black", width=4)
        rows = (("Product", "Quantity", "Price"), ("Notebook", "12", "5.00"), ("Marker", "6", "2.50"), ("Folder", "9", "3.25"))
        for row, values in enumerate(rows):
            for col, value in enumerate(values):
                draw.text((70 + col * 315, 60 + row * 115), value, fill="black", font=font)
        images = [("synthetic-wired", image)] + [(name, Image.open(root / "tests/test_files" / path)) for name, path in (
            ("upstream-wired", "wired/table1.png"), ("upstream-lineless", "lineless_table_recognition.jpg"), ("upstream-table", "table.jpg"))]
    result = []
    for name, image in images:
        data = io.BytesIO()
        if name in formula_paths and image.format == "WEBP":
            payload = formula_paths[name].read_bytes()
        else:
            image.save(data, format="WEBP", lossless=True)
            payload = data.getvalue()
        metadata = {"name": name, "width": image.width, "height": image.height, "bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest()}
        if name in expectations:
            if expectations[name].get("sha256") and metadata["sha256"] != expectations[name]["sha256"]:
                raise ValueError(f"Corpus fixture hash mismatch: {name}")
            metadata["expectation"] = expectations[name]
        result.append((name, payload, metadata))
        image.close()
    return result


def corpus_omissions(root: Path, service: str):
    if service != "latex":
        return []
    entries = json.loads((root / "tests/corpus/manifest.json").read_text(encoding="utf-8"))
    if isinstance(entries, dict):
        entries = entries["fixtures"]
    return [{"id": entry["id"], "reason": entry.get("known_limitation", "Explicitly excluded from performance corpus")}
            for entry in entries if entry.get("benchmark", True) is False]


def validate_output(text, metadata, service, root):
    if not isinstance(text, str) or not text.strip():
        raise ValueError(f"Empty {service} output for {metadata['name']}")
    if service == "table":
        if "<table>" not in text:
            raise ValueError(f"No table in {metadata['name']}")
    else:
        tests_path = str(Path(root) / "tests")
        if tests_path not in sys.path:
            sys.path.insert(0, tests_path)
        from corpus_support import matches_expected
        if not matches_expected(text, metadata["expectation"]):
            raise ValueError(f"Formula does not match reviewed corpus: {metadata['name']}")


def table_runtime(bundle, threads):
    components = [bundle.classifier.table_engine.table_cls, bundle.ocr.text_det, bundle.ocr.text_cls,
                  bundle.ocr.text_rec, bundle.wired.table_structure.session,
                  bundle.lineless.table_structure.det_session, bundle.lineless.table_structure.process_session]
    sessions = []
    for obj in components:
        while not hasattr(obj, "get_session_options"):
            obj = getattr(obj, "session", None) or getattr(obj, "engine")
        sessions.append(obj)
    session_threads = [session.get_session_options().intra_op_num_threads for session in sessions]
    if any(value != threads for value in session_threads) or any(value != ["CPUExecutionProvider"] for value in bundle.provider_summary.values()):
        raise ValueError("Table backend or effective thread configuration differs from requested CPU settings")
    return {"engine": "onnxruntime", "device": "cpu", "cpu_threads": threads,
            "providers": bundle.provider_summary, "session_threads": session_threads}


def worker(root, model_dir, service, threads, index, ready, go, deadline, results, affinity, cycles):
    try:
        if affinity:
            os.sched_setaffinity(0, affinity)
        sys.path.insert(0, str(root))
        import cv2
        cv2.setNumThreads(1)
        loading = time.perf_counter()
        if service == "table":
            from table_rec_service.engine import EngineBundle
            from table_rec_service.image_validation import decode_webp
            bundle = EngineBundle.load(Path(model_dir), "CPUExecutionProvider", threads)
            runtime = table_runtime(bundle, threads)
        else:
            from latex_rec_service.engine import EngineBundle
            from latex_rec_service.image_validation import decode_webp
            bundle = EngineBundle.load(Path(model_dir), cpu_threads=threads)
            runtime = bundle.runtime_summary
        load_seconds = time.perf_counter() - loading
        samples = corpus(Path(root), service)
        outputs = {}
        def extract(sample):
            name, payload, metadata = sample
            result = bundle.extract(decode_webp(payload))
            text = result.html if service == "table" else result.latex
            validate_output(text, metadata, service, root)
            outputs.setdefault(name, set()).add(hashlib.sha256(text.encode()).hexdigest())
        first = time.perf_counter()
        extract(samples[0])
        first_seconds = time.perf_counter() - first
        for sample in samples:
            extract(sample)
        ready.put({"pid": os.getpid(), "runtime": runtime, "model_load_seconds": load_seconds,
                   "first_request_ms": first_seconds * 1000, "warm_memory": memory(os.getpid()), "corpus": [s[2] for s in samples],
                   "corpus_omissions": corpus_omissions(Path(root), service)})
        go.wait()
        started, cpu = time.perf_counter(), time.process_time()
        records, errors = [], []
        i = index
        while (len(records) + len(errors) < cycles * len(samples)) if cycles else (time.perf_counter() < deadline.value):
            sample = samples[i % len(samples)]
            t = time.perf_counter()
            try:
                extract(sample)
                records.append([sample[0], time.perf_counter() - t])
            except Exception as exc:
                errors.append({"fixture": sample[0], "error": repr(exc)})
            i += 1
        results.put({"records": records, "errors": errors, "wall_seconds": time.perf_counter() - started,
                     "cpu_seconds": time.process_time() - cpu, "final_memory": memory(os.getpid()),
                     "outputs": {k: sorted(v) for k, v in outputs.items()}})
    except BaseException:
        failure = {"error": traceback.format_exc()}
        ready.put(failure)
        results.put(failure)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service", choices=("table", "latex"), required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--threads", type=int, default=1)
    parser.add_argument("--seconds", type=float, default=90)
    parser.add_argument("--cycles", type=int, default=3, help="0 measures for --seconds")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--affinity", default="")
    parser.add_argument("--barrier-dir", type=Path)
    args = parser.parse_args()
    if not 1 <= args.workers <= 16 or args.threads < 1 or args.seconds <= 0 or args.cycles < 0:
        parser.error("Invalid worker, thread, cycle or duration count")
    if args.service == "latex" and args.workers != 1:
        parser.error("Formula release benchmarks use one process, matching API concurrency")
    os.environ.update(OMP_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1", MKL_NUM_THREADS="1", NUMEXPR_NUM_THREADS="1")
    ctx = mp.get_context("spawn")
    ready, results = ctx.Queue(), ctx.Queue()
    go, deadline = ctx.Event(), ctx.Value("d", 0)
    affinity = [int(x) for x in args.affinity.split(",") if x]
    model_dir = args.model_dir or args.root / "models"
    processes = [ctx.Process(target=worker, args=(str(args.root.resolve()), str(model_dir.resolve()), args.service, args.threads, i, ready, go, deadline, results, affinity, args.cycles)) for i in range(args.workers)]
    try:
        for process in processes:
            process.start()
        reports = [ready.get(timeout=300) for _ in processes]
        if any("error" in report for report in reports):
            raise RuntimeError(reports)
        if args.barrier_dir:
            write_json(args.barrier_dir / f"{args.service}.ready.json", reports)
            barrier_deadline = time.monotonic() + 300
            while not (args.barrier_dir / "start.json").exists():
                if time.monotonic() > barrier_deadline:
                    raise TimeoutError("Mixed benchmark readiness barrier timed out")
                time.sleep(.05)
            start = json.loads((args.barrier_dir / "start.json").read_text())["start_monotonic"]
            while True:
                remaining = start - time.monotonic()
                if remaining <= 0:
                    break
                time.sleep(min(.02, remaining))
        deadline.value = time.perf_counter() + args.seconds
        go.set()
        peak_rss = peak_swap = 0
        peak_temperature = None
        memory_samples, collected = [], []
        completion_deadline = time.monotonic() + args.seconds + 120 if not args.cycles else time.monotonic() + 600
        while len(collected) < args.workers:
            values = [memory(process.pid) for process in processes]
            rss, swap = sum(v["VmRSS"] for v in values), sum(v["VmSwap"] for v in values)
            peak_rss, peak_swap = max(peak_rss, rss), max(peak_swap, swap)
            current_temperature = temperature()
            if current_temperature is not None:
                peak_temperature = max(peak_temperature or current_temperature, current_temperature)
            memory_samples.append([time.monotonic(), rss, swap])
            try:
                collected.append(results.get(timeout=.05))
            except queue.Empty:
                if time.monotonic() > completion_deadline or not any(p.is_alive() for p in processes):
                    raise RuntimeError("Native benchmark failed or exceeded completion deadline")
        if any("error" in value for value in collected):
            raise RuntimeError(collected)
        records = [record for value in collected for record in value["records"]]
        latencies = [record[1] for record in records]
        elapsed = max(value["wall_seconds"] for value in collected)
        report = {"service": args.service, "workers": args.workers, "threads": args.threads, "affinity": affinity,
                  "requested_seconds": args.seconds, "cycles_per_worker": args.cycles, "elapsed_seconds": elapsed,
                  "successes": len(records), "throughput_rps": len(records) / elapsed,
                  "mean_ms": statistics.mean(latencies) * 1000 if latencies else None,
                  "p95_ms": percentile(latencies, .95) * 1000 if latencies else None, "p95_method": "nearest-rank",
                  "peak_worker_rss_bytes": peak_rss, "peak_worker_swap_bytes": peak_swap, "memory_samples": memory_samples,
                  "peak_total_worker_rss_bytes": sum(value["final_memory"]["VmHWM"] for value in collected),
                  "peak_cpu_temperature_c": peak_temperature,
                  "cpu_seconds": sum(value["cpu_seconds"] for value in collected), "readiness": reports,
                  "errors": [error for value in collected for error in value["errors"]],
                  "worker_outputs": [value["outputs"] for value in collected], "per_fixture": {}}
        for name in sorted({record[0] for record in records}):
            values = [record[1] for record in records if record[0] == name]
            report["per_fixture"][name] = {"count": len(values), "mean_ms": statistics.mean(values) * 1000, "p95_ms": percentile(values, .95) * 1000}
        write_json(args.output, report)
        print(json.dumps({key: report[key] for key in ("service", "throughput_rps", "mean_ms", "p95_ms", "errors")}))
        if report["errors"] or peak_swap:
            raise SystemExit(1)
    finally:
        for process in processes:
            if process.is_alive():
                process.terminate()
            process.join(timeout=5)


if __name__ == "__main__":
    main()
