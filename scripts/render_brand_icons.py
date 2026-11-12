#!/usr/bin/env python3
"""Renders the UEToVS brand mark (game controller on the indigo-to-blue tile).

Writes src/UEToVS.Host/Resources/UEToVS.svg (source) and the PNGs used by the VSIX / VSCT:
UEToVS16.png, UEToVS32.png, Icon.png (128), PreviewImage.png (256). Only needs Pillow (supersampled rendering);
the geometry below is the single source of truth and is mirrored in src/UEToVS.UI/Atoms/BrandMark.cs.

Geometry (16-unit grid, pixel-aligned so the 16 px icon is crisp):
  tile    rounded square 0.5..15.5, radius 3.5, gradient #6B4FE8 -> #1F7AE0 (top-left -> bottom-right)
  body    rounded rect x 2..14, y 4..10, radius 2, plus two grips x 2..6 / 10..14, y 8..12, radius 2 (white)
  d-pad   cut-out plus: x 3..6 * y 6..7 and x 4..5 * y 5..8
  buttons four cut-out face buttons around (11.5, 6.5): squares at 16 px, round dots from 32 px up

Usage: python3 scripts/render_brand_icons.py
"""
import os
import sys

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "UEToVS.Host", "Resources")

TILE = (0.5, 0.5, 15.5, 15.5)
TILE_R = 3.5
C0 = (0x6B, 0x4F, 0xE8)
C1 = (0x1F, 0x7A, 0xE0)
BODY = [((2, 4, 14, 10), 2.0), ((2, 8, 6, 12), 2.0), ((10, 8, 14, 12), 2.0)]
DPAD = [(3, 6, 6, 7), (4, 5, 5, 8)]
BUTTON_CENTERS = [(11.5, 5.5), (10.5, 6.5), (12.5, 6.5), (11.5, 7.5)]
BUTTON_R = 0.58

SS = 16  # supersampling factor per output pixel


def gradient(size):
    """Diagonal gradient C0 (top-left) -> C1 (bottom-right)."""
    if size > 512:
        return gradient(512).resize((size, size), Image.BILINEAR)
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2.0 * (size - 1)) if size > 1 else 0
            px[x, y] = tuple(round(C0[i] + (C1[i] - C0[i]) * t) for i in range(3))
    return img


def render(size, square_buttons):
    big = size * SS
    k = big / 16.0

    def box(r):
        return [r[0] * k, r[1] * k, r[2] * k - 1, r[3] * k - 1]

    tile_mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle(box(TILE), radius=TILE_R * k, fill=255)

    glyph = Image.new("L", (big, big), 0)
    g = ImageDraw.Draw(glyph)
    for rect, radius in BODY:
        g.rounded_rectangle(box(rect), radius=radius * k, fill=255)
    for rect in DPAD:
        g.rounded_rectangle(box(rect), radius=0 if square_buttons else 0.18 * k, fill=0)
    for cx, cy in BUTTON_CENTERS:
        if square_buttons:
            g.rectangle(box((cx - 0.5, cy - 0.5, cx + 0.5, cy + 0.5)), fill=0)
        else:
            g.ellipse([(cx - BUTTON_R) * k, (cy - BUTTON_R) * k, (cx + BUTTON_R) * k - 1, (cy + BUTTON_R) * k - 1], fill=0)

    tile = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    tile.paste(gradient(big), (0, 0), tile_mask)
    white = Image.new("RGBA", (big, big), (255, 255, 255, 255))
    tile.paste(white, (0, 0), glyph)
    # keep the tile's own alpha (the glyph never leaves the tile)
    r, gch, b, _ = tile.split()
    tile = Image.merge("RGBA", (r, gch, b, tile_mask))
    return tile.resize((size, size), Image.BOX)  # area average: exact for the pixel-aligned shapes


def svg():
    def rr(rect, radius, fill):
        x0, y0, x1, y1 = rect
        return f'    <rect x="{x0}" y="{y0}" width="{x1 - x0}" height="{y1 - y0}" rx="{radius}" fill="{fill}"/>'

    body = "\n".join(rr(r, rad, "#FFFFFF") for r, rad in BODY)
    holes = "\n".join(rr(r, 0.18, "#000000") for r in DPAD)
    dots = "\n".join(f'    <circle cx="{cx}" cy="{cy}" r="{BUTTON_R}" fill="#000000"/>' for cx, cy in BUTTON_CENTERS)
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!--
  UEToVS brand mark: a game controller (rounded body with grips, d-pad on the left, four face buttons on the right)
  on the indigo-to-blue rounded tile. Source of Resources/UEToVS16.png, UEToVS32.png, Icon.png (128) and
  PreviewImage.png (256), all rendered by scripts/render_brand_icons.py (the geometry lives there and in
  src/UEToVS.UI/Atoms/BrandMark.cs). 16-unit grid, pixel-aligned so the 16 px icon stays crisp.
-->
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 16 16">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#6B4FE8"/>
      <stop offset="1" stop-color="#1F7AE0"/>
    </linearGradient>
    <mask id="pad" maskUnits="userSpaceOnUse" x="0" y="0" width="16" height="16">
{body}
{holes}
{dots}
    </mask>
  </defs>
  <rect x="0.5" y="0.5" width="15" height="15" rx="3.5" ry="3.5" fill="url(#g)"/>
  <rect x="0" y="0" width="16" height="16" fill="#FFFFFF" mask="url(#pad)"/>
</svg>
"""


def main():
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "UEToVS.svg"), "w", encoding="utf-8", newline="\n") as f:
        f.write(svg())
    for name, size in [("UEToVS16.png", 16), ("UEToVS32.png", 32), ("Icon.png", 128), ("PreviewImage.png", 256)]:
        render(size, square_buttons=size <= 20).save(os.path.join(OUT, name), optimize=True)
        print("wrote", name, size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
