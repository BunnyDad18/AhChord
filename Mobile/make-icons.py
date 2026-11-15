#!/usr/bin/env python3
"""
MT-01: generate the Android launcher icons from the web app's favicon.svg, so the
shell icon matches the app (the Capacitor template ships a generic hero image).

Style: the app's dark theme (its default) — the mark on the --bg tile #1a1a2e,
brightened to #4dd0e1 for contrast (teal #00838f on that navy reads dim, ~3.7:1;
the bright teal reads ~9:1 and keeps the brand's teal identity).

Renders Frontend/public/favicon.svg and writes:
  - ic_launcher.png            legacy square (dark tile, mark at 70%)   48..192
  - ic_launcher_round.png      legacy round (circle-clipped)            48..192
  - ic_launcher_foreground.png adaptive foreground (transparent, mark at 66%) 108..432

The adaptive background is set to the same navy in values/ic_launcher_background.xml
(this script writes it), so the adaptive icon equals the legacy tile.

Run (needs cairosvg, provided by uv):
  uv run --with cairosvg Mobile/make-icons.py
"""

from pathlib import Path
import io
import cairosvg
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SVG = ROOT / "Frontend" / "public" / "favicon.svg"
RES = ROOT / "Mobile" / "android" / "app" / "src" / "main" / "res"
HI_RES = 1024

TILE = (0x1A, 0x1A, 0x2E, 255)  # dark theme --bg
MARK = (0x4D, 0xD0, 0xE1, 255)  # bright teal, ~9:1 on TILE (dark theme reads)

# density → (legacy px, adaptive foreground px)
SIZES = {
    "mipmap-mdpi": (48, 108),
    "mipmap-hdpi": (72, 162),
    "mipmap-xhdpi": (96, 216),
    "mipmap-xxhdpi": (144, 324),
    "mipmap-xxxhdpi": (192, 432),
}


def render_mark() -> Image.Image:
    """The SVG mark, transparent, square, high resolution."""
    png = cairosvg.svg2png(url=str(SVG), output_width=HI_RES, output_height=HI_RES)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def recolor(mark: Image.Image, color) -> Image.Image:
    """Keep the mark's alpha, repaint every pixel `color` (brand tint for dark bg)."""
    out = Image.new("RGBA", mark.size, color)
    out.putalpha(mark.getchannel("A"))
    return out


def place(mark: Image.Image, canvas: int, fraction: float) -> Image.Image:
    """Mark scaled to `fraction` of the canvas, centered, transparent remainder."""
    art = mark.resize((int(canvas * fraction), int(canvas * fraction)), Image.LANCZOS)
    out = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    off = (canvas - art.width) // 2
    out.paste(art, (off, off), art)
    return out


def main() -> None:
    mark = recolor(render_mark(), MARK)

    # Circle mask (4x supersampled) for the legacy round icon.
    big = HI_RES * 4
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, big - 1, big - 1), fill=255)
    mask = mask.resize((HI_RES, HI_RES), Image.LANCZOS)

    for density, (legacy, fg) in SIZES.items():
        out_dir = RES / density
        out_dir.mkdir(parents=True, exist_ok=True)

        # Legacy square: dark tile (launchers mask it; keep the full square readable).
        tile = Image.new("RGBA", (legacy, legacy), TILE)
        sq = place(mark, legacy, 0.70)
        tile.paste(sq, (0, 0), sq)
        tile.save(out_dir / "ic_launcher.png")

        # Legacy round: same tile clipped to a circle.
        rnd = tile.resize((HI_RES, HI_RES), Image.LANCZOS)
        rnd.putalpha(mask)
        rnd = rnd.resize((legacy, legacy), Image.LANCZOS)
        rnd.save(out_dir / "ic_launcher_round.png")

        # Adaptive foreground: transparent, mark inside the 66% safe zone.
        place(mark, fg, 0.66).save(out_dir / "ic_launcher_foreground.png")

        print(f"{density}: legacy {legacy}px, round {legacy}px, foreground {fg}px")

    # Adaptive background = the same navy tile (keeps adaptive == legacy look).
    bg_xml = RES / "values" / "ic_launcher_background.xml"
    bg_xml.write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<resources>\n"
        '    <color name="ic_launcher_background">#1A1A2E</color>\n'
        "</resources>\n",
        encoding="utf-8",
    )
    print(f"{bg_xml.relative_to(ROOT)}: #1A1A2E (dark theme --bg)")


if __name__ == "__main__":
    main()
