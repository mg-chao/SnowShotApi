"""Observe only temporary HTTP workers parented by one Linux sweep controller.

This supplemental observer does not import or modify the benchmark or services.
Its CPU summaries infer measurement boundaries from HTTP report modification
times; they are approximate, unlike the native benchmark's internal CPU timer.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import signal
import sys
import time


MODULES = {"table_rec_service": "table", "latex_rec_service": "latex"}


def process_stat(pid):
    # comm may contain spaces or parentheses; numeric fields follow its last ')'.
    fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    return {"parent_pid": int(fields[1]), "user_ticks": int(fields[11]),
            "system_ticks": int(fields[12]), "start_ticks": int(fields[19])}


def process_memory(pid):
    status = Path(f"/proc/{pid}/status").read_text().splitlines()
    entries = dict(line.split(":", 1) for line in status if ":" in line)
    return {name: int(entries[name].split()[0]) * 1024 for name in ("VmRSS", "VmHWM", "VmSwap")}


def worker_samples(controller_pid, ticks_per_second):
    children = set()
    for task in Path(f"/proc/{controller_pid}/task").glob("*/children"):
        try:
            children.update(map(int, task.read_text().split()))
        except (FileNotFoundError, ProcessLookupError):
            continue
    rows = []
    for pid in sorted(children):
        try:
            before = process_stat(pid)
            if before["parent_pid"] != controller_pid:
                continue
            arguments = Path(f"/proc/{pid}/cmdline").read_bytes().decode("utf-8", errors="replace").split("\0")
            module = next((following for current, following in zip(arguments, arguments[1:])
                           if current == "-m" and following in MODULES), None)
            if module is None:
                continue
            memory = process_memory(pid)
            after = process_stat(pid)
            if after["start_ticks"] != before["start_ticks"] or after["parent_pid"] != controller_pid:
                continue
            user = after["user_ticks"] / ticks_per_second
            system = after["system_ticks"] / ticks_per_second
            rows.append({"service": MODULES[module], "module": module, "pid": pid,
                         "process_start_ticks": after["start_ticks"], "user_cpu_seconds": user,
                         "system_cpu_seconds": system, "cpu_seconds": user + system, **memory})
        except (FileNotFoundError, ProcessLookupError, KeyError):
            # A temporary worker may exit between reads; never follow reused PIDs.
            continue
    return rows


def save_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def observe(args):
    if sys.platform != "linux":
        raise RuntimeError("The worker observer requires Linux /proc")
    if not 0 < args.max_seconds <= 7200:
        raise ValueError("The observer duration must be positive and at most two hours")
    controller_start = process_stat(args.controller_pid)["start_ticks"]
    args.output_dir.mkdir(parents=True, exist_ok=True)
    output = args.output_dir / "worker-cpu-observer.jsonl"
    metadata_path = args.output_dir / "worker-cpu-observer.metadata.json"
    ticks_per_second = os.sysconf("SC_CLK_TCK")
    interval = .5
    started = time.monotonic()
    metadata = {"script": str(Path(__file__).absolute()),
                "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                "observer_pid": os.getpid(), "controller_pid": args.controller_pid,
                "controller_start_ticks": controller_start, "started_wall_timestamp": time.time(),
                "started_monotonic_timestamp": started, "sample_interval_seconds": interval,
                "maximum_duration_seconds": args.max_seconds, "clock_ticks_per_second": ticks_per_second,
                "method": "Read direct controller children with exact -m table_rec_service or -m latex_rec_service arguments; verify PPID and process start ticks; cumulative CPU=(utime+stime)/SC_CLK_TCK; full VmRSS/VmHWM/VmSwap from /proc/PID/status. No process environment or private key contents are read.",
                "cpu_summary_caveat": "HTTP boundaries are inferred from output mtime minus elapsed_seconds. Nearest observations add roughly <=0.5 seconds per boundary under normal scheduling, plus report construction/serialization/write delay and clock-tick quantization; actual offsets and observer gaps are reported. This is supplemental, not an exact internal CPU timer."}
    count = total_workers = 0
    previous_tick = None
    maximum_gap = 0.0
    reason = "maximum duration reached"
    signal.signal(signal.SIGTERM, lambda *_args: (_ for _ in ()).throw(KeyboardInterrupt()))
    with output.open("x", encoding="utf-8") as stream:
        save_json(metadata_path, metadata)
        print(json.dumps({"observer_pid": os.getpid(), "output": str(output),
                          "script_sha256": metadata["script_sha256"], "status": "ready"}), flush=True)
        try:
            next_tick = started
            while time.monotonic() - started < args.max_seconds:
                try:
                    if process_stat(args.controller_pid)["start_ticks"] != controller_start:
                        reason = "controller PID reused"
                        break
                except (FileNotFoundError, ProcessLookupError):
                    reason = "controller disappeared"
                    break
                wall, monotonic = time.time(), time.monotonic()
                workers = worker_samples(args.controller_pid, ticks_per_second)
                row = {"wall_timestamp": wall, "wall_utc": datetime.fromtimestamp(wall, timezone.utc).isoformat(),
                       "monotonic_timestamp": monotonic, "read_completed_monotonic_timestamp": time.monotonic(),
                       "workers": workers}
                stream.write(json.dumps(row, separators=(",", ":")) + "\n")
                stream.flush()
                count += 1
                total_workers += len(workers)
                if previous_tick is not None:
                    maximum_gap = max(maximum_gap, monotonic - previous_tick)
                previous_tick = monotonic
                next_tick += interval
                if next_tick < time.monotonic():
                    next_tick = time.monotonic() + interval
                time.sleep(max(0, next_tick - time.monotonic()))
        except KeyboardInterrupt:
            reason = "observer interrupted"
        finally:
            metadata.update(stopped_wall_timestamp=time.time(), stopped_monotonic_timestamp=time.monotonic(),
                            stop_reason=reason, snapshots=count, worker_observations=total_workers,
                            maximum_observer_gap_seconds=maximum_gap)
            save_json(metadata_path, metadata)
    print(json.dumps({"status": "stopped", "reason": reason, "snapshots": count,
                      "worker_observations": total_workers}), flush=True)


def summarize(args):
    source = args.output_dir / "worker-cpu-observer.jsonl"
    metadata = json.loads((args.output_dir / "worker-cpu-observer.metadata.json").read_text())
    processes = defaultdict(list)
    for line in source.read_text().splitlines():
        snapshot = json.loads(line)
        for worker in snapshot["workers"]:
            processes[(worker["service"], worker["pid"], worker["process_start_ticks"])].append(
                {"wall_timestamp": snapshot["wall_timestamp"], "monotonic_timestamp": snapshot["monotonic_timestamp"],
                 "read_span_seconds": snapshot["read_completed_monotonic_timestamp"] - snapshot["monotonic_timestamp"], **worker})
    result = {"method": metadata["cpu_summary_caveat"], "observer_script_sha256": metadata["script_sha256"],
              "clock_ticks_per_second": metadata["clock_ticks_per_second"], "runs": []}
    for path in sorted(args.output_dir.glob("**/http.json")):
        report = json.loads(path.read_text())
        if "elapsed_seconds" not in report:
            continue
        end = path.stat().st_mtime
        elapsed = report["elapsed_seconds"]
        start = end - elapsed
        run = {"report": str(path.relative_to(args.output_dir)), "elapsed_seconds": elapsed,
               "report_mtime": end, "inferred_start_wall_timestamp": start, "services": {}}
        for service in MODULES.values():
            candidates = [(key, rows) for key, rows in processes.items() if key[0] == service
                          and rows[0]["wall_timestamp"] <= start and rows[-1]["wall_timestamp"] >= end - 1.0]
            if len(candidates) != 1:
                run["services"][service] = {"unavailable": "No unique worker observation series covers the inferred HTTP interval"}
                continue
            key, rows = candidates[0]
            first = min(rows, key=lambda row: abs(row["wall_timestamp"] - start))
            last = min(rows, key=lambda row: abs(row["wall_timestamp"] - end))
            cpu = last["cpu_seconds"] - first["cpu_seconds"]
            sampled_seconds = last["monotonic_timestamp"] - first["monotonic_timestamp"]
            gaps = [right["monotonic_timestamp"] - left["monotonic_timestamp"] for left, right in zip(rows, rows[1:])
                    if first["monotonic_timestamp"] <= left["monotonic_timestamp"] <= last["monotonic_timestamp"]]
            run["services"][service] = {"pid": key[1], "process_start_ticks": key[2],
                "approximate_cpu_seconds": cpu, "approximate_mean_cpu_cores": cpu / elapsed,
                "approximate_mean_cpu_percent_one_core": cpu / elapsed * 100,
                "nearest_sample_start_offset_seconds": first["wall_timestamp"] - start,
                "nearest_sample_end_offset_seconds": last["wall_timestamp"] - end,
                "sampled_monotonic_seconds": sampled_seconds, "maximum_interval_gap_seconds": max(gaps, default=0),
                "maximum_proc_read_span_seconds": max(row["read_span_seconds"] for row in rows),
                "cpu_tick_resolution_seconds": 1 / metadata["clock_ticks_per_second"]}
        result["runs"].append(run)
    save_json(args.output_dir / "supplemental-http-cpu.json", result)
    print(json.dumps(result, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    observe_parser = commands.add_parser("observe")
    observe_parser.add_argument("--controller-pid", type=int, required=True)
    observe_parser.add_argument("--output-dir", type=Path, required=True)
    observe_parser.add_argument("--max-seconds", type=float, default=7200)
    summary_parser = commands.add_parser("summarize")
    summary_parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    (observe if args.command == "observe" else summarize)(args)


if __name__ == "__main__":
    main()
