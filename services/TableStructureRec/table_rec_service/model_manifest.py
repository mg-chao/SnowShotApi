from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class ModelFile:
    path: Path
    url: str
    sha256: str


def load_manifest(path: Path) -> tuple[ModelFile, ...]:
    document: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
    revision = document.get("revision", "")
    if len(revision) != 40 or any(character not in "0123456789abcdef" for character in revision):
        raise ValueError("Model manifest revision must be an immutable 40-character commit.")
    files = tuple(
        ModelFile(Path(item["path"]), item["url"], item["sha256"].lower())
        for item in document.get("files", [])
    )
    if not files or any(len(item.sha256) != 64 for item in files):
        raise ValueError("Model manifest must contain SHA-256 values for every file.")
    return files


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_models(model_dir: Path, manifest_path: Path) -> None:
    for item in load_manifest(manifest_path):
        model = model_dir / item.path
        if not model.is_file() or sha256(model) != item.sha256:
            raise ValueError(f"Model hash verification failed: {item.path}")
