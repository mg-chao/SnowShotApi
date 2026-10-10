from __future__ import annotations

import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
import types
from pathlib import Path

import pytest

from latex_rec_service import prefetch
from latex_rec_service.model_manifest import ARCHIVE_ROOT, MODEL_NAME, load_manifest, verify_models

FILES = {"inference.json": b"graph", "inference.pdiparams": b"weights", "inference.yml": b"configuration"}


def archive_bytes(members=None):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w") as archive:
        root = tarfile.TarInfo("./" + ARCHIVE_ROOT)
        root.type = tarfile.DIRTYPE
        archive.addfile(root)
        for name, content, kind in members or [(name, value, tarfile.REGTYPE) for name, value in FILES.items()]:
            info = tarfile.TarInfo("./" + ARCHIVE_ROOT + "/" + name)
            info.size = len(content)
            info.type = kind
            if kind == tarfile.SYMTYPE:
                info.linkname = "../../outside"
                info.size = 0
            archive.addfile(info, io.BytesIO(content) if kind == tarfile.REGTYPE else None)
    return buffer.getvalue()


def manifest(tmp_path, archive=None):
    archive = archive or archive_bytes()
    document = {
        "schema_version": 1, "model_name": MODEL_NAME,
        "archive": {
            "url": "https://models.test/model.tar", "root": ARCHIVE_ROOT,
            "size_bytes": len(archive), "sha256": hashlib.sha256(archive).hexdigest(),
        },
        "files": [{"path": name, "size_bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest()}
                  for name, payload in FILES.items()],
    }
    path = tmp_path / "manifest.json"
    path.write_text(json.dumps(document), encoding="utf-8")
    return path


def install_files(path):
    path.mkdir()
    for name, payload in FILES.items():
        (path / name).write_bytes(payload)


def respond(monkeypatch, payload, error=None):
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def raise_for_status(self):
            if error:
                raise error

        def iter_content(self, chunk_size):
            return iter([payload[:5], b"", payload[5:]])

    calls = []
    monkeypatch.setattr(prefetch.requests, "get", lambda *a, **kw: calls.append((a, kw)) or Response())
    return calls


def test_cached_models_are_all_hash_verified(tmp_path):
    model_dir = tmp_path / "models"
    install_files(model_dir)
    manifest_path = manifest(tmp_path)
    verify_models(model_dir, manifest_path)
    (model_dir / "inference.yml").write_bytes(b"tampered data")
    with pytest.raises(ValueError, match="hash verification failed"):
        verify_models(model_dir, manifest_path)


def test_alternate_configuration_is_rejected(tmp_path):
    model_dir = tmp_path / "models"
    install_files(model_dir)
    (model_dir / "config.json").write_text("{}")
    with pytest.raises(ValueError, match="exactly three"):
        verify_models(model_dir, manifest(tmp_path))


@pytest.mark.parametrize("field,value", [
    ("model_name", "PP-FormulaNet_plus-M"), ("schema_version", 2), ("schema_version", True),
])
def test_wrong_model_or_schema_is_rejected(tmp_path, field, value):
    path = manifest(tmp_path)
    document = json.loads(path.read_text())
    document[field] = value
    path.write_text(json.dumps(document))
    with pytest.raises(ValueError, match="schema version 1"):
        load_manifest(path)


@pytest.mark.parametrize("field,value", [
    ("path", "../inference.json"), ("path", "/inference.json"),
    ("path", "config.json"), ("sha256", "g" * 64), ("sha256", "A" * 64),
    ("size_bytes", -1), ("size_bytes", True),
])
def test_invalid_file_entry_is_rejected(tmp_path, field, value):
    path = manifest(tmp_path)
    document = json.loads(path.read_text())
    document["files"][0][field] = value
    path.write_text(json.dumps(document))
    with pytest.raises(ValueError):
        load_manifest(path)


def test_duplicate_manifest_files_are_rejected(tmp_path):
    path = manifest(tmp_path)
    document = json.loads(path.read_text())
    document["files"][1] = document["files"][0]
    path.write_text(json.dumps(document))
    with pytest.raises(ValueError, match="exactly"):
        load_manifest(path)


@pytest.mark.parametrize("url", ["http://models.test/model.tar", "https://user:secret@models.test/model.tar"])
def test_archive_url_must_use_https_without_credentials(tmp_path, url):
    path = manifest(tmp_path)
    document = json.loads(path.read_text())
    document["archive"]["url"] = url
    path.write_text(json.dumps(document))
    with pytest.raises(ValueError, match="HTTPS"):
        load_manifest(path)


def test_download_hash_mismatch_preserves_old_file_and_cleans_temporary(tmp_path, monkeypatch):
    respond(monkeypatch, b"wrong")
    destination = tmp_path / "model.tar"
    destination.write_bytes(b"old")
    with pytest.raises(ValueError, match="hash or size mismatch"):
        prefetch.download_file("https://models.test/model.tar", destination, "0" * 64, 5)
    assert destination.read_bytes() == b"old"
    assert not list(tmp_path.glob("*.download"))


def test_overlong_download_is_stopped_and_not_installed(tmp_path, monkeypatch):
    respond(monkeypatch, b"longer")
    destination = tmp_path / "model.tar"
    with pytest.raises(ValueError, match="exceeds"):
        prefetch.download_file("https://models.test/model.tar", destination, "0" * 64, 2)
    assert not destination.exists() and not list(tmp_path.glob("*.download"))


def test_network_error_cleans_partial_download(tmp_path, monkeypatch):
    respond(monkeypatch, b"", OSError("network failed"))
    destination = tmp_path / "model.tar"
    with pytest.raises(OSError, match="network failed"):
        prefetch.download_file("https://models.test/model.tar", destination, "0" * 64, 1)
    assert not destination.exists() and not list(tmp_path.glob("*.download"))


@pytest.mark.parametrize("members", [
    [("../outside", b"bad", tarfile.REGTYPE)],
    [("inference.json", b"", tarfile.SYMTYPE)],
    [("inference.json", b"graph", tarfile.REGTYPE), ("inference.json", b"graph", tarfile.REGTYPE)],
    [("inference.json", b"wrong size", tarfile.REGTYPE)],
    [("inference.json", b"graph", tarfile.REGTYPE)],
])
def test_unsafe_incomplete_or_duplicate_archive_is_rejected(tmp_path, members):
    payload = archive_bytes(members)
    archive_path = tmp_path / "model.tar"
    archive_path.write_bytes(payload)
    with pytest.raises(ValueError):
        prefetch.extract_archive(archive_path, tmp_path / "staged", load_manifest(manifest(tmp_path, payload)))
    assert not (tmp_path / "outside").exists()


def test_prefetch_preflights_staged_model_before_install(tmp_path, monkeypatch):
    payload = archive_bytes()
    calls = respond(monkeypatch, payload)
    preflight = []
    monkeypatch.setattr(prefetch, "_preflight", lambda path, threads: preflight.append((path, threads)))
    monkeypatch.setenv("LATEX_REC_CPU_THREADS", "2")
    monkeypatch.setenv("LATEX_REC_ENVIRONMENT", "production")
    monkeypatch.setenv("LATEX_REC_TLS_CERTIFICATE", str(tmp_path / "not-installed.pem"))
    destination = tmp_path / "models"
    manifest_path = manifest(tmp_path, payload)
    prefetch.prefetch(destination, manifest_path)
    verify_models(destination, manifest_path)
    assert len(calls) == 1 and calls[0][1]["timeout"] == (15, 180)
    assert preflight[0][0] != destination and preflight[0][1] == 2
    assert not list(tmp_path.glob(".models-prefetch-*"))


def test_valid_cached_model_does_not_download(tmp_path, monkeypatch):
    destination = tmp_path / "models"
    install_files(destination)
    calls = []
    monkeypatch.setattr(prefetch, "_preflight", lambda *args: calls.append(args))
    monkeypatch.setattr(prefetch.requests, "get", lambda *a, **kw: pytest.fail("Cached models were downloaded"))
    monkeypatch.setenv("LATEX_REC_CPU_THREADS", "1")
    prefetch.prefetch(destination, manifest(tmp_path))
    assert calls == [(destination, 1)]


def test_preflight_failure_leaves_existing_model_intact(tmp_path, monkeypatch):
    destination = tmp_path / "models"
    install_files(destination)
    (destination / "inference.yml").write_bytes(b"old")
    respond(monkeypatch, archive_bytes())
    monkeypatch.setattr(prefetch, "_preflight", lambda *_: (_ for _ in ()).throw(RuntimeError("warmup failed")))
    with pytest.raises(RuntimeError, match="warmup failed"):
        prefetch.prefetch(destination, manifest(tmp_path))
    assert (destination / "inference.yml").read_bytes() == b"old"
    assert not list(tmp_path.glob(".models-prefetch-*"))


def test_failed_directory_exchange_preserves_previous_model(tmp_path, monkeypatch):
    destination = tmp_path / "models"
    install_files(destination)
    (destination / "inference.yml").write_bytes(b"old")
    respond(monkeypatch, archive_bytes())
    monkeypatch.setattr(prefetch, "_preflight", lambda *_: None)
    monkeypatch.setattr(prefetch, "_atomic_exchange", lambda *_: (_ for _ in ()).throw(OSError("commit failed")))
    with pytest.raises(OSError, match="commit failed"):
        prefetch.prefetch(destination, manifest(tmp_path))
    assert (destination / "inference.yml").read_bytes() == b"old"
    assert not list(tmp_path.glob(".models-prefetch-*"))


@pytest.mark.skipif(sys.platform != "linux", reason="Ubuntu/WSL atomic publication and process-crash test")
@pytest.mark.parametrize("crash_after_exchange", [False, True])
def test_hard_interruption_keeps_visible_models_and_next_prefetch_recovers(tmp_path, monkeypatch, crash_after_exchange):
    destination = tmp_path / "models"
    install_files(destination)
    (destination / "inference.yml").write_bytes(b"old")
    payload = archive_bytes()
    archive_path = tmp_path / "source.tar"
    archive_path.write_bytes(payload)
    manifest_path = manifest(tmp_path, payload)
    child = r'''
import os, shutil, sys
from pathlib import Path
from latex_rec_service import prefetch
archive, destination, manifest, after = sys.argv[1:]
prefetch.download_file = lambda url, path, digest, size: shutil.copyfile(archive, path)
prefetch._preflight = lambda *args: None
exchange = prefetch._atomic_exchange
def interrupt(first, second):
    if after == "True":
        exchange(first, second)
    os._exit(23)
prefetch._atomic_exchange = interrupt
prefetch.prefetch(Path(destination), Path(manifest))
'''
    environment = os.environ.copy()
    environment["PYTHONPATH"] = str(Path(__file__).resolve().parents[1])
    environment["LATEX_REC_CPU_THREADS"] = "1"
    completed = subprocess.run(
        [sys.executable, "-c", child, str(archive_path), str(destination), str(manifest_path), str(crash_after_exchange)],
        env=environment, capture_output=True, text=True, timeout=30,
    )
    assert completed.returncode == 23, completed.stderr
    assert destination.is_dir()
    assert (destination / "inference.yml").read_bytes() == (FILES["inference.yml"] if crash_after_exchange else b"old")
    assert len(list(tmp_path.glob(".models-prefetch-*"))) == 1
    respond(monkeypatch, payload)
    monkeypatch.setattr(prefetch, "_preflight", lambda *_: None)
    prefetch.prefetch(destination, manifest_path)
    verify_models(destination, manifest_path)
    assert not list(tmp_path.glob(".models-prefetch-*"))


def test_unmarked_staging_directory_is_never_deleted(tmp_path, monkeypatch):
    destination = tmp_path / "models"
    install_files(destination)
    unrelated = tmp_path / ".models-prefetch-user-files"
    unrelated.mkdir()
    (unrelated / "keep.txt").write_text("keep")
    monkeypatch.setattr(prefetch, "_preflight", lambda *_: None)
    prefetch.prefetch(destination, manifest(tmp_path))
    assert (unrelated / "keep.txt").read_text() == "keep"


def test_unrelated_destination_files_are_never_replaced(tmp_path, monkeypatch):
    destination = tmp_path / "models"
    install_files(destination)
    unrelated = destination / "keep.txt"
    unrelated.write_text("keep")
    monkeypatch.setattr(prefetch.requests, "get", lambda *a, **kw: pytest.fail("Unexpected download"))
    with pytest.raises(ValueError, match="unrelated"):
        prefetch.prefetch(destination, manifest(tmp_path))
    assert unrelated.read_text() == "keep"


def test_preflight_closes_native_predictor(monkeypatch, tmp_path):
    calls = []
    model = type("Model", (), {"close": lambda self: calls.append("close")})()
    engine = types.SimpleNamespace(EngineBundle=types.SimpleNamespace(load=lambda *args: calls.append(args) or model))
    monkeypatch.setitem(sys.modules, "latex_rec_service.engine", engine)
    for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS", "TOKENIZERS_PARALLELISM"):
        monkeypatch.setenv(name, "9")
    prefetch._preflight(tmp_path, 1)
    assert calls == [(tmp_path, 1), "close"]
