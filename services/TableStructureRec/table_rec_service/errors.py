from __future__ import annotations

from dataclasses import dataclass


class ServiceError(Exception):
    def __init__(
        self,
        status_code: int,
        code: str,
        public_message: str,
        category: str,
    ) -> None:
        super().__init__(code)
        self.status_code = status_code
        self.code = code
        self.public_message = public_message
        self.category = category


@dataclass(frozen=True)
class ErrorDefinition:
    status_code: int
    code: str
    public_message: str
    category: str

    def exception(self) -> ServiceError:
        return ServiceError(
            self.status_code,
            self.code,
            self.public_message,
            self.category,
        )


NOT_WEBP = ErrorDefinition(
    415,
    "not_webp",
    "The image payload must be WebP.",
    "validation_format",
)
INVALID_IMAGE = ErrorDefinition(
    422,
    "invalid_image",
    "The WebP image is invalid.",
    "validation_image",
)
IMAGE_TOO_LARGE = ErrorDefinition(
    422,
    "image_too_large",
    "The image width and height may not exceed 2880 pixels.",
    "validation_dimensions",
)
PAYLOAD_TOO_LARGE = ErrorDefinition(
    413,
    "payload_too_large",
    "The image payload may not exceed 800 KiB (819200 bytes).",
    "validation_payload_size",
)
NO_TABLE = ErrorDefinition(
    422,
    "no_table",
    "No extractable table was produced.",
    "empty_result",
)
INFERENCE_FAILED = ErrorDefinition(
    500,
    "inference_failed",
    "Table inference failed.",
    "inference_failure",
)
WORKER_BUSY = ErrorDefinition(
    503,
    "worker_busy",
    "The table inference worker is busy.",
    "inference_busy",
)
WORKER_UNAVAILABLE = ErrorDefinition(
    503,
    "worker_unavailable",
    "The table inference worker is not ready.",
    "inference_unavailable",
)
