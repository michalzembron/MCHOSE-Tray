"""Generates the application icon (assets/app.ico): a mouse whose body is partly filled
like a battery gauge. Requires Pillow.  Usage: python tools/make_icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

SS = 4                      # supersampling factor
BASE = 256
OUT = Path(__file__).resolve().parent.parent / "assets" / "app.ico"


def draw(size: int) -> Image.Image:
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Background tile (Windows 11 style rounded square).
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=s * 0.22, fill=(0, 103, 192, 255))

    # Mouse body.
    w, h = s * 0.46, s * 0.70
    x0, y0 = (s - w) / 2, (s - h) / 2
    body = (x0, y0, x0 + w, y0 + h)
    stroke = max(SS, round(s * 0.055))
    d.rounded_rectangle(body, radius=w / 2, fill=(255, 255, 255, 255))

    # Battery fill inside the body (green, lower ~60 %), clipped to the body shape.
    inner = Image.new("L", (s, s), 0)
    ImageDraw.Draw(inner).rounded_rectangle(
        (x0 + stroke, y0 + stroke, x0 + w - stroke, y0 + h - stroke), radius=w / 2 - stroke, fill=255)
    level = Image.new("L", (s, s), 0)
    ImageDraw.Draw(level).rectangle((0, y0 + h * 0.42, s, s), fill=255)
    mask = Image.composite(level, Image.new("L", (s, s), 0), inner)
    img.paste(Image.new("RGBA", (s, s), (108, 203, 95, 255)), (0, 0), mask)

    # Scroll wheel.
    cx = s / 2
    d.rounded_rectangle((cx - stroke * 0.8, y0 + h * 0.12, cx + stroke * 0.8, y0 + h * 0.28),
                        radius=stroke, fill=(0, 103, 192, 255))
    return img.resize((size, size), Image.LANCZOS)


def main():
    OUT.parent.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    big = draw(BASE)
    big.save(OUT, sizes=[(n, n) for n in sizes],
             append_images=[draw(n) for n in sizes if n != BASE])
    big.save(OUT.with_suffix(".png"))
    print(f"written {OUT}")


if __name__ == "__main__":
    main()
