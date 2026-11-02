"""Summarize native and mTLS sweep evidence without changing raw reports."""
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    root = args.directory
    source_root = root / "raw" if (root / "raw").is_dir() else root
    rows, hashes, http, errors = [], {}, {}, []
    for path in sorted(source_root.rglob("*.json")):
        if "barrier" in path.parts or path.name in ("summary.json", "configuration.json") or "pilot" in path.name:
            continue
        report = json.loads(path.read_text(encoding="utf-8-sig"))
        source = path.relative_to(root).as_posix()
        if "worker_outputs" in report:
            row = {key: report.get(key) for key in ("service", "workers", "threads", "elapsed_seconds", "successes", "throughput_rps", "mean_ms", "p95_ms")}
            row.update(source=source, affinity=",".join(map(str, report.get("affinity", []))),
                       peak_worker_rss_mib=report["peak_worker_rss_bytes"] / 1024 ** 2,
                       peak_worker_swap_mib=report.get("peak_worker_swap_bytes", 0) / 1024 ** 2,
                       error_count=len(report["errors"]))
            rows.append(row)
            errors.extend({"source": source, "error": error} for error in report["errors"])
            for ready in report["readiness"]:
                runtime = ready.get("runtime")
                if runtime is not None:
                    assert runtime["device"] == "cpu" and runtime["cpu_threads"] == report["threads"], source
                    if report["service"] == "table":
                        assert all(providers == ["CPUExecutionProvider"] for providers in runtime["providers"].values()), source
                        assert all(value == report["threads"] for value in runtime["session_threads"]), source
                    else:
                        assert runtime["engine"] == "paddle_static" and runtime["mkldnn"] is False, source
                else:
                    assert all(providers == ["CPUExecutionProvider"] for providers in ready["providers"].values()), source
                    assert all(value == report["threads"] for value in ready["session_threads"]), source
            for worker in report["worker_outputs"]:
                for fixture, values in worker.items():
                    hashes.setdefault(report["service"], {}).setdefault(fixture, set()).update(values)
        elif "services" in report and "output_hashes" in report:
            http[source] = {key: report.get(key) for key in ("elapsed_seconds", "errors", "mix", "transport", "balanced_pairs_rps")}
            http[source]["services"] = {kind: {key: value for key, value in result.items() if key != "samples"} for kind, result in report["services"].items()}
            errors.extend({"source": source, "error": error} for error in report["errors"])
            for kind, fixtures in report["output_hashes"].items():
                for fixture, values in fixtures.items():
                    hashes.setdefault(kind, {}).setdefault(fixture, set()).update(values)
    if not rows:
        raise ValueError("No native benchmark reports found")
    if any(len(values) != 1 for fixtures in hashes.values() for values in fixtures.values()):
        raise ValueError("Outputs differ across configurations or native/HTTP paths")
    with (root / "native-summary.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    summary = {"native_runs": len(rows), "native_successes": sum(row["successes"] for row in rows),
               "errors": errors, "cpu_configuration_verified": True, "outputs_identical_across_configurations": True,
               "output_sha256": {kind: {fixture: sorted(values) for fixture, values in fixtures.items()} for kind, fixtures in hashes.items()},
               "balanced_http": http}
    # Preserve the sweep's selection summary as a distinct artifact.
    (root / "measurement-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"native_runs": len(rows), "native_successes": summary["native_successes"],
                      "errors": len(errors), "balanced_http_runs": len(http)}))
    if errors:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
