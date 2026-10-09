from types import SimpleNamespace

import pytest

from table_rec_service import prefetch
from table_rec_service.engine import _require_provider


def test_cpu_provider_requires_cpu_only_sessions():
    _require_provider({"classifier": ["CPUExecutionProvider"]}, "classifier", "CPUExecutionProvider")


@pytest.mark.parametrize("providers", [[], ["DmlExecutionProvider"], ["CPUExecutionProvider", "CUDAExecutionProvider"]])
def test_cpu_mode_rejects_missing_or_gpu_providers(providers):
    with pytest.raises(RuntimeError, match="CPU-only"):
        _require_provider({"classifier": providers}, "classifier", "CPUExecutionProvider")


def test_prefetch_does_not_require_http_tls_settings(monkeypatch, tmp_path):
    monkeypatch.setenv("TABLE_REC_ENVIRONMENT", "production")
    monkeypatch.setenv("TABLE_REC_TLS_CERTIFICATE", str(tmp_path / "not-installed.pem"))
    monkeypatch.setenv("TABLE_REC_EXECUTION_PROVIDER", "CPUExecutionProvider")
    monkeypatch.setenv("TABLE_REC_CPU_THREADS", "2")
    monkeypatch.setattr(prefetch, "load_manifest", lambda _: [])
    monkeypatch.setattr(prefetch, "verify_models", lambda *_: None)
    calls = []
    monkeypatch.setattr(prefetch, "EngineBundle", SimpleNamespace(load=lambda *args: calls.append(args)))

    prefetch.prefetch(tmp_path, tmp_path / "manifest.json")

    assert calls == [(tmp_path, "CPUExecutionProvider", 2)]
