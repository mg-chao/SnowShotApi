from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse

MODEL_NAME = "PP-FormulaNet_plus-S"
MODEL_FILES = frozenset({"inference.json", "inference.pdiparams", "inference.yml"})
ARCHIVE_ROOT = MODEL_NAME + "_infer"
MANIFEST_PATH = Path(__file__).resolve().parents[1] / "model-manifest.json"


@dataclass(frozen=True)
class ModelFile:
    path: Path
    sha256: str
    size_bytes: int


@dataclass(frozen=True)
class ModelArchive:
    url: str
    sha256: str
    size_bytes: int
    root: str


@dataclass(frozen=True)
class ModelManifest:
    model_name: str
    archive: ModelArchive
    files: tuple[ModelFile, ...]


def _digest(value: object) -> str:
    if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None:
        raise ValueError("Model manifest must contain lowercase SHA-256 values.")
    return value


def _size(value: object) -> int:
    if type(value) is not int or value <= 0:
        raise ValueError("Model manifest sizes must be positive integers.")
    return value


def load_manifest(path: Path = MANIFEST_PATH) -> ModelManifest:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
        if type(document.get("schema_version")) is not int or document.get("schema_version") != 1 or document.get("model_name") != MODEL_NAME:
            raise ValueError("Model manifest must describe PP-FormulaNet_plus-S with schema version 1.")
        archive = document["archive"]
        url = archive["url"]
        parsed = urlparse(url)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
            raise ValueError("Model archive must use an HTTPS URL without credentials.")
        if archive["root"] != ARCHIVE_ROOT:
            raise ValueError("Model archive root does not match the fixed model.")
        files = tuple(
            ModelFile(Path(item["path"]), _digest(item["sha256"]), _size(item["size_bytes"]))
            for item in document["files"]
        )
        # Exact names also reject absolute paths, traversal, duplicates and alternate configs.
        if len(files) != len(MODEL_FILES) or {item.path.as_posix() for item in files} != MODEL_FILES:
            raise ValueError("Model manifest must contain exactly the three Paddle inference files.")
        result = ModelManifest(
            MODEL_NAME,
            ModelArchive(url, _digest(archive["sha256"]), _size(archive["size_bytes"]), archive["root"]),
            files,
        )
        if result.archive.size_bytes < sum(item.size_bytes for item in files):
            raise ValueError("Model archive size is smaller than its declared files.")
        return result
    except (AttributeError, KeyError, TypeError) as exception:
        raise ValueError("Malformed model manifest.") from exception


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_models(model_dir: Path, manifest_path: Path = MANIFEST_PATH) -> None:
    manifest = load_manifest(manifest_path)
    if model_dir.is_symlink() or not model_dir.is_dir():
        raise ValueError("Model hash verification failed: model directory is missing or is a symlink.")
    if {item.name for item in model_dir.iterdir()} != MODEL_FILES:
        raise ValueError("Model hash verification failed: expected exactly three inference files.")
    for item in manifest.files:
        model = model_dir / item.path
        if (
            model.is_symlink()
            or not model.is_file()
            or model.stat().st_size != item.size_bytes
            or sha256(model) != item.sha256
        ):
            raise ValueError(f"Model hash verification failed: {item.path}")
