"""Генерирует UnityTextTranslator.ico — прежний знак (градиент, «U», звезда). Pillow: pip install pillow."""
from __future__ import annotations

import math
import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def _blend_rgb(t: float) -> tuple[int, int, int]:
    r = int(138 + (92 - 138) * t)
    g = int(108 + (58 - 108) * t)
    b = int(255 + (210 - 255) * t)
    return r, g, b


def _star_points(cx: float, cy: float, outer: float, inner: float) -> list[tuple[float, float]]:
    pts = []
    for i in range(5):
        ao = math.radians(i * 72 - 90)
        ai = math.radians(i * 72 + 36 - 90)
        pts.append((cx + outer * math.cos(ao), cy + outer * math.sin(ao)))
        pts.append((cx + inner * math.cos(ai), cy + inner * math.sin(ai)))
    return pts


def _render(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    pad = max(2, size // 16)
    x0, y0 = pad, pad
    x1, y1 = size - pad - 1, size - pad - 1
    radius = max(size // 8, 4)

    mask = Image.new("L", (size, size), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle([x0, y0, x1, y1], radius=radius, fill=255)

    pix = img.load()
    for y in range(y0, y1 + 1):
        t = (y - y0) / max(y1 - y0, 1)
        r, g, b = _blend_rgb(t)
        for x in range(x0, x1 + 1):
            if mask.getpixel((x, y)) > 0:
                pix[x, y] = (r, g, b, 255)

    d = ImageDraw.Draw(img)
    lw = max(1, size // 48)
    d.rounded_rectangle(
        [x0, y0, x1, y1],
        radius=radius,
        outline=(210, 190, 255, 180),
        width=lw,
    )

    font = None
    windir = os.environ.get("WINDIR", r"C:\Windows")
    for fp in (
        os.path.join(windir, "Fonts", "segoeuib.ttf"),
        os.path.join(windir, "Fonts", "arialbd.ttf"),
    ):
        if os.path.isfile(fp):
            try:
                font = ImageFont.truetype(fp, max(int(size * 0.42), 10))
                break
            except OSError:
                pass
    if font is None:
        font = ImageFont.load_default()

    box_w, box_h = x1 - x0 + 1, y1 - y0 + 1
    cx = x0 + box_w / 2
    cy = y0 + box_h / 2
    d.text((cx, cy), "U", fill=(255, 255, 255, 255), font=font, anchor="mm")

    outer = max(size * 0.12, 3.5)
    inner = outer * 0.42
    sx = x1 - outer * 1.5
    sy = y0 + outer * 0.35
    star = _star_points(sx, sy, outer, inner)
    d.polygon(star, fill=(244, 196, 58, 255), outline=(255, 255, 255, 220))

    shine_w = box_w * 0.38
    shine_h = box_h * 0.22
    sx0 = x0 + box_w * 0.14
    sy0 = y0 + box_h * 0.1
    el = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ed = ImageDraw.Draw(el)
    ed.ellipse([sx0, sy0, sx0 + shine_w, sy0 + shine_h], fill=(255, 255, 255, 55))
    img = Image.alpha_composite(img, el)

    return img


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    out = root / "UnityTextTranslator.ico"
    sizes = [256, 128, 64, 48, 32, 16]
    imgs = [_render(s) for s in sizes]
    imgs[0].save(
        out,
        format="ICO",
        sizes=[(im.width, im.height) for im in imgs],
        append_images=imgs[1:],
    )
    print("Wrote", out)


if __name__ == "__main__":
    main()
