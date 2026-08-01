from __future__ import annotations

from io import BytesIO

import numpy as np
from PIL import Image, UnidentifiedImageError

from .config import MAX_IMAGE_SIDE
from .errors import IMAGE_TOO_LARGE, INVALID_IMAGE, NOT_WEBP, ServiceError


def decode_webp(payload: bytes) -> np.ndarray:
    has_webp_container = (
        len(payload) >= 12
        and payload[:4] == b"RIFF"
        and payload[8:12] == b"WEBP"
    )

    try:
        with Image.open(BytesIO(payload)) as image:
            if image.format != "WEBP":
                raise NOT_WEBP.exception()

            width, height = image.size
            if width <= 0 or height <= 0:
                raise INVALID_IMAGE.exception()
            if width > MAX_IMAGE_SIDE or height > MAX_IMAGE_SIDE:
                raise IMAGE_TOO_LARGE.exception()
            if getattr(image, "n_frames", 1) != 1:
                raise INVALID_IMAGE.exception()

            image.load()
            rgb_image = image.convert("RGB")
            return np.asarray(rgb_image)[:, :, ::-1].copy()
    except ServiceError:
        raise
    except (UnidentifiedImageError, OSError, SyntaxError, ValueError) as exception:
        definition = INVALID_IMAGE if has_webp_container else NOT_WEBP
        raise definition.exception() from exception
