from __future__ import annotations

import io

import numpy as np
import pytest
from PIL import Image

from table_rec_service.image_validation import decode_image
from .test_api import FakeBundle, client_for, error_code, image_bytes, post_image


@pytest.mark.parametrize(("image_format", "media_type"), [
    ("JPEG", "image/jpeg"), ("PNG", "image/png"), ("WEBP", "image/webp"),
])
def test_supported_formats_reach_inference(image_format: str, media_type: str) -> None:
    with client_for(FakeBundle()) as client:
        result = post_image(client, image_bytes(image_format), headers={"Content-Type": media_type})
    assert result.status_code == 200
    assert "<table>" in result.json()["html"]


@pytest.mark.parametrize(("image_format", "media_type"), [("JPEG", "image/jpeg"), ("PNG", "image/png")])
def test_new_formats_keep_dimension_limit(image_format: str, media_type: str) -> None:
    with client_for(FakeBundle()) as client:
        exact = post_image(client, image_bytes(image_format, (2880, 1)), headers={"Content-Type": media_type})
        oversized = post_image(client, image_bytes(image_format, (1, 2881)), headers={"Content-Type": media_type})
    assert exact.status_code == 200
    assert oversized.status_code == 422
    assert error_code(oversized) == "image_too_large"


@pytest.mark.parametrize(("payload", "media_type"), [(b"\xff\xd8\xffbroken", "image/jpeg"), (b"\x89PNG\r\n\x1a\nbroken", "image/png")])
def test_corrupt_supported_images_are_invalid(payload: bytes, media_type: str) -> None:
    with client_for(FakeBundle()) as client:
        result = post_image(client, payload, headers={"Content-Type": media_type})
    assert result.status_code == 422
    assert error_code(result) == "invalid_image"


def test_exif_orientation_is_applied_before_inference() -> None:
    image = Image.new("RGB", (12, 8), "white")
    exif = image.getexif()
    exif[274] = 6
    output = io.BytesIO()
    image.save(output, format="JPEG", exif=exif)
    assert decode_image(output.getvalue(), "image/jpeg").shape == (12, 8, 3)


def test_transparent_png_uses_white_background() -> None:
    output = io.BytesIO()
    Image.new("RGBA", (2, 2), (0, 0, 0, 0)).save(output, format="PNG")
    assert np.all(decode_image(output.getvalue(), "image/png") == 255)


def test_animated_png_and_unsupported_formats_are_rejected() -> None:
    animated = io.BytesIO()
    first = Image.new("RGB", (8, 8), "white")
    first.save(animated, format="PNG", save_all=True, append_images=[Image.new("RGB", (8, 8), "black")])
    with client_for(FakeBundle()) as client:
        apng = post_image(client, animated.getvalue(), headers={"Content-Type": "image/png"})
        gif = post_image(client, image_bytes("GIF"), headers={"Content-Type": "image/png"})
    assert apng.status_code == 422 and error_code(apng) == "invalid_image"
    assert gif.status_code == 415 and error_code(gif) == "unsupported_image_format"
