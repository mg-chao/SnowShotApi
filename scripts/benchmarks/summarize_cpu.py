"""Produce compact, reproducible summaries from the saved Ubuntu benchmark JSON."""
from __future__ import annotations

import argparse
import csv
import json
from collections import defaultdict
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    root = args.directory
    rows = []
    hashes = defaultdict(lambda: defaultdict(set))
    successes = 0
    errors = []
    for path in sorted((root / "raw").rglob("*.json")):
        report = json.loads(path.read_text(encoding="utf-8"))
        if "worker_outputs" not in report or "pilot" in path.name:
            continue
        source = path.relative_to(root).as_posix()
        profile = "throughput" if "throughput" in source else "legacy"
        rows.append({"source": source, "service": report["service"], "profile": profile,
                     "workers": report["workers"], "threads": report["threads"],
                     "affinity": ",".join(map(str, report.get("affinity", []))),
                     "cycles_per_worker": report.get("cycles_per_worker", 0),
                     "elapsed_seconds": report["elapsed_seconds"], "successes": report["successes"],
                     "throughput_rps": report["throughput_rps"], "mean_ms": report["mean_ms"],
                     "p95_ms": report["p95_ms"],
                     "peak_worker_rss_mib": report["peak_worker_rss_bytes"] / 1024 ** 2,
                     "error_count": len(report["errors"])})
        successes += report["successes"]
        errors.extend({"source": source, "error": error} for error in report["errors"])
        for ready in report["readiness"]:
            assert all(providers == ["CPUExecutionProvider"] for providers in ready["providers"].values()), source
            assert all(threads == report["threads"] for threads in ready["session_threads"]), source
        for worker in report["worker_outputs"]:
            for fixture, values in worker.items():
                hashes[report["service"]][fixture].update(values)
    assert rows, "No native benchmark reports found"
    assert all(len(values) == 1 for fixtures in hashes.values() for values in fixtures.values()), "Outputs differ across configurations"
    with (root / "native-summary.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    http = {}
    for path in sorted((root / "raw").glob("*.json")):
        if path.name.startswith(("deployed-http-balanced", "http-balanced-")):
            report = json.loads(path.read_text(encoding="utf-8"))
            http[path.name] = {"elapsed_seconds": report["elapsed_seconds"], "errors": report["errors"],
                               "mix": report["mix"], "transport": report["transport"],
                               "services": {kind: {key: value for key, value in result.items() if key != "samples"}
                                            for kind, result in report["services"].items()}}
            for kind, fixtures in report["output_hashes"].items():
                for fixture, values in fixtures.items():
                    assert set(values) == hashes[kind][fixture], (path.name, fixture)
    summary = {"native_runs": len(rows), "native_successes": successes, "native_errors": errors,
               "providers": "All inspected ONNX sessions used CPUExecutionProvider exclusively",
               "outputs_identical_across_configurations": True,
               "output_sha256": {kind: {fixture: sorted(values) for fixture, values in fixtures.items()}
                                 for kind, fixtures in hashes.items()}, "balanced_http": http}
    (root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"native_runs": len(rows), "native_successes": successes,
                      "native_errors": len(errors), "balanced_http_runs": len(http)}))


if __name__ == "__main__":
    main()
