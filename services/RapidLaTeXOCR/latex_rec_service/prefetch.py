from __future__ import annotations

import argparse
from pathlib import Path

import requests

from .engine import EngineBundle
from .model_manifest import load_manifest, sha256, verify_models


def download_file(url: str, destination: Path, expected_sha256: str) -> None:
    if destination.is_file() and sha256(destination) == expected_sha256:
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".download")
    with requests.get(url, stream=True, timeout=180) as response:
        response.raise_for_status()
        with temporary.open("wb") as output:
            for chunk in response.iter_content(chunk_size=1024 * 1024):
                if chunk:
                    output.write(chunk)
    if sha256(temporary) != expected_sha256:
        temporary.unlink(missing_ok=True)
        raise ValueError(f"Downloaded model hash mismatch: {destination.name}")
    temporary.replace(destination)


def prefetch(model_dir: Path, manifest_path: Path) -> None:
    model_dir.mkdir(parents=True, exist_ok=True)
    for item in load_manifest(manifest_path):
        download_file(item.url, model_dir / item.path, item.sha256)
    verify_models(model_dir, manifest_path)
    EngineBundle.load(model_dir)
    print(f"All models downloaded, verified, and preflighted in {model_dir}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "model-manifest.json",
    )
    arguments = parser.parse_args()
    prefetch(arguments.model_dir.expanduser().resolve(), arguments.manifest.resolve())


if __name__ == "__main__":
    main()
