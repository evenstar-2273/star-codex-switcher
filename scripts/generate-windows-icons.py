"""Package the selected Star Codex Switcher artwork without redrawing it (Pillow)."""
from pathlib import Path
from io import BytesIO
import struct

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "windows" / "CodexAccountSwitcher"
SIZES = [(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
RESAMPLE = getattr(Image, "Resampling", Image).LANCZOS


def write_icon(image, path):
    frames = []
    for size in SIZES:
        frame = image.resize(size, RESAMPLE)
        data = BytesIO()
        if size[0] == 256:
            frame.save(data, format="PNG")
            frames.append(data.getvalue())
        else:
            frame.save(data, format="ICO", sizes=[size], bitmap_format="bmp")
            encoded = data.getvalue()
            length, offset = struct.unpack_from("<II", encoded, 14)
            frames.append(encoded[offset:offset + length])
    offset = 6 + 16 * len(frames)
    header = struct.pack("<HHH", 0, 1, len(frames))
    for size, frame in zip(SIZES, frames):
        header += struct.pack("<BBBBHHII", size[0] % 256, size[1] % 256, 0, 0, 1, 32, len(frame), offset)
        offset += len(frame)
    path.write_bytes(header + b"".join(frames))

# The chosen artwork has real alpha outside its navy rounded tile. Retain that
# original artwork for every Windows surface. The same full-color tray mark is
# intentional: it stays recognizable on both light and dark taskbars without
# introducing a separately redrawn silhouette or an unreliable alpha extraction.
source = Image.open(ROOT / "assets" / "star-codex-switcher-logo.png").convert("RGBA")
for filename in ("app.ico", "tray-light.ico", "tray-dark.ico"):
    write_icon(source, OUTPUT / filename)

print("Packaged selected Star Codex Switcher artwork at 9 Windows sizes; alpha preserved.")
