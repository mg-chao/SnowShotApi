from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping


MAX_IMAGE_SIDE = 1_500
MAX_UPLOAD_BYTES = 500 * 1024
VALID_LOG_LEVELS = frozenset({"critical", "error", "warning", "info", "debug", "trace"})
VALID_ENVIRONMENTS = frozenset({"development", "staging", "production"})


@dataclass(frozen=True)
class ServiceSettings:
    model_dir: Path
    host: str = "127.0.0.1"
    port: int = 18080
    workers: int = 3
    log_level: str = "info"
    environment: str = "development"
    watchdog_seconds: int = 55
    tls_certificate: Path | None = None
    tls_private_key: Path | None = None
    tls_client_ca: Path | None = None

    @property
    def tls_enabled(self) -> bool:
        return self.tls_certificate is not None

    @classmethod
    def from_environment(
        cls, environment: Mapping[str, str] | None = None
    ) -> "ServiceSettings":
        values = os.environ if environment is None else environment
        service_root = Path(__file__).resolve().parents[1]
        host = values.get("TABLE_REC_HOST", "127.0.0.1").strip()
        if not host:
            raise ValueError("TABLE_REC_HOST must not be empty.")

        log_level = values.get("TABLE_REC_LOG_LEVEL", "info").strip().lower()
        if log_level not in VALID_LOG_LEVELS:
            allowed = ", ".join(sorted(VALID_LOG_LEVELS))
            raise ValueError(f"TABLE_REC_LOG_LEVEL must be one of: {allowed}.")

        service_environment = values.get(
            "TABLE_REC_ENVIRONMENT", "development"
        ).strip().lower()
        if service_environment not in VALID_ENVIRONMENTS:
            allowed = ", ".join(sorted(VALID_ENVIRONMENTS))
            raise ValueError(
                f"TABLE_REC_ENVIRONMENT must be one of: {allowed}."
            )

        settings = cls(
            model_dir=Path(
                values.get("TABLE_REC_MODEL_DIR", service_root / "models")
            ).expanduser().resolve(),
            host=host,
            port=_bounded_integer(values, "TABLE_REC_PORT", 18_080, 1, 65_535),
            workers=_bounded_integer(values, "TABLE_REC_WORKERS", 3, 1, 64),
            log_level=log_level,
            environment=service_environment,
            watchdog_seconds=_bounded_integer(
                values, "TABLE_REC_WATCHDOG_SECONDS", 55, 1, 55
            ),
            tls_certificate=_optional_path(values, "TABLE_REC_TLS_CERTIFICATE"),
            tls_private_key=_optional_path(values, "TABLE_REC_TLS_PRIVATE_KEY"),
            tls_client_ca=_optional_path(values, "TABLE_REC_TLS_CLIENT_CA"),
        )
        settings._validate_tls()
        return settings

    def _validate_tls(self) -> None:
        tls_paths = (
            self.tls_certificate,
            self.tls_private_key,
            self.tls_client_ca,
        )
        if any(tls_paths) and not all(tls_paths):
            raise ValueError(
                "TABLE_REC_TLS_CERTIFICATE, TABLE_REC_TLS_PRIVATE_KEY, and "
                "TABLE_REC_TLS_CLIENT_CA must be configured together."
            )
        if self.environment != "development" and not all(tls_paths):
            raise ValueError("Non-development environments require mutual TLS configuration.")
        for path in tls_paths:
            if path is not None and not path.is_file():
                raise ValueError(f"Configured TLS file does not exist or is not a file: {path}")


def _bounded_integer(
    environment: Mapping[str, str],
    name: str,
    default: int,
    minimum: int,
    maximum: int,
) -> int:
    raw_value = environment.get(name, str(default)).strip()
    try:
        value = int(raw_value)
    except ValueError as exception:
        raise ValueError(f"{name} must be an integer; received {raw_value!r}.") from exception
    if not minimum <= value <= maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}; received {value}.")
    return value


def _optional_path(environment: Mapping[str, str], name: str) -> Path | None:
    value = environment.get(name, "").strip()
    return Path(value).expanduser().resolve() if value else None
