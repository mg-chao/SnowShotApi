from __future__ import annotations

import ssl
import logging
import os

from .config import ServiceSettings


def main() -> None:
    settings = ServiceSettings.from_environment()
    # Set process-wide pool limits before importing NumPy or Paddle in the app.
    for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
        os.environ[name] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"

    import uvicorn
    logging.basicConfig(
        level="DEBUG" if settings.log_level == "trace" else settings.log_level.upper(),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    uvicorn.run("latex_rec_service.app:app", **uvicorn_options(settings))


def uvicorn_options(settings: ServiceSettings) -> dict[str, object]:
    return {
        "host": settings.host,
        "port": settings.port,
        "workers": settings.workers,
        "log_level": settings.log_level,
        "access_log": False,
        "timeout_graceful_shutdown": 55,
        "ssl_certfile": str(settings.tls_certificate) if settings.tls_enabled else None,
        "ssl_keyfile": str(settings.tls_private_key) if settings.tls_enabled else None,
        "ssl_ca_certs": str(settings.tls_client_ca) if settings.tls_enabled else None,
        "ssl_cert_reqs": ssl.CERT_REQUIRED if settings.tls_enabled else ssl.CERT_NONE,
    }


if __name__ == "__main__":
    main()
