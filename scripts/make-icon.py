#!/usr/bin/env python3
"""
Renders src/Scenariometer/images/icon.png (512x512), the plugin's Dalamud icon.

Pure stdlib on purpose - no Pillow, no cairo, nothing to install on a machine that
only ever needs to regenerate one asset. Shapes are signed distance fields and the
edge is antialiased from the distance itself, which is exact for circles, arcs and
rounded boxes and needs no supersampling.

    python scripts/make-icon.py      (python3 on macOS/Linux)

Design: a progress ring (about three quarters round, with a bright head at the
leading edge) around a quest marker - progress along the Main Scenario, and how
far there is left to go.
"""

import math
import struct
import zlib
from pathlib import Path

SIZE = 512
OUT = Path(__file__).resolve().parent.parent / "src" / "Scenariometer" / "images" / "icon.png"

# --- palette ---------------------------------------------------------------
BG_TOP = (26, 32, 54)
BG_BOTTOM = (11, 14, 28)
TRACK = (42, 50, 78)
GOLD_TOP = (255, 216, 128)
GOLD_BOTTOM = (219, 163, 58)
HEAD = (255, 244, 214)

# --- geometry --------------------------------------------------------------
CENTER = SIZE / 2
CORNER_RADIUS = 112
RING_RADIUS = 168
RING_HALF = 19
ARC_START_DEG = -90.0          # 12 o'clock
ARC_SWEEP_DEG = 268.0          # roughly 3/4 of the way round
HEAD_RADIUS = 25

MARK_TOP_Y = -96
MARK_BOTTOM_Y = 32
MARK_TOP_HALF = 27
MARK_BOTTOM_HALF = 13
MARK_ROUND = 8
DOT_Y = 78
DOT_RADIUS = 23


def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else hi if v > hi else v


def lerp(a, b, t):
    return a + (b - a) * t


def lerp_rgb(a, b, t):
    return (lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t))


def coverage(distance, softness=1.0):
    """SDF distance (negative inside) -> 0..1 alpha across a one-pixel edge."""
    return clamp(0.5 - distance / softness)


def sd_rounded_box(x, y, half_w, half_h, radius):
    dx = abs(x) - half_w + radius
    dy = abs(y) - half_h + radius
    outside = math.hypot(max(dx, 0.0), max(dy, 0.0))
    return outside + min(max(dx, dy), 0.0) - radius


def sd_trapezoid(x, y, half_bottom, half_top, half_height):
    """IQ's trapezoid SDF - the tapered bar of the quest marker."""
    k1x, k1y = half_top, half_height
    k2x, k2y = half_top - half_bottom, 2.0 * half_height
    x = abs(x)
    cax = x - min(x, half_bottom if y < 0.0 else half_top)
    cay = abs(y) - half_height
    k2_len2 = k2x * k2x + k2y * k2y
    t = clamp(((k1x - x) * k2x + (k1y - y) * k2y) / k2_len2)
    cbx = x - k1x + k2x * t
    cby = y - k1y + k2y * t
    sign = -1.0 if (cbx < 0.0 and cay < 0.0) else 1.0
    return sign * math.sqrt(min(cax * cax + cay * cay, cbx * cbx + cby * cby))


def arc_alpha(x, y):
    """
    Ring arc with round caps: the annulus, masked to the swept angle, unioned with
    a disc at each end. Masking alone would leave the arc cut off square.
    """
    radial = abs(math.hypot(x, y) - RING_RADIUS) - RING_HALF
    alpha = coverage(radial)
    if alpha <= 0.0:
        return 0.0

    # y points down in image space, so a growing atan2 angle already runs
    # clockwise on screen - which is the direction a progress ring should fill.
    angle = math.degrees(math.atan2(y, x))
    swept = (angle - ARC_START_DEG) % 360.0
    if swept > ARC_SWEEP_DEG:
        # Outside the sweep: only the round caps survive.
        cap = 1e9
        for deg in (ARC_START_DEG, ARC_START_DEG + ARC_SWEEP_DEG):
            cx = math.cos(math.radians(deg)) * RING_RADIUS
            cy = math.sin(math.radians(deg)) * RING_RADIUS
            cap = min(cap, math.hypot(x - cx, y - cy) - RING_HALF)
        return coverage(cap)

    return alpha


def blend(base, layer, alpha):
    if alpha <= 0.0:
        return base
    return (
        lerp(base[0], layer[0], alpha),
        lerp(base[1], layer[1], alpha),
        lerp(base[2], layer[2], alpha),
    )


def render():
    pixels = bytearray()
    head_deg = ARC_START_DEG + ARC_SWEEP_DEG
    head_x = math.cos(math.radians(head_deg)) * RING_RADIUS
    head_y = math.sin(math.radians(head_deg)) * RING_RADIUS

    for py in range(SIZE):
        row = bytearray()
        for px in range(SIZE):
            # Pixel centre, in a coordinate system with the origin at the middle.
            x = px + 0.5 - CENTER
            y = py + 0.5 - CENTER
            down = (py + 0.5) / SIZE

            shape_alpha = coverage(sd_rounded_box(x, y, CENTER, CENTER, CORNER_RADIUS))
            if shape_alpha <= 0.0:
                row += b"\x00\x00\x00\x00"
                continue

            colour = lerp_rgb(BG_TOP, BG_BOTTOM, down)

            # Ring track, then the filled arc over it.
            track = coverage(abs(math.hypot(x, y) - RING_RADIUS) - RING_HALF)
            colour = blend(colour, TRACK, track)

            gold = lerp_rgb(GOLD_TOP, GOLD_BOTTOM, clamp((y + RING_RADIUS) / (2 * RING_RADIUS)))
            colour = blend(colour, gold, arc_alpha(x, y))

            # Bright head at the leading edge - "you are here".
            colour = blend(colour, HEAD, coverage(math.hypot(x - head_x, y - head_y) - HEAD_RADIUS))

            # Quest marker: tapered bar plus dot.
            bar_half_height = (MARK_BOTTOM_Y - MARK_TOP_Y) / 2
            bar_y = y - (MARK_TOP_Y + bar_half_height)
            bar = sd_trapezoid(x, -bar_y, MARK_TOP_HALF, MARK_BOTTOM_HALF, bar_half_height) - MARK_ROUND
            dot = math.hypot(x, y - DOT_Y) - DOT_RADIUS
            mark_gold = lerp_rgb(GOLD_TOP, GOLD_BOTTOM, clamp((y + 110) / 220))
            colour = blend(colour, mark_gold, max(coverage(bar), coverage(dot)))

            row += bytes((
                int(round(clamp(colour[0], 0, 255))),
                int(round(clamp(colour[1], 0, 255))),
                int(round(clamp(colour[2], 0, 255))),
                int(round(shape_alpha * 255)),
            ))
        pixels += row
    return bytes(pixels)


def write_png(path, width, height, rgba):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    stride = width * 4
    raw = b"".join(b"\x00" + rgba[y * stride:(y + 1) * stride] for y in range(height))

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


if __name__ == "__main__":
    write_png(OUT, SIZE, SIZE, render())
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
