"""Generates src/LiveSlides.App/Assets/LiveSlides.ico (and a PNG preview).

Same family as LiveCue's and LiveCount's icons (same dark tile, palette and proportions): two
stacked slides, the front one carrying LiveCue's green GO "play" triangle, and under them a row
of three sequence marks - dim, amber (the one on air, like LiveCue's current cue), dim - for
"many presentations played as one". Small sizes (<= 32 px) use a simplified drawing so it stays
readable in the taskbar and title bar.

Usage: python tools/make_icon.py   (needs Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT_ICO = ROOT / "src" / "LiveSlides.App" / "Assets" / "LiveSlides.ico"
OUT_PNG = ROOT / "src" / "LiveSlides.App" / "Assets" / "LiveSlides.png"

SUPERSAMPLE = 1024  # drawn at this size, then downscaled with Lanczos for smooth edges

# Palette shared with LiveCue and LiveCount.
TILE_TOP = (52, 57, 66)
TILE_BOTTOM = (22, 24, 28)
DIM = (98, 106, 122)
AMBER = (255, 179, 0)
GO_TOP = (102, 222, 128)
GO_BOTTOM = (34, 160, 70)
LIGHT = (235, 238, 242)  # LiveCount's stopwatch hand: the front slide


def vertical_gradient(size, top, bottom):
    gradient = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / (size - 1)
        gradient.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return gradient.resize((size, size))


def rounded_mask(size, box, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    return mask


def triangle_mask(size, points):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return mask


def draw(simplified: bool) -> Image.Image:
    s = SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Tile: identical to LiveCue's.
    margin = s * 0.03
    tile_box = (margin, margin, s - margin, s - margin)
    img.paste(vertical_gradient(s, TILE_TOP, TILE_BOTTOM), (0, 0), rounded_mask(s, tile_box, s * 0.2))
    d = ImageDraw.Draw(img)

    def box(x0, y0, x1, y1):
        return (s * x0, s * y0, s * x1, s * y1)

    if simplified:
        back = box(0.34, 0.14, 0.88, 0.50)
        front = box(0.12, 0.28, 0.74, 0.70)
        tri = [(0.34, 0.37), (0.34, 0.61), (0.56, 0.49)]
        marks = [((0.12, 0.79, 0.74, 0.89), AMBER)]
        corner = 0.045
    else:
        back = box(0.30, 0.15, 0.86, 0.50)
        front = box(0.14, 0.27, 0.72, 0.64)
        tri = [(0.365, 0.355), (0.365, 0.555), (0.535, 0.455)]
        marks = [((0.14, 0.75, 0.31, 0.82), DIM), ((0.345, 0.75, 0.515, 0.82), AMBER), ((0.55, 0.75, 0.72, 0.82), DIM)]
        corner = 0.035

    d.rounded_rectangle(back, radius=s * corner, fill=DIM)
    # A thin dark outline separates the front slide from the one behind it.
    outline = s * 0.018
    d.rounded_rectangle((front[0] - outline, front[1] - outline, front[2] + outline, front[3] + outline),
                        radius=s * (corner + 0.018), fill=TILE_BOTTOM)
    d.rounded_rectangle(front, radius=s * corner, fill=LIGHT)

    points = [(s * x, s * y) for x, y in tri]
    img.paste(vertical_gradient(s, GO_TOP, GO_BOTTOM), (0, 0), triangle_mask(s, points))

    for (x0, y0, x1, y1), color in marks:
        d.rounded_rectangle(box(x0, y0, x1, y1), radius=s * (y1 - y0) / 2, fill=color)
    return img


def main():
    full = draw(simplified=False)
    small = draw(simplified=True)

    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [(small if size <= 32 else full).resize((size, size), Image.LANCZOS) for size in sizes]

    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    frames[-1].save(OUT_ICO, format="ICO", sizes=[(f.width, f.height) for f in frames], append_images=frames[:-1])
    full.resize((512, 512), Image.LANCZOS).save(OUT_PNG)
    print(f"Wrote {OUT_ICO} and {OUT_PNG}")


if __name__ == "__main__":
    main()
