from __future__ import annotations

import ssl

from .config import ServiceSettings


def main() -> None:
    import uvicorn

    settings = ServiceSettings.from_environment()
    uvicorn.run("table_rec_service.app:app", **uvicorn_options(settings))


def uvicorn_options(settings: ServiceSettings) -> dict[str, object]:
    return {
        "host": settings.host,
        "port": settings.port,
        "workers": settings.workers,
        "log_level": settings.log_level,
        "access_log": False,
        "ssl_certfile": str(settings.tls_certificate) if settings.tls_enabled else None,
        "ssl_keyfile": str(settings.tls_private_key) if settings.tls_enabled else None,
        "ssl_ca_certs": str(settings.tls_client_ca) if settings.tls_enabled else None,
        "ssl_cert_reqs": ssl.CERT_REQUIRED if settings.tls_enabled else ssl.CERT_NONE,
    }


if __name__ == "__main__":
    main()
