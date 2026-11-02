"""Behavioral checks for the bounded benchmark driver and synchronized starts."""
from __future__ import annotations

import json
from pathlib import Path
import sys
import time
from types import SimpleNamespace
import venv
import subprocess

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import recognition_http
import sweep_formula_cpu
from recognition_cpu import corpus, interleave_formula_entries, percentile
from sweep_formula_cpu import interpreter_path, native, preflight_runtime, selection, stop_http_workers, terminate, topologies


def test_formula_corpus_interleaves_every_base_before_repeating_variants():
    root = Path(__file__).resolve().parents[3] / "services/FormulaRecognition"
    manifest = root / "tests/corpus/manifest.json"
    entries = json.loads(manifest.read_text(encoding="utf-8"))
    eligible = [entry for entry in entries if entry.get("benchmark", True) is not False]
    samples = corpus(root, "latex")
    names = [sample[0] for sample in samples]

    def base(entry):
        name, separator, suffix = entry["id"].rpartition("-")
        return name if entry.get("category") != "historical" and separator and suffix in ("light", "dark", "scaled", "transparent") else entry["id"]

    first_by_base = {}
    for entry in eligible:
        first_by_base.setdefault(base(entry), entry["id"])
    assert len(samples) == len(eligible) == len(set(names)) == 43
    assert len(first_by_base) == 14
    assert names[:14] == list(first_by_base.values())
    assert set(names) == {entry["id"] for entry in eligible}
    assert names == [entry["id"] for entry in interleave_formula_entries(eligible)]
    assert recognition_http.corpus is corpus
    for name, payload, metadata in samples:
        expected = next(entry for entry in eligible if entry["id"] == name)
        assert payload == (manifest.parent / expected["image"]).read_bytes()
        assert metadata["expectation"] == expected
    for family in first_by_base:
        assert [name for name in names if base({"id": name}) == family] == [entry["id"] for entry in eligible if base(entry) == family]


def test_topologies_cover_disjoint_core_budgets_and_shared_controls():
    cores = [[101 + core * 3, 103 + core * 3] for core in range(8)]
    candidates = topologies(cores)
    assert len(candidates) == 12
    for candidate in candidates[:9]:
        assert set(candidate["table_affinity"]).isdisjoint(candidate["latex_affinity"])
        assert set(candidate["table_affinity"]) | set(candidate["latex_affinity"]) == {cpu for core in cores for cpu in core}
        assert candidate["table_threads"] + candidate["formula_cores"] == 8
        assert candidate["latex_threads"] <= candidate["formula_cores"]
    for candidate in candidates[9:]:
        assert candidate["table_affinity"] == candidate["latex_affinity"]
    assert percentile([1, 2, 3, 4], .95) == 4


@pytest.mark.skipif(sys.platform != "linux", reason="Linux venv Python symlinks")
def test_interpreter_symlink_keeps_venv_packages_and_preflight_fails_fast(tmp_path):
    environment = tmp_path / "venv"
    venv.EnvBuilder(with_pip=False, symlinks=True).create(environment)
    python = interpreter_path(environment / "bin/python")
    assert python.is_symlink()
    packages = Path(subprocess.check_output([str(python), "-c", "import sysconfig;print(sysconfig.get_paths()['purelib'])"], text=True).strip())
    (packages / "benchmark_venv_marker.py").write_text("VALUE = 1\n", encoding="utf-8")
    result = preflight_runtime(python, "test", ["benchmark_venv_marker"])
    assert Path(result["prefix"]) == environment
    with pytest.raises(RuntimeError, match="dependency preflight failed"):
        preflight_runtime(python, "test", ["benchmark_definitely_missing_module"])


@pytest.mark.skipif(sys.platform != "linux", reason="Ubuntu/WSL sweep entry point")
@pytest.mark.parametrize("fixture", ["wired/table1.png", "lineless_table_recognition.jpg", "table.jpg"])
def test_table_fixture_mutation_invalidates_reused_screen(monkeypatch, tmp_path, fixture):
    root = tmp_path / "repository"

    def save(relative, payload):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)
        return path

    for service in ("TableStructureRec", "FormulaRecognition"):
        save(f"services/{service}/model-manifest.json", b'{"files":[{"path":"model.bin"}]}')
        save(f"services/{service}/models/model.bin", b"unchanged model")
    for name in ("recognition_cpu.py", "recognition_http.py", "sweep_formula_cpu.py"):
        save(f"scripts/benchmarks/{name}", b"unchanged source")
    table_paths = [f"services/TableStructureRec/tests/test_files/{name}"
                   for name in ("wired/table1.png", "lineless_table_recognition.jpg", "table.jpg")]
    for path in table_paths:
        save(path, path.encode())
    save("services/FormulaRecognition/tests/corpus/manifest.json",
         b'[{"id":"formula","image":"formula.webp","expected_latex":"x"}]')
    save("services/FormulaRecognition/tests/corpus/formula.webp", b"unchanged formula")
    monkeypatch.setattr(sweep_formula_cpu, "preflight_runtime", lambda *_args: {"executable": sys.executable})
    monkeypatch.setattr(sweep_formula_cpu, "physical_cores", lambda: [[core] for core in range(8)])
    monkeypatch.setattr(sweep_formula_cpu.subprocess, "check_output", lambda command, **_kwargs:
                        '{"lscpu":[]}' if command[0] == "lscpu" else '{"python":"test","packages":[]}')
    native_calls = []

    def measure(_args, candidate):
        native_calls.append(candidate["name"])
        return {"configuration": candidate, "balanced_capacity_rps": 1, "reports": {}}

    monkeypatch.setattr(sweep_formula_cpu, "native", measure)
    output = tmp_path / "output"
    argv = ["sweep", "--root", str(root), "--table-python", sys.executable,
            "--latex-python", sys.executable, "--output-dir", str(output),
            "--stage", "screen", "--candidate-limit", "1"]
    monkeypatch.setattr(sys, "argv", argv)
    sweep_formula_cpu.main()
    initial = json.loads((output / "summary.json").read_text())
    assert all(path in initial["fingerprint"]["files"] for path in table_paths)
    monkeypatch.setattr(sys, "argv", argv + ["--reuse-screen"])
    sweep_formula_cpu.main()
    assert len(native_calls) == 1, "Unchanged input should reuse the measured screen"

    changed = f"services/TableStructureRec/tests/test_files/{fixture}"
    save(changed, b"changed fixture bytes")
    with pytest.raises(RuntimeError, match="reference must pass"):
        sweep_formula_cpu.main()
    rejected = json.loads((output / "summary.json").read_text())
    assert len(native_calls) == 1, "Changed cached inputs must be rejected before any native measurement"
    assert rejected["screen"] == []
    assert "Cached screen fingerprint differs" in rejected["errors"][0]["error"]
    assert rejected["fingerprint"]["sha256"] != initial["fingerprint"]["sha256"]
    before, after = initial["fingerprint"]["files"], rejected["fingerprint"]["files"]
    assert {path for path in before if before[path] != after[path]} == {changed}


def test_selection_rejects_inconsistent_reference_wins_and_prefers_simple_ties():
    candidates = topologies([[core] for core in range(8)])[:3]
    reference, inconsistent, fast = candidates
    confirmations = {
        reference["name"]: [{"balanced_pairs_rps": value} for value in (1, 1, 1)],
        inconsistent["name"]: [{"balanced_pairs_rps": value} for value in (.8, .9, 10)],
        fast["name"]: [{"balanced_pairs_rps": value} for value in (1.5, 1.6, 1.7)],
    }
    assert selection(candidates, confirmations, reference)[0] == fast
    confirmations[fast["name"]] = [{"balanced_pairs_rps": value} for value in (1.01, 1.02, 1.03)]
    assert selection(candidates, confirmations, reference)[0] == reference
    confirmations[fast["name"]] = [{"balanced_pairs_rps": 1.04}] * 3
    assert selection(candidates, confirmations, reference)[0] == fast


def test_native_waits_until_both_services_are_ready(tmp_path):
    script = tmp_path / "scripts/benchmarks/recognition_cpu.py"
    script.parent.mkdir(parents=True)
    script.write_text('''import argparse,json,pathlib,time
p=argparse.ArgumentParser();p.add_argument('--service');p.add_argument('--barrier-dir');p.add_argument('--output');a,_=p.parse_known_args()
b=pathlib.Path(a.barrier_dir);b.mkdir(parents=True,exist_ok=True)
if a.service=='latex':time.sleep(.15)
(b/(a.service+'.ready.json')).write_text('{}')
while not (b/'start.json').exists():time.sleep(.01)
assert (b/'table.ready.json').exists() and (b/'latex.ready.json').exists()
pathlib.Path(a.output).write_text(json.dumps({'throughput_rps':1,'errors':[]}))
''', encoding="utf-8")
    args = SimpleNamespace(root=tmp_path, output_dir=tmp_path / "output", table_python=Path(sys.executable),
                           latex_python=Path(sys.executable), table_model_dir=tmp_path, latex_model_dir=tmp_path,
                           screen_seconds=.02)
    report = native(args, topologies([[core] for core in range(8)])[0])
    assert report["balanced_capacity_rps"] == 1


@pytest.mark.skipif(sys.platform != "linux", reason="Linux process-group cleanup")
def test_failed_peer_cleans_up_spawned_native_children(tmp_path):
    script = tmp_path / "scripts/benchmarks/recognition_cpu.py"
    script.parent.mkdir(parents=True)
    script.write_text('''import argparse,pathlib,subprocess,sys,time
p=argparse.ArgumentParser();p.add_argument('--service');p.add_argument('--barrier-dir');a,_=p.parse_known_args()
b=pathlib.Path(a.barrier_dir);b.mkdir(parents=True,exist_ok=True);child_pid=b/'child.pid'
if a.service=='table':
    child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)'])
    child_pid.write_text(str(child.pid))
    (b/'table.ready.json').write_text('{}')
    time.sleep(60)
else:
    while not child_pid.exists():time.sleep(.01)
    sys.exit(1)
''', encoding="utf-8")
    args = SimpleNamespace(root=tmp_path, output_dir=tmp_path / "output", table_python=Path(sys.executable),
                           latex_python=Path(sys.executable), table_model_dir=tmp_path, latex_model_dir=tmp_path,
                           screen_seconds=.02)
    candidate = topologies([[core] for core in range(8)])[0]
    with pytest.raises(RuntimeError, match="warmup failed"):
        native(args, candidate)
    pid = int((args.output_dir / "screen" / candidate["name"] / "barrier/child.pid").read_text())
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        status = Path(f"/proc/{pid}/stat")
        if not status.exists() or status.read_text().split()[2] == "Z":
            break
        time.sleep(.02)
    else:
        pytest.fail("Native child remains running after its peer failed")


@pytest.mark.skipif(sys.platform != "linux", reason="Linux signal replay and process-group teardown")
@pytest.mark.parametrize("behavior", ["clean_sigterm", "clean_zero", "missing_marker", "wrong_pid", "paddle_fatal", "abnormal_exit", "timeout"])
def test_http_teardown_requires_lifespan_marker_and_rejects_native_signal_failure(tmp_path, behavior):
    script = tmp_path / "fake_worker.py"
    script.write_text('''import os,pathlib,signal,sys,time
kind,behavior,ready=sys.argv[1:]
def stop(signum,frame):
    if kind=='latex' and behavior=='paddle_fatal':
        print('FatalError: Termination signal; SIGTERM received',flush=True)
    if not (kind=='latex' and behavior=='missing_marker'):
        pid=0 if kind=='latex' and behavior=='wrong_pid' else os.getpid()
        print(f'worker_stopped worker_pid={pid}',flush=True)
    if kind=='latex' and behavior=='abnormal_exit':sys.exit(70)
    if kind=='latex' and behavior in ('clean_zero','missing_marker','wrong_pid'):
        sys.exit(0)
    signal.signal(signal.SIGTERM,signal.SIG_DFL)
    os.kill(os.getpid(),signal.SIGTERM)
signal.signal(signal.SIGTERM,signal.SIG_IGN if kind=='latex' and behavior=='timeout' else stop)
pathlib.Path(ready).write_text('ready')
while True:time.sleep(.05)
''', encoding="utf-8")
    processes, streams = [], []
    try:
        for kind in ("table", "latex"):
            stream = (tmp_path / f"{kind}.log").open("w", encoding="utf-8")
            streams.append(stream)
            process = subprocess.Popen([sys.executable, str(script), kind, behavior, str(tmp_path / f"{kind}.ready")],
                                       stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
            process.benchmark_process_group = True
            processes.append(process)
        deadline = time.monotonic() + 5
        while not all((tmp_path / f"{kind}.ready").exists() for kind in ("table", "latex")):
            assert time.monotonic() < deadline, "Fake workers failed to initialize"
            time.sleep(.01)
        timeout = .3 if behavior == "timeout" else 3
        if behavior.startswith("clean"):
            evidence = stop_http_workers(processes, tmp_path, timeout=timeout)
            assert evidence["errors"] == []
            assert evidence["workers"]["latex"]["worker_stopped"]
            assert evidence["workers"]["latex"]["returncode"] == (0 if behavior == "clean_zero" else -15)
        else:
            with pytest.raises(RuntimeError, match="mTLS worker teardown failed"):
                stop_http_workers(processes, tmp_path, timeout=timeout)
        saved = json.loads((tmp_path / "worker-teardown.json").read_text())
        assert saved["workers"]["latex"]["observed_stop_seconds"] > 0
        assert all(process.poll() is not None for process in processes)
        if behavior == "timeout":
            assert saved["workers"]["latex"]["forced_kill"]
            assert saved["workers"]["latex"]["returncode"] == -9
        if behavior == "paddle_fatal":
            assert saved["workers"]["latex"]["worker_stopped"]
            assert saved["workers"]["latex"]["paddle_sigterm_fatal"]
        if behavior == "abnormal_exit":
            assert saved["workers"]["latex"]["returncode"] == 70
    finally:
        terminate(processes)
        for stream in streams:
            stream.close()


@pytest.mark.parametrize("failure", [None, "latex", "table"])
def test_http_backlog_is_bounded_and_peer_failure_cannot_deadlock(monkeypatch, tmp_path, failure):
    class Client:
        def __init__(self, kind):
            self.kind, self.path, self.calls = kind, "", 0
        def request(self, method, path, **_kwargs):
            self.path = path
        def getresponse(self):
            if self.path == "/health/ready":
                return SimpleNamespace(status=200, read=lambda: b'{"status":"ready"}')
            self.calls += 1
            if self.kind == "latex":
                time.sleep(.02)
            if failure == self.kind and self.calls > 2:
                raise ConnectionError("simulated inference connection loss")
            key = "html" if self.kind == "table" else "latex"
            return SimpleNamespace(status=200, read=lambda: json.dumps({key: "valid"}).encode())
        def close(self):
            pass
    output = tmp_path / "http.json"
    monkeypatch.setattr(recognition_http, "connection", lambda kind, *_args: Client(kind))
    monkeypatch.setattr(recognition_http, "corpus", lambda *_args: [("fixture", b"image", {"name": "fixture"})])
    monkeypatch.setattr(recognition_http, "corpus_omissions", lambda *_args: [])
    monkeypatch.setattr(recognition_http, "validate_output", lambda *_args: None)
    monkeypatch.setattr(sys, "argv", ["recognition_http", "--root", str(tmp_path), "--seconds", ".06", "--output", str(output)])
    started = time.monotonic()
    if failure:
        with pytest.raises(SystemExit):
            recognition_http.main()
    else:
        recognition_http.main()
    assert time.monotonic() - started < 5
    report = json.loads(output.read_text())
    assert report["peak_formula_backlog"] <= 8
    if failure:
        assert report["errors"]
    else:
        assert report["offered_pairs"] == report["services"]["latex"]["successes"] == report["services"]["table"]["successes"]
