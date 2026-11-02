from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "redis" / "Render-Acl.ps1"


def test_redis_acl_is_hashed_namespaced_and_non_dangerous(tmp_path: Path) -> None:
    password = "a-production-strength-redis-password-123"
    password_file = tmp_path / "password.txt"
    output = tmp_path / "users.acl"
    password_file.write_text(password, encoding="ascii")

    subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(SCRIPT),
            "-PasswordFile",
            str(password_file),
            "-OutputPath",
            str(output),
        ],
        check=True,
        capture_output=True,
        text=True,
    )

    acl = output.read_text(encoding="utf-8")
    expected_hash = hashlib.sha256(password.encode()).hexdigest()
    assert password not in acl
    assert f"#{expected_hash}" in acl
    assert "~{snowshot:*}:*" in acl
    assert "resetchannels" in acl
    assert "&*" not in acl
    assert "+@scripting" in acl
    assert "-@dangerous" in acl
