"""Evaluate the independent printed-formula corpus; no production requests."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True, help="FormulaRecognition service root")
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--threads", type=int, default=1)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    os.environ.update(OMP_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1", MKL_NUM_THREADS="1", NUMEXPR_NUM_THREADS="1")
    sys.path.insert(0, str(args.root.resolve()))
    from latex_rec_service.engine import EngineBundle
    from latex_rec_service.image_validation import decode_webp
    from tests.corpus_support import matches_expected
    corpus = args.root / "tests/corpus"
    fixtures = json.loads((corpus / "manifest.json").read_text())
    started = time.perf_counter()
    bundle = EngineBundle.load(args.model_dir, cpu_threads=args.threads)
    load_seconds = time.perf_counter() - started
    records = []
    try:
        for fixture in fixtures:
            payload = (corpus / fixture["image"]).read_bytes()
            assert hashlib.sha256(payload).hexdigest() == fixture["sha256"]
            request_started = time.perf_counter()
            record = {"id": fixture["id"], "category": fixture["category"],
                      "expected_latex": fixture["expected_latex"], "image_sha256": fixture["sha256"]}
            try:
                latex = bundle.extract(decode_webp(payload)).latex
                record.update(latex=latex, correct=matches_expected(latex, fixture))
            except Exception as error:
                record.update(correct=False, error=f"{type(error).__name__}: {error}")
            record["latency_ms"] = (time.perf_counter() - request_started) * 1000
            records.append(record)
            print(f'{fixture["id"]}: {"pass" if record["correct"] else "FAIL"}', flush=True)
    finally:
        bundle.close()
    report = {"runtime": bundle.runtime_summary, "load_and_warmup_seconds": load_seconds,
              "count": len(records), "correct": sum(row["correct"] for row in records), "records": records}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(f'{report["correct"]}/{report["count"]} correct')
    if report["correct"] != report["count"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
