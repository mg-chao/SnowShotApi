from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from table_rec_service.model_manifest import load_manifest, verify_models
from table_rec_service.prefetch import download_file


def manifest(tmp_path: Path, digest: str, revision: str = "a" * 40) -> Path:
    path = tmp_path / "manifest.json"
    path.write_text(
        json.dumps(
            {
                "revision": revision,
                "files": [
                    {
                        "path": "model.bin",
                        "url": "https://models.test/model.bin",
                        "sha256": digest,
                    }
                ],
            }
        ),
        encoding="utf-8",
    )
    return path


def test_cached_models_are_hash_verified(tmp_path: Path) -> None:
    payload = b"verified model"
    expected = hashlib.sha256(payload).hexdigest()
    model = tmp_path / "model.bin"
    model.write_bytes(payload)
    verify_models(tmp_path, manifest(tmp_path, expected))
    model.write_bytes(b"tampered")
    with pytest.raises(ValueError, match="hash verification failed"):
        verify_models(tmp_path, manifest(tmp_path, expected))


def test_manifest_rejects_mutable_revision(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="immutable"):
        load_manifest(manifest(tmp_path, "0" * 64, revision="master"))


def test_download_hash_mismatch_is_not_installed(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def raise_for_status(self) -> None:
            return None

        def iter_content(self, chunk_size: int):
            return iter([b"wrong"])

    monkeypatch.setattr("table_rec_service.prefetch.requests.get", lambda *args, **kwargs: Response())
    destination = tmp_path / "model.bin"
    with pytest.raises(ValueError, match="hash mismatch"):
        download_file("https://models.test/model.bin", destination, "0" * 64)
    assert not destination.exists()
