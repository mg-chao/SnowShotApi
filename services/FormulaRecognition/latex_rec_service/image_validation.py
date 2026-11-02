from __future__ import annotations

from io import BytesIO
import warnings

import numpy as np
from PIL import Image, ImageOps, UnidentifiedImageError

from .config import MAX_IMAGE_HEIGHT, MAX_IMAGE_WIDTH
from .errors import IMAGE_TOO_LARGE, INVALID_IMAGE, UNSUPPORTED_IMAGE_FORMAT, ServiceError


IMAGE_FORMATS = {"image/webp": "WEBP", "image/jpeg": "JPEG", "image/png": "PNG"}


def decode_image(payload: bytes, media_type: str) -> np.ndarray:
    expected_format = IMAGE_FORMATS.get(media_type)
    if expected_format is None:
        raise UNSUPPORTED_IMAGE_FORMAT.exception()

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            image = Image.open(BytesIO(payload))
        with image:
            if image.format != expected_format:
                raise UNSUPPORTED_IMAGE_FORMAT.exception()

            width, height = image.size
            if width <= 0 or height <= 0:
                raise INVALID_IMAGE.exception()
            if width > MAX_IMAGE_WIDTH or height > MAX_IMAGE_HEIGHT:
                raise IMAGE_TOO_LARGE.exception()
            if getattr(image, "n_frames", 1) != 1:
                raise INVALID_IMAGE.exception()

            image.load()
            oriented = ImageOps.exif_transpose(image)
            if oriented.mode == "RGB":
                return np.asarray(oriented).copy()
            rgba = oriented.convert("RGBA")
            background = Image.new("RGBA", rgba.size, "white")
            rgb_image = Image.alpha_composite(background, rgba).convert("RGB")
            return np.asarray(rgb_image).copy()
    except ServiceError:
        raise
    except (Image.DecompressionBombError, Image.DecompressionBombWarning) as exception:
        raise IMAGE_TOO_LARGE.exception() from exception
    except (UnidentifiedImageError, OSError, SyntaxError, ValueError) as exception:
        raise INVALID_IMAGE.exception() from exception


def decode_webp(payload: bytes) -> np.ndarray:
    return decode_image(payload, "image/webp")
