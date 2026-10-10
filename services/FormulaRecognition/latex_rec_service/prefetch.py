from __future__ import annotations

import argparse
import asyncio
import ctypes
import json
import os
import shutil
import sys
import tarfile
import tempfile
from pathlib import Path

import requests
from filelock import FileLock

from .config import _bounded_integer, available_cpu_count
from .inference_gate import InferenceGate
from .model_manifest import MANIFEST_PATH, MODEL_FILES, ModelManifest, load_manifest, sha256, verify_models


def download_file(
    url: str, destination: Path, expected_sha256: str, expected_size: int
) -> None:
    """Stream a bounded archive into a unique temporary file and commit after verification."""
    if destination.is_file() and destination.stat().st_size == expected_size and sha256(destination) == expected_sha256:
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            dir=destination.parent, prefix=destination.name + ".", suffix=".download", delete=False
        ) as output:
            temporary = Path(output.name)
            received = 0
            with requests.get(url, stream=True, timeout=(15, 180)) as response:
                response.raise_for_status()
                for chunk in response.iter_content(chunk_size=1024 * 1024):
                    if not chunk:
                        continue
                    received += len(chunk)
                    if received > expected_size:
                        raise ValueError("Downloaded archive exceeds its manifest size.")
                    output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        if received != expected_size or sha256(temporary) != expected_sha256:
            raise ValueError("Downloaded model archive hash or size mismatch.")
        temporary.replace(destination)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def extract_archive(archive_path: Path, destination: Path, manifest: ModelManifest) -> None:
    """Extract only declared regular files; never let tar names select output paths."""
    destination.mkdir()
    expected = {f"{manifest.archive.root}/{item.path.as_posix()}": item for item in manifest.files}
    seen = set()
    with tarfile.open(archive_path, mode="r:*") as archive:
        for member in archive:
            name = member.name.removeprefix("./").rstrip("/")
            if name in seen:
                raise ValueError("Duplicate model archive member.")
            seen.add(name)
            if name == manifest.archive.root:
                if not member.isdir():
                    raise ValueError("Model archive root must be a directory.")
                continue
            item = expected.get(name)
            if item is None or not member.isfile() or member.size != item.size_bytes:
                raise ValueError("Unexpected model archive member, type, or size.")
            source = archive.extractfile(member)
            if source is None:
                raise ValueError("Missing model archive member data.")
            with source, (destination / item.path).open("xb") as output:
                shutil.copyfileobj(source, output, length=1024 * 1024)
                output.flush()
                os.fsync(output.fileno())
    if not set(expected).issubset(seen):
        raise ValueError("Model archive is missing a declared inference file.")


def _runtime_limits(cpu_threads: int | None = None) -> tuple[int, int, int]:
    """Validate native settings without requiring deployment TLS files."""
    if "LATEX_REC_EXECUTION_PROVIDER" in os.environ:
        raise ValueError(
            "LATEX_REC_EXECUTION_PROVIDER is obsolete; formula recognition uses Paddle CPU inference."
        )
    maximum = min(256, available_cpu_count())
    if cpu_threads is None:
        cpu_threads = _bounded_integer(os.environ, "LATEX_REC_CPU_THREADS", 1, 1, maximum)
    elif type(cpu_threads) is not int or not 1 <= cpu_threads <= maximum:
        raise ValueError(f"LATEX_REC_CPU_THREADS must be between 1 and {maximum}.")
    watchdog = _bounded_integer(os.environ, "LATEX_REC_WATCHDOG_SECONDS", 55, 1, 55)
    startup_watchdog = _bounded_integer(os.environ, "LATEX_REC_STARTUP_WATCHDOG_SECONDS", 120, 1, 300)
    return cpu_threads, watchdog, startup_watchdog


def _preflight(model_dir: Path, cpu_threads: int) -> None:
    cpu_threads, watchdog, startup_watchdog = _runtime_limits(cpu_threads)
    # These must be set before importing NumPy or Paddle. Model imports also
    # belong to the startup watchdog, because native imports can block.
    for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
        os.environ[name] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"

    bundle = None

    def load():
        nonlocal bundle
        from .engine import EngineBundle

        bundle = EngineBundle.load(model_dir, cpu_threads)
        return bundle

    def dispose() -> None:
        # Startup can finish after its awaiting caller was canceled. Evaluate
        # the retained bundle only after the gate has drained that native call.
        if bundle is not None:
            bundle.close()

    async def run() -> None:
        gate = InferenceGate(watchdog_seconds=watchdog, startup_watchdog_seconds=startup_watchdog)
        try:
            await gate.initialize(load)
        finally:
            await gate.close(dispose)

    asyncio.run(run())


def _atomic_exchange(first: Path, second: Path) -> None:
    """Ubuntu/WSL atomically exchanges two existing directories on one filesystem."""
    if sys.platform != "linux":
        raise RuntimeError("Replacing existing formula models requires Linux atomic directory exchange.")
    library = ctypes.CDLL(None, use_errno=True)
    try:
        renameat2 = library.renameat2
    except AttributeError as exception:
        raise RuntimeError("This Linux runtime does not provide atomic directory exchange.") from exception
    renameat2.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
    renameat2.restype = ctypes.c_int
    if renameat2(-100, os.fsencode(first), -100, os.fsencode(second), 2) != 0:
        error = ctypes.get_errno()
        raise OSError(error, "Formula model directory exchange failed; existing models remain intact.")


def _staging_owner(model_dir: Path, manifest: ModelManifest) -> dict[str, str]:
    return {"destination": str(model_dir), "archive_sha256": manifest.archive.sha256}


def _cleanup_abandoned_staging(model_dir: Path, manifest: ModelManifest) -> None:
    # A crash can leave the verified new model or the exchanged old model in a
    # staging directory. Only our marked siblings are removable, under the lock.
    prefix = f".{model_dir.name}-prefetch-"
    expected = _staging_owner(model_dir, manifest)
    for candidate in model_dir.parent.iterdir():
        if not candidate.name.startswith(prefix) or candidate.is_symlink() or not candidate.is_dir():
            continue
        marker = candidate / ".prefetch-owner.json"
        if marker.is_symlink() or not marker.is_file():
            continue
        try:
            if marker.stat().st_size > 4096 or json.loads(marker.read_text(encoding="utf-8")) != expected:
                continue
        except (OSError, ValueError):
            continue
        shutil.rmtree(candidate)


def prefetch(model_dir: Path, manifest_path: Path = MANIFEST_PATH) -> None:
    cpu_threads, _, _ = _runtime_limits()
    model_dir = model_dir.expanduser().absolute()
    model_dir.parent.mkdir(parents=True, exist_ok=True)
    with FileLock(str(model_dir.parent / f".{model_dir.name}.prefetch.lock"), timeout=180):
        _prefetch_locked(model_dir, manifest_path, cpu_threads)


def _prefetch_locked(model_dir: Path, manifest_path: Path, cpu_threads: int) -> None:
    manifest = load_manifest(manifest_path)
    _cleanup_abandoned_staging(model_dir, manifest)
    # Never replace a symlink or an unrelated directory. The old model remains
    # intact until a complete staged model passes hashes and native warmup.
    if model_dir.is_symlink() or (model_dir.exists() and not model_dir.is_dir()):
        raise ValueError("Model destination must be a regular directory.")
    if model_dir.exists() and any(child.name not in MODEL_FILES or not child.is_file() or child.is_symlink() for child in model_dir.iterdir()):
        raise ValueError("Model destination contains unrelated files or symlinks.")
    try:
        verify_models(model_dir, manifest_path)
    except ValueError:
        pass
    else:
        _preflight(model_dir, cpu_threads)
        print(f"PP-FormulaNet_plus-S models verified and preflighted in {model_dir}")
        return

    model_dir.parent.mkdir(parents=True, exist_ok=True)
    # Staging on the same filesystem permits directory rename without copying.
    with tempfile.TemporaryDirectory(dir=model_dir.parent, prefix=f".{model_dir.name}-prefetch-") as temporary:
        stage = Path(temporary)
        with (stage / ".prefetch-owner.json").open("w", encoding="utf-8") as marker:
            json.dump(_staging_owner(model_dir, manifest), marker)
            marker.flush()
            os.fsync(marker.fileno())
        archive_path = stage / "model.tar"
        download_file(manifest.archive.url, archive_path, manifest.archive.sha256, manifest.archive.size_bytes)
        extracted = stage / "model"
        extract_archive(archive_path, extracted, manifest)
        verify_models(extracted, manifest_path)
        _preflight(extracted, cpu_threads)
        if model_dir.exists():
            _atomic_exchange(extracted, model_dir)
        else:
            extracted.replace(model_dir)
    print(f"PP-FormulaNet_plus-S models downloaded, verified and preflighted in {model_dir}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Install the pinned PP-FormulaNet_plus-S archive before service startup.")
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, default=MANIFEST_PATH)
    arguments = parser.parse_args()
    prefetch(arguments.model_dir.expanduser().absolute(), arguments.manifest.expanduser().resolve())


if __name__ == "__main__":
    main()
