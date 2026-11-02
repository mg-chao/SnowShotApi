from __future__ import annotations

import io

import numpy as np
import pytest
from PIL import Image

from latex_rec_service.image_validation import decode_image, decode_webp
from .test_api import FakeBundle, client_for, error_code, image_bytes, post_image


FORMATS = [("JPEG", "image/jpeg"), ("PNG", "image/png"), ("WEBP", "image/webp")]


@pytest.mark.parametrize(("image_format", "media_type"), FORMATS)
def test_supported_formats_reach_inference(image_format: str, media_type: str) -> None:
    latex = " \\frac{x}{y}\n\\alpha + \\beta "
    with client_for(FakeBundle(result_latex=latex)) as client:
        response = post_image(client, image_bytes(image_format), headers={"Content-Type": media_type})
    assert response.status_code == 200
    assert response.json()["latex"] == latex


@pytest.mark.parametrize(("image_format", "media_type"), FORMATS)
def test_dimension_and_payload_limits(image_format: str, media_type: str, monkeypatch) -> None:
    import latex_rec_service.app as service_app
    payload = image_bytes(image_format, (2880, 1))
    with client_for(FakeBundle()) as client:
        exact = post_image(client, payload, headers={"Content-Type": media_type})
        oversized = post_image(client, image_bytes(image_format, (1, 2881)), headers={"Content-Type": media_type})
        monkeypatch.setattr(service_app, "MAX_UPLOAD_BYTES", len(payload))
        exact_bytes = post_image(client, payload, headers={"Content-Type": media_type})
        over_bytes = post_image(client, payload + b"x", headers={"Content-Type": media_type})
    assert exact.status_code == exact_bytes.status_code == 200
    assert oversized.status_code == 422 and error_code(oversized) == "image_too_large"
    assert over_bytes.status_code == 413 and error_code(over_bytes) == "payload_too_large"


@pytest.mark.parametrize(("payload", "media_type"), [
    (b"\xff\xd8\xffbroken", "image/jpeg"),
    (b"\x89PNG\r\n\x1a\nbroken", "image/png"),
    (b"RIFF0000WEBPbroken", "image/webp"),
])
def test_corrupt_images_are_invalid(payload: bytes, media_type: str) -> None:
    with client_for(FakeBundle()) as client:
        response = post_image(client, payload, headers={"Content-Type": media_type})
    assert response.status_code == 422 and error_code(response) == "invalid_image"


@pytest.mark.parametrize(("image_format", "media_type"), FORMATS)
def test_mismatched_media_types_are_rejected(image_format: str, media_type: str) -> None:
    mismatched = "image/png" if media_type != "image/png" else "image/jpeg"
    with client_for(FakeBundle()) as client:
        response = post_image(client, image_bytes(image_format), headers={"Content-Type": mismatched})
    assert response.status_code == 415 and error_code(response) == "unsupported_image_format"


def test_exif_orientation_and_rgb_channels() -> None:
    image = Image.new("RGB", (12, 8), (210, 40, 10))
    exif = image.getexif()
    exif[274] = 6
    output = io.BytesIO()
    image.save(output, format="JPEG", exif=exif, quality=100)
    decoded = decode_image(output.getvalue(), "image/jpeg")
    assert decoded.shape == (12, 8, 3)
    assert decoded[0, 0, 0] > decoded[0, 0, 1] > decoded[0, 0, 2]


def test_transparent_png_is_white_and_webp_wrapper_is_compatible() -> None:
    output = io.BytesIO()
    Image.new("RGBA", (2, 2), (0, 0, 0, 0)).save(output, format="PNG")
    assert np.all(decode_image(output.getvalue(), "image/png") == 255)
    webp = image_bytes()
    assert np.array_equal(decode_webp(webp), decode_image(webp, "image/webp"))


@pytest.mark.parametrize(("image_format", "media_type"), [("PNG", "image/png"), ("WEBP", "image/webp")])
def test_animated_images_are_rejected(image_format: str, media_type: str) -> None:
    output = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(output, format=image_format, save_all=True,
                                        append_images=[Image.new("RGB", (8, 8), "black")])
    with client_for(FakeBundle()) as client:
        response = post_image(client, output.getvalue(), headers={"Content-Type": media_type})
    assert response.status_code == 422 and error_code(response) == "invalid_image"


def test_unsupported_image_is_rejected_even_with_supported_media_type() -> None:
    with client_for(FakeBundle()) as client:
        response = post_image(client, image_bytes("GIF"), headers={"Content-Type": "image/png"})
    assert response.status_code == 415 and error_code(response) == "unsupported_image_format"
