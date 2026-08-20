"""Regenerates the WingFlutter sprite frames (src/Thopter.App/Assets/Wings/wings-*.png).

Four ornithopter blade wings, no fuselage, drawn as 8-bit pixel art: front and rear
pairs flap out of phase like a dragonfly. Frames are baked at 2x (256x256) so the app
can draw them 1:1 with nearest-neighbor scaling and keep the pixels chunky.

Requires Pillow:  pip install pillow
Usage:            python tools/wings/generate-wings.py
"""
import math
import os

from PIL import Image, ImageDraw

W = H = 128          # logical pixel-art canvas
BAKE_SCALE = 2       # exported at 256x256
FRAMES = 8
L = 52               # wing length (logical px)
AMP = 15             # flap amplitude (tip vertical travel)

OUTLINE = (66, 60, 48, 255)      # dark blade outline
BLADE = (207, 200, 180, 255)     # bone/khaki fill
HUB = (107, 100, 85, 255)        # pivot pod
HUB_CORE = (160, 152, 132, 255)

# (pivot_x, pivot_y, direction, base_splay, phase)
# Front pair tips angle up and out, rear pair tips angle down and out (X shape).
WINGS = [
    (61, 56, -1, -9, 0.0),
    (67, 56, +1, -9, 0.0),
    (61, 67, -1, +9, math.pi),
    (67, 67, +1, +9, math.pi),
]


def tip_at(px, py, d, splay, phase, f):
    flap = AMP * math.sin(2 * math.pi * f / FRAMES + phase)
    return (px + d * L, py + splay - round(flap))


def perp(px, py, tx, ty):
    dx, dy = tx - px, ty - py
    n = math.hypot(dx, dy) or 1.0
    return -dy / n, dx / n


def draw_blade(dr, px, py, tx, ty):
    # Solid tapered quad: wide at the root, pointed at the tip.
    nx, ny = perp(px, py, tx, ty)
    rw, tw = 2.6, 0.7
    poly = [
        (px + nx * rw, py + ny * rw),
        (tx + nx * tw, ty + ny * tw),
        (tx - nx * tw, ty - ny * tw),
        (px - nx * rw, py - ny * rw),
    ]
    dr.polygon(poly, fill=BLADE, outline=OUTLINE)
    # Panel-line ticks along the blade for the segmented mechanical look.
    for t in (0.28, 0.5, 0.72):
        cx = px + (tx - px) * t
        cy = py + (ty - py) * t
        dr.line([(cx + nx, cy + ny), (cx - nx, cy - ny)], fill=OUTLINE, width=1)


def render_frame(f):
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    dr = ImageDraw.Draw(img)

    for px, py, d, splay, phase in WINGS:
        tx, ty = tip_at(px, py, d, splay, phase, f)
        draw_blade(dr, px, py, tx, ty)

    # Tiny pivot pods (mechanism, not fuselage).
    for px, py, d, splay, phase in WINGS:
        dr.rectangle([px - 1, py - 1, px + 1, py + 1], fill=HUB)
        dr.point((px, py), fill=HUB_CORE)

    return img.resize((W * BAKE_SCALE, H * BAKE_SCALE), Image.NEAREST)


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out_dir = os.path.join(root, "src", "Thopter.App", "Assets", "Wings")
    os.makedirs(out_dir, exist_ok=True)
    for f in range(FRAMES):
        render_frame(f).save(os.path.join(out_dir, f"wings-{f}.png"))
    print(f"wrote {FRAMES} frames to {out_dir}")


if __name__ == "__main__":
    main()
