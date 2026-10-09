from types import SimpleNamespace

import pytest

import rapid_latex_ocr.utils_load as upstream
from latex_rec_service import prefetch


def test_cpu_session_uses_explicit_cpu_provider_and_disables_fallback(monkeypatch, tmp_path):
    model = tmp_path / "model.onnx"
    model.write_bytes(b"unused")
    calls = {}

    def create_session(path, *, sess_options, providers):
        calls["providers"] = providers
        return SimpleNamespace(
            disable_fallback=lambda: calls.update(fallback_disabled=True),
            get_providers=lambda: providers,
        )

    monkeypatch.setattr(upstream, "get_available_providers", lambda: ["CPUExecutionProvider"])
    monkeypatch.setattr(upstream, "InferenceSession", create_session)
    upstream.OrtInferSession(model, execution_provider="CPUExecutionProvider")
    assert calls == {"providers": ["CPUExecutionProvider"], "fallback_disabled": True}


def test_missing_cpu_provider_fails_closed(monkeypatch, tmp_path):
    model = tmp_path / "model.onnx"
    model.write_bytes(b"unused")
    monkeypatch.setattr(upstream, "get_available_providers", lambda: [])
    with pytest.raises(RuntimeError, match="CPUExecutionProvider is required"):
        upstream.OrtInferSession(model, execution_provider="CPUExecutionProvider")


def test_prefetch_does_not_require_http_tls_settings(monkeypatch, tmp_path):
    monkeypatch.setenv("LATEX_REC_ENVIRONMENT", "production")
    monkeypatch.setenv("LATEX_REC_TLS_CERTIFICATE", str(tmp_path / "not-installed.pem"))
    monkeypatch.setenv("LATEX_REC_EXECUTION_PROVIDER", "CPUExecutionProvider")
    monkeypatch.setenv("LATEX_REC_CPU_THREADS", "2")
    monkeypatch.setattr(prefetch, "load_manifest", lambda _: [])
    monkeypatch.setattr(prefetch, "verify_models", lambda *_: None)
    calls = []
    monkeypatch.setattr(prefetch, "EngineBundle", SimpleNamespace(load=lambda *args: calls.append(args)))

    prefetch.prefetch(tmp_path, tmp_path / "manifest.json")

    assert calls == [(tmp_path, "CPUExecutionProvider", 2)]
