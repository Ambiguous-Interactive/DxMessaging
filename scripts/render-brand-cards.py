#!/usr/bin/env python3
"""Render the composed brand cards from their SVG sources.

The Open Graph card and the Asset Store card are shipped as PNG, because social
scrapers and the Asset Store submission form do not accept SVG. Their source of
truth is the SVG beside each one, so a palette, copy, or mark change is a text
edit followed by this script.

Usage:

    python3 scripts/render-brand-cards.py

Requirements: cairosvg (already in requirements-docs.txt), the system cairo
library, and the three brand faces installed for fontconfig. The script stops
before writing anything if a face is missing, because cairo silently
substitutes a default face and the render would look almost right.
"""

import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# (SVG source, PNG output, width, height)
CARDS = [
    ("docs/images/dxmessaging-og-1200x630.svg", "docs/images/dxmessaging-og-1200x630.png", 1200, 630),
    ("docs/images/dxmessaging-store-card-420x280.svg", "docs/images/dxmessaging-store-card-420x280.png", 420, 280),
]

REQUIRED_FONTS = ["Space Grotesk", "IBM Plex Sans", "JetBrains Mono"]

# Every SVG that draws the mark inline, so a mark change cannot reach one copy
# and miss another. There is no portable way to reference an external SVG that
# both a browser and cairo honour, so the geometry is duplicated on purpose and
# the duplication is checked instead of hidden.
MARK_SOURCE = "docs/images/dxmessaging-mark.svg"
MARK_COPIES = [
    "docs/images/dxmessaging-icon-tile.svg",
    "docs/images/dxmessaging-og-1200x630.svg",
    "docs/images/dxmessaging-store-card-420x280.svg",
]

FONT_HELP = """Install the missing faces where fontconfig can see them, for example:

    mkdir -p ~/.local/share/fonts && cd ~/.local/share/fonts
    curl -sSLO https://raw.githubusercontent.com/floriankarsten/space-grotesk/master/fonts/otf/SpaceGrotesk-Bold.otf
    curl -sSLO https://raw.githubusercontent.com/IBM/plex/master/packages/plex-sans/fonts/complete/otf/IBMPlexSans-Regular.otf
    curl -sSLO https://raw.githubusercontent.com/JetBrains/JetBrainsMono/master/fonts/ttf/JetBrainsMono-Regular.ttf
    fc-cache -f

All three are SIL Open Font License 1.1."""


def installed_families():
    if shutil.which("fc-list") is None:
        sys.exit("fc-list not found. Install fontconfig, then re-run.")
    probe = subprocess.run(["fc-list", ":", "family"], capture_output=True, text=True)
    if probe.returncode != 0:
        sys.exit(f"fc-list failed ({probe.returncode}): {probe.stderr.strip()}")
    listing = probe.stdout
    families = set()
    for line in listing.splitlines():
        for name in line.split(","):
            families.add(name.strip())
    return families


def drawing_elements(path):
    """The lines between the opening and closing svg tags, stripped of indent.

    Anchored on `<svg`, not on the first `>` in the file, so an XML declaration
    ahead of the root element does not end up inside the comparison.
    """
    text = (REPO_ROOT / path).read_text(encoding="utf-8")
    opening = text.index(">", text.index("<svg"))
    inner = text[opening + 1 :].rsplit("</svg>", 1)[0]
    return [line.strip() for line in inner.splitlines() if line.strip()]


def check_mark_copies():
    mark = drawing_elements(MARK_SOURCE)
    stale = []
    for copy in MARK_COPIES:
        elements = drawing_elements(copy)
        if any(line not in elements for line in mark):
            stale.append(copy)
    if stale:
        sys.exit(
            f"These files draw the mark but no longer match {MARK_SOURCE}:\n"
            + "\n".join(f"    {path}" for path in stale)
            + f"\n\nCopy the drawing elements from {MARK_SOURCE} into each one, then re-run."
        )


def main():
    try:
        import cairosvg
    except ImportError as error:
        sys.exit(f"cairosvg is not importable ({error}). Install requirements-docs.txt into a virtual environment.")

    check_mark_copies()

    available = installed_families()
    missing = [family for family in REQUIRED_FONTS if family not in available]
    if missing:
        sys.exit(f"Missing font families: {', '.join(missing)}.\n\n{FONT_HELP}")

    for svg, png, width, height in CARDS:
        source = REPO_ROOT / svg
        target = REPO_ROOT / png
        if not source.is_file():
            sys.exit(f"Missing SVG source: {svg}")
        cairosvg.svg2png(
            url=str(source), write_to=str(target), output_width=width, output_height=height
        )
        print(f"{png} <- {svg} ({width}x{height}, {target.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
