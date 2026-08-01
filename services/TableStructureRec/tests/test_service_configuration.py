from __future__ import annotations

import shutil
import subprocess
import xml.etree.ElementTree as element_tree
from pathlib import Path

import pytest


SERVICE_ROOT = Path(__file__).resolve().parents[1]
RENDERER = SERVICE_ROOT / "scripts" / "Render-ServiceConfiguration.ps1"
TEMPLATE = SERVICE_ROOT / "deployment" / "TableStructureRecService.xml"


def _powershell() -> str:
    executable = shutil.which("powershell") or shutil.which("pwsh")
    if executable is None:
        pytest.skip("PowerShell is required to verify the WinSW renderer")
    return executable


def test_renderer_creates_parameterized_production_mtls_configuration(
    tmp_path: Path,
) -> None:
    certificate = tmp_path / "worker.pem"
    private_key = tmp_path / "worker-key.pem"
    client_ca = tmp_path / "client-ca.pem"
    for path in (certificate, private_key, client_ca):
        path.write_text("test", encoding="ascii")
    output = tmp_path / "service.xml"

    subprocess.run(
        [
            _powershell(),
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(RENDERER),
            "-TemplatePath",
            str(TEMPLATE),
            "-OutputPath",
            str(output),
            "-ListenHost",
            "100.64.0.25",
            "-ServiceEnvironment",
            "production",
            "-TlsCertificate",
            str(certificate),
            "-TlsPrivateKey",
            str(private_key),
            "-TlsClientCa",
            str(client_ca),
        ],
        check=True,
        capture_output=True,
        text=True,
    )

    values = {
        node.attrib["name"]: node.attrib["value"]
        for node in element_tree.parse(output).findall("env")
    }
    assert values["TABLE_REC_HOST"] == "100.64.0.25"
    assert values["TABLE_REC_ENVIRONMENT"] == "production"
    assert Path(values["TABLE_REC_TLS_CERTIFICATE"]) == certificate
    assert Path(values["TABLE_REC_TLS_PRIVATE_KEY"]) == private_key
    assert Path(values["TABLE_REC_TLS_CLIENT_CA"]) == client_ca
    account_domain = element_tree.parse(output).find("serviceaccount/domain")
    account_user = element_tree.parse(output).find("serviceaccount/user")
    assert account_domain is not None
    assert account_user is not None
    assert account_domain.text == "NT AUTHORITY"
    assert account_user.text == "LocalService"


def test_renderer_rejects_production_without_complete_mtls(tmp_path: Path) -> None:
    result = subprocess.run(
        [
            _powershell(),
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(RENDERER),
            "-TemplatePath",
            str(TEMPLATE),
            "-OutputPath",
            str(tmp_path / "service.xml"),
            "-ListenHost",
            "100.64.0.25",
            "-ServiceEnvironment",
            "production",
        ],
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode != 0
    assert "require mutual TLS files" in (result.stdout + result.stderr)


def test_renderer_rejects_local_system_for_production(tmp_path: Path) -> None:
    certificate = tmp_path / "worker.pem"
    private_key = tmp_path / "worker-key.pem"
    client_ca = tmp_path / "client-ca.pem"
    for path in (certificate, private_key, client_ca):
        path.write_text("test", encoding="ascii")

    result = subprocess.run(
        [
            _powershell(),
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(RENDERER),
            "-TemplatePath",
            str(TEMPLATE),
            "-OutputPath",
            str(tmp_path / "service.xml"),
            "-ListenHost",
            "100.64.0.25",
            "-ServiceEnvironment",
            "production",
            "-ServiceAccount",
            "LocalSystem",
            "-TlsCertificate",
            str(certificate),
            "-TlsPrivateKey",
            str(private_key),
            "-TlsClientCa",
            str(client_ca),
        ],
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode != 0
    assert "must not run as LocalSystem" in (result.stdout + result.stderr)
