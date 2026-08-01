from __future__ import annotations

import re
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[3]
SHA_ACTION = re.compile(r"^[^@]+@[0-9a-f]{40}$")


def test_ci_actions_and_external_container_images_are_immutable() -> None:
    workflow = yaml.safe_load(
        (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    )
    for job in workflow["jobs"].values():
        for step in job.get("steps", []):
            action = step.get("uses")
            if action:
                assert SHA_ACTION.match(action), action
        for service in job.get("services", {}).values():
            assert "@sha256:" in service["image"], service["image"]

    for relative in ("Dockerfile", "tests/load/fake-provider/Dockerfile"):
        dockerfile = (ROOT / relative).read_text(encoding="utf-8")
        from_lines = [line for line in dockerfile.splitlines() if line.startswith("FROM ")]
        assert from_lines
        external_from_lines = [line for line in from_lines if "mcr.microsoft.com/" in line]
        assert external_from_lines
        assert all("@sha256:" in line for line in external_from_lines)
        assert "apt-get" not in dockerfile

    for relative in (
        "compose.yaml",
        "tests/load/compose.yaml",
        "tests/recovery/compose.yaml",
    ):
        compose = (ROOT / relative).read_text(encoding="utf-8")
        external = [
            line.strip()
            for line in compose.splitlines()
            if line.strip().startswith(("image: postgres:", "image: redis:"))
        ]
        assert external
        assert all("@sha256:" in line for line in external)


def test_audit_tooling_and_vulnerability_policy_are_required() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(
        encoding="utf-8"
    )
    audit_lock = (
        ROOT / "services" / "TableStructureRec" / "requirements-audit.lock"
    ).read_text(encoding="utf-8")
    assert "python -m venv .audit-venv" in workflow
    assert (
        r".\.audit-venv\Scripts\python.exe -m pip install --require-hashes "
        "-r services/TableStructureRec/requirements-audit.lock"
    ) in workflow
    assert r".\.audit-venv\Scripts\pip-audit.exe" in workflow
    assert r".\.audit-venv\Scripts\cyclonedx-py.exe" in workflow
    assert "Verify-DotnetVulnerabilities.ps1" in workflow
    assert workflow.count("scripts/Verify-PowerShellSyntax.ps1") == 2
    assert "shell: powershell" in workflow
    assert "sbom-tool -- validate" in workflow
    assert "Verify-SbomValidation.ps1" in workflow
    assert "-b artifacts/api -bc src " in workflow
    assert "-b artifacts/migrator -bc src/SnowShot.Infrastructure " in workflow
    assert "python tests/recovery/run_harness.py" in workflow
    assert "run_winsw_service_harness.ps1" in workflow
    assert "pip-audit==2.10.1" in audit_lock
    assert "cyclonedx-bom==7.3.1" in audit_lock
    assert "--hash=sha256:" in audit_lock
