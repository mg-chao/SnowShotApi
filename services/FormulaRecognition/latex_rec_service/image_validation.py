from __future__ import annotations

from io import BytesIO
import warnings

import numpy as np
from PIL import Image, UnidentifiedImageError

from .config import MAX_IMAGE_HEIGHT, MAX_IMAGE_WIDTH
from .errors import IMAGE_TOO_LARGE, INVALID_IMAGE, NOT_WEBP, ServiceError


def decode_webp(payload: bytes) -> np.ndarray:
    has_webp_container = (
        len(payload) >= 12
        and payload[:4] == b"RIFF"
        and payload[8:12] == b"WEBP"
    )

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            image = Image.open(BytesIO(payload))
        with image:
            if image.format != "WEBP":
                raise NOT_WEBP.exception()

            width, height = image.size
            if width <= 0 or height <= 0:
                raise INVALID_IMAGE.exception()
            if width > MAX_IMAGE_WIDTH or height > MAX_IMAGE_HEIGHT:
                raise IMAGE_TOO_LARGE.exception()
            if getattr(image, "n_frames", 1) != 1:
                raise INVALID_IMAGE.exception()

            image.load()
            if image.mode == "RGB":
                return np.asarray(image).copy()
            rgba = image.convert("RGBA")
            background = Image.new("RGBA", image.size, "white")
            rgb_image = Image.alpha_composite(background, rgba).convert("RGB")
            return np.asarray(rgb_image).copy()
    except ServiceError:
        raise
    except (Image.DecompressionBombError, Image.DecompressionBombWarning) as exception:
        raise IMAGE_TOO_LARGE.exception() from exception
    except (UnidentifiedImageError, OSError, SyntaxError, ValueError) as exception:
        definition = INVALID_IMAGE if has_webp_container else NOT_WEBP
        raise definition.exception() from exception
