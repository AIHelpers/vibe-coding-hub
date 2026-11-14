#!/usr/bin/env python3
"""Generates every application icon from one drawing.

    python3 packaging/icons/make-icons.py            # writes next to this script
    python3 packaging/icons/make-icons.py --master my-artwork.png

Needs Pillow (`pip install pillow`). Works on any OS, so `.icns` does not require a Mac.

Outputs (all committed, so a normal build needs neither Python nor Pillow):
    icon-1024.png          master artwork
    app.ico                Windows exe and installer (16-256 px)
    app.icns               macOS bundle
    vibe-coding-hub.png    256 px, Linux desktop entry and the Avalonia window icon
    vibe-coding-hub-512.png  512 px, Linux icon theme / AppImage

The default drawing is an original placeholder: a hub node linked to five agents. To use real brand artwork,
pass it with --master (square PNG, at least 1024 px); every other file is derived from it.
"""
from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw

MASTER_SIZE = 1024
SUPERSAMPLE = 4  # draw at 4x and downscale for smooth edges

BG_TOP = (79, 70, 229, 255)      # indigo
BG_BOTTOM = (30, 27, 75, 255)    # deep indigo
LINK = (255, 255, 255, 140)
NODE = (255, 255, 255, 255)
ACCENT = (34, 211, 238, 255)     # cyan

ICO_SIZES = [256, 128, 64, 48, 32, 16]


def draw_master() -> Image.Image:
    size = MASTER_SIZE * SUPERSAMPLE
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # Rounded-square background with a vertical gradient.
    gradient_mask = Image.linear_gradient("L").resize((size, size))
    background = Image.composite(
        Image.new("RGBA", (size, size), BG_BOTTOM),
        Image.new("RGBA", (size, size), BG_TOP),
        gradient_mask,
    )
    margin = int(size * 0.04)
    shape = Image.new("L", (size, size), 0)
    ImageDraw.Draw(shape).rounded_rectangle(
        (margin, margin, size - margin, size - margin), radius=int(size * 0.22), fill=255)
    canvas.paste(background, (0, 0), shape)

    centre = size / 2
    orbit = size * 0.29
    satellites = [
        (centre + orbit * math.cos(math.radians(-90 + 72 * i)),
         centre + orbit * math.sin(math.radians(-90 + 72 * i)))
        for i in range(5)
    ]

    # Links first (translucent), so the nodes sit on top.
    links = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    link_draw = ImageDraw.Draw(links)
    for x, y in satellites:
        link_draw.line((centre, centre, x, y), fill=LINK, width=int(size * 0.022))
    canvas.alpha_composite(links)

    draw = ImageDraw.Draw(canvas)
    hub_r = size * 0.115
    draw.ellipse((centre - hub_r, centre - hub_r, centre + hub_r, centre + hub_r), fill=NODE)
    core_r = hub_r * 0.42
    draw.ellipse((centre - core_r, centre - core_r, centre + core_r, centre + core_r), fill=BG_BOTTOM)

    node_r = size * 0.06
    for index, (x, y) in enumerate(satellites):
        draw.ellipse((x - node_r, y - node_r, x + node_r, y + node_r), fill=ACCENT if index == 0 else NODE)

    return canvas.resize((MASTER_SIZE, MASTER_SIZE), Image.LANCZOS)


def load_master(path: Path) -> Image.Image:
    image = Image.open(path).convert("RGBA")
    if image.width != image.height or image.width < MASTER_SIZE:
        sys.exit(f"{path}: the master must be square and at least {MASTER_SIZE}x{MASTER_SIZE}, got {image.size}")
    return image.resize((MASTER_SIZE, MASTER_SIZE), Image.LANCZOS)


def write_all(master: Image.Image, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)

    def scaled(px: int) -> Image.Image:
        return master.resize((px, px), Image.LANCZOS)

    master.save(out / "icon-1024.png", optimize=True)
    scaled(512).save(out / "vibe-coding-hub-512.png", optimize=True)
    scaled(256).save(out / "vibe-coding-hub.png", optimize=True)
    scaled(256).save(out / "app.ico", sizes=[(s, s) for s in ICO_SIZES])
    master.save(out / "app.icns")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--master", type=Path, help="use this square PNG instead of the built-in drawing")
    parser.add_argument("--out", type=Path, default=Path(__file__).resolve().parent, help="output folder")
    args = parser.parse_args()

    master = load_master(args.master) if args.master else draw_master()
    write_all(master, args.out)
    print(f"Icons written to {args.out}")


if __name__ == "__main__":
    main()
