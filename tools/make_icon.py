"""Generates src/LiveSlides.App/Assets/LiveSlides.ico and .png (needs Pillow).

The icon: a blue tile with two stacked slides and a "play" triangle on the front one.
"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "src" / "LiveSlides.App" / "Assets"
BLUE, DARK, WHITE, AMBER = (47, 128, 237), (27, 79, 156), (255, 255, 255), (245, 166, 35)


def draw(size: int) -> Image.Image:
    s = size * 4  # supersample, then shrink for smooth edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=s * 0.2, fill=BLUE)
    # back slide
    d.rounded_rectangle((s * 0.26, s * 0.20, s * 0.86, s * 0.58), radius=s * 0.04, fill=DARK)
    # front slide
    d.rounded_rectangle((s * 0.14, s * 0.34, s * 0.76, s * 0.76), radius=s * 0.04, fill=WHITE)
    # play triangle
    d.polygon([(s * 0.38, s * 0.44), (s * 0.38, s * 0.66), (s * 0.56, s * 0.55)], fill=AMBER)
    return img.resize((size, size), Image.LANCZOS)


OUT.mkdir(parents=True, exist_ok=True)
draw(256).save(OUT / "LiveSlides.png")
draw(256).save(OUT / "LiveSlides.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok")
