from __future__ import annotations

from pathlib import Path
import ssl

import pytest

from latex_rec_service.config import MAX_UPLOAD_BYTES, ServiceSettings
from latex_rec_service.__main__ import uvicorn_options


def test_defaults_are_local_and_one_process() -> None:
    settings = ServiceSettings.from_environment({})

    assert MAX_UPLOAD_BYTES == 800 * 1024
    assert settings.host == "127.0.0.1"
    assert settings.port == 18081
    assert settings.workers == 1
    assert settings.log_level == "info"
    assert settings.watchdog_seconds == 55
    assert not settings.tls_enabled


def test_valid_environment_and_tls_files(tmp_path: Path) -> None:
    certificate = tmp_path / "server.pem"
    private_key = tmp_path / "server-key.pem"
    client_ca = tmp_path / "client-ca.pem"
    for path in (certificate, private_key, client_ca):
        path.write_text("test", encoding="ascii")

    settings = ServiceSettings.from_environment(
        {
            "LATEX_REC_HOST": "100.64.0.2",
            "LATEX_REC_PORT": "18443",
            "LATEX_REC_WORKERS": "2",
            "LATEX_REC_LOG_LEVEL": "WARNING",
            "LATEX_REC_TLS_CERTIFICATE": str(certificate),
            "LATEX_REC_TLS_PRIVATE_KEY": str(private_key),
            "LATEX_REC_TLS_CLIENT_CA": str(client_ca),
        }
    )

    assert settings.port == 18443
    assert settings.workers == 2
    assert settings.log_level == "warning"
    assert settings.tls_enabled


@pytest.mark.parametrize(
    ("name", "value"),
    [
        ("LATEX_REC_HOST", ""),
        ("LATEX_REC_PORT", "not-a-number"),
        ("LATEX_REC_PORT", "0"),
        ("LATEX_REC_PORT", "65536"),
        ("LATEX_REC_WORKERS", "0"),
        ("LATEX_REC_LOG_LEVEL", "verbose"),
        ("LATEX_REC_ENVIRONMENT", "unknown"),
    ],
)
def test_invalid_scalar_settings_fail_with_variable_name(name: str, value: str) -> None:
    with pytest.raises(ValueError, match=name):
        ServiceSettings.from_environment({name: value})


def test_partial_tls_configuration_is_rejected(tmp_path: Path) -> None:
    certificate = tmp_path / "server.pem"
    certificate.write_text("test", encoding="ascii")

    with pytest.raises(ValueError, match="must be configured together"):
        ServiceSettings.from_environment(
            {"LATEX_REC_TLS_CERTIFICATE": str(certificate)}
        )


def test_missing_tls_file_is_rejected(tmp_path: Path) -> None:
    missing = tmp_path / "missing.pem"

    with pytest.raises(ValueError, match="does not exist"):
        ServiceSettings.from_environment(
            {
                "LATEX_REC_TLS_CERTIFICATE": str(missing),
                "LATEX_REC_TLS_PRIVATE_KEY": str(missing),
                "LATEX_REC_TLS_CLIENT_CA": str(missing),
            }
        )


def test_production_requires_mutual_tls() -> None:
    with pytest.raises(ValueError, match="mutual TLS"):
        ServiceSettings.from_environment({"LATEX_REC_ENVIRONMENT": "production"})


def test_staging_requires_mutual_tls() -> None:
    with pytest.raises(ValueError, match="mutual TLS"):
        ServiceSettings.from_environment({"LATEX_REC_ENVIRONMENT": "staging"})


def test_secure_environment_allows_configured_workers_and_requires_client_certificates(
    tmp_path: Path,
) -> None:
    certificate = tmp_path / "server.pem"
    private_key = tmp_path / "server-key.pem"
    client_ca = tmp_path / "client-ca.pem"
    for path in (certificate, private_key, client_ca):
        path.write_text("test", encoding="ascii")
    environment = {
        "LATEX_REC_ENVIRONMENT": "production",
        "LATEX_REC_TLS_CERTIFICATE": str(certificate),
        "LATEX_REC_TLS_PRIVATE_KEY": str(private_key),
        "LATEX_REC_TLS_CLIENT_CA": str(client_ca),
    }

    configured = ServiceSettings.from_environment(
        {**environment, "LATEX_REC_WORKERS": "2"}
    )
    assert configured.workers == 2

    settings = ServiceSettings.from_environment(environment)
    assert settings.workers == 1
    options = uvicorn_options(settings)
    assert options["ssl_certfile"] == str(certificate)
    assert options["ssl_keyfile"] == str(private_key)
    assert options["ssl_ca_certs"] == str(client_ca)
    assert options["ssl_cert_reqs"] == ssl.CERT_REQUIRED
