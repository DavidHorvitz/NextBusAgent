"""Generate the BusPulse tray icons (bus_active.ico, bus_muted.ico, bus_alert.ico).

Provenance: the artwork is original, drawn from the vector geometry defined in
this script (no third-party artwork). This script is the editable source; the
.ico files in BusPulse/Assets/Icons are exported runtime assets. Edit the
geometry/palette below and rerun:

    python -I tools/generate_icons.py

Each .ico contains 16x16, 32x32 and 48x48 (32-bit BMP) plus 256x256 (PNG)
images with transparent backgrounds. Stdlib only.
"""

import struct
import zlib
from pathlib import Path

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "BusPulse" / "Assets" / "Icons"
SIZES = (16, 32, 48, 256)
SUPERSAMPLE = 4

# Geometry is expressed on a 32x32 design grid and scaled to each size.
# Rects are (left, top, right, bottom, corner radius); circles are (cx, cy, radius).
GEOMETRY = {
    "body": (4.0, 4.0, 28.0, 25.0, 4.0),
    "outline": 1.25,
    "windows": ((7.0, 7.5, 15.0, 14.5, 1.0), (17.0, 7.5, 25.0, 14.5, 1.0)),
    "lights": ((7.0, 19.0, 10.5, 21.5, 0.75), (21.5, 19.0, 25.0, 21.5, 0.75)),
    "wheels": ((10.0, 26.0, 3.25), (22.0, 26.0, 3.25)),
    "badge": (24.5, 24.5, 6.5),
    "badge_gap": 1.25,  # transparent ring separating the badge from the bus
    "pause_bars": ((22.0, 21.5, 23.8, 27.5, 0.0), (25.2, 21.5, 27.0, 27.5, 0.0)),
    "alert_bar": (23.6, 20.9, 25.4, 25.3, 0.6),
    "alert_dot": (24.5, 27.1, 1.0),
}
# 16x16 overrides snap edges to whole pixels (2 design units) so the tray icon stays crisp.
GEOMETRY_16 = dict(
    GEOMETRY,
    body=(4.0, 4.0, 28.0, 24.0, 4.0),
    outline=2.0,
    windows=((8.0, 8.0, 14.0, 14.0, 0.5), (18.0, 8.0, 24.0, 14.0, 0.5)),
    lights=((8.0, 18.0, 10.0, 20.0, 0.0), (22.0, 18.0, 24.0, 20.0, 0.0)),
    wheels=((10.0, 25.0, 3.0), (22.0, 25.0, 3.0)),
    badge=(25.0, 25.0, 7.0),
    badge_gap=2.0,
    pause_bars=((22.0, 22.0, 24.0, 28.0, 0.0), (26.0, 22.0, 28.0, 28.0, 0.0)),
    alert_bar=(24.0, 20.0, 26.0, 26.0, 0.0),
    alert_dot=(25.0, 29.0, 1.2),
)

OUTLINE = (30, 43, 42, 255)
WINDOW = (232, 246, 244, 255)
WHEEL = (30, 43, 42, 255)

STATES = {
    "bus_active": {"body": (20, 163, 148, 255), "badge": None},
    "bus_muted": {"body": (138, 145, 144, 255), "badge": "pause"},
    "bus_alert": {"body": (20, 163, 148, 255), "badge": "alert"},
}
BADGE_COLORS = {
    "pause": ((74, 80, 79, 255), (255, 255, 255, 255)),  # fill, glyph
    "alert": ((245, 166, 35, 255), (30, 43, 42, 255)),
}


def in_round_rect(x, y, rect, grow=0.0):
    left, top, right, bottom, radius = rect
    left, top, right, bottom, radius = left - grow, top - grow, right + grow, bottom + grow, radius + grow
    if x < left or x > right or y < top or y > bottom:
        return False
    cx = min(max(x, left + radius), right - radius)
    cy = min(max(y, top + radius), bottom - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def in_circle(x, y, cx, cy, radius):
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def badge_glyph(x, y, kind, geo):
    if kind == "pause":
        return any(in_round_rect(x, y, bar) for bar in geo["pause_bars"])
    return in_round_rect(x, y, geo["alert_bar"]) or in_circle(x, y, *geo["alert_dot"])


def sample(x, y, state, geo):
    """Return the RGBA colour of the topmost layer at design-grid point (x, y)."""
    badge = state["badge"]
    if badge:
        cx, cy, radius = geo["badge"]
        if in_circle(x, y, cx, cy, radius):
            fill, glyph = BADGE_COLORS[badge]
            return glyph if badge_glyph(x, y, badge, geo) else fill
        if in_circle(x, y, cx, cy, radius + geo["badge_gap"]):
            return None
    for rect in geo["windows"] + geo["lights"]:
        if in_round_rect(x, y, rect):
            return WINDOW
    if in_round_rect(x, y, geo["body"]):
        return state["body"]
    for cx, cy, radius in geo["wheels"]:
        if in_circle(x, y, cx, cy, radius):
            return WHEEL
    if in_round_rect(x, y, geo["body"], geo["outline"]):
        return OUTLINE
    for cx, cy, radius in geo["wheels"]:
        if in_circle(x, y, cx, cy, radius + geo["outline"] * 0.6):
            return OUTLINE
    return None


def render(size, state):
    """Rasterise to a list of rows of (r, g, b, a) with box-filter antialiasing."""
    scale = 32.0 / size
    geo = GEOMETRY_16 if size <= 16 else GEOMETRY
    n = SUPERSAMPLE
    rows = []
    for py in range(size):
        row = []
        for px in range(size):
            r = g = b = a = 0
            for sy in range(n):
                for sx in range(n):
                    color = sample((px + (sx + 0.5) / n) * scale, (py + (sy + 0.5) / n) * scale, state, geo)
                    if color:
                        alpha = color[3]
                        r += color[0] * alpha
                        g += color[1] * alpha
                        b += color[2] * alpha
                        a += alpha
            if a:
                row.append((round(r / a), round(g / a), round(b / a), round(a / (n * n))))
            else:
                row.append((0, 0, 0, 0))
        rows.append(row)
    return rows


def encode_png(rows):
    raw = b"".join(b"\x00" + bytes(c for px in row for c in px) for row in rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    header = struct.pack(">IIBBBBB", len(rows[0]), len(rows), 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def encode_bmp(rows):
    size = len(rows)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = b"".join(bytes((b, g, r, a)) for row in reversed(rows) for r, g, b, a in row)
    mask_stride = ((size + 31) // 32) * 4
    mask = b""
    for row in reversed(rows):
        bits = bytearray(mask_stride)
        for x, px in enumerate(row):
            if px[3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bytes(bits)
    return header + pixels + mask


def write_ico(path, images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries = b""
    data = b""
    for size, payload in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(payload), offset + len(data))
        data += payload
    path.write_bytes(header + entries + data)


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    for name, state in STATES.items():
        images = []
        for size in SIZES:
            rows = render(size, state)
            images.append((size, encode_png(rows) if size >= 256 else encode_bmp(rows)))
        path = OUTPUT_DIR / f"{name}.ico"
        write_ico(path, images)
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
