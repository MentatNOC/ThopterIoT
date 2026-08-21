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


ICON_RED = (216, 69, 59, 255)    # SaphoBrush, dark theme
ICON_INK = (14, 14, 16, 255)     # InkBrush, dark theme
ICON_BONE = (244, 241, 234, 255) # BoneBrush, dark theme


def paint_icon(n):
    """One icon cell at native pixel size n: red rounded square, bone wing X, ink hub."""
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    dr = ImageDraw.Draw(img)
    dr.rounded_rectangle([0, 0, n - 1, n - 1], radius=max(2, n // 5), fill=ICON_RED)

    cx = cy = (n - 1) / 2
    inset = max(2, round(n * 0.14))       # wing tips stop short of the edge
    span = round(n * 0.27)                # vertical spread of the X, wider than tall
    width = max(2, n // 9)

    for dx in (-1, 1):
        for dy in (-1, 1):
            root = (cx + dx, cy)
            tip = (inset if dx < 0 else n - 1 - inset, cy + dy * span)
            dr.line([root, tip], fill=ICON_BONE, width=width)

    hub = max(1, n // 12)
    dr.rectangle([cx - hub, cy - hub, cx + hub, cy + hub], fill=ICON_INK)
    return img


def save_icon(path):
    """Multi-size .ico: 16/24/32 drawn natively, larger sizes nearest-upscaled from 32."""
    cells = {n: paint_icon(n) for n in (16, 24, 32)}
    cells[48] = cells[24].resize((48, 48), Image.NEAREST)
    cells[64] = cells[32].resize((64, 64), Image.NEAREST)
    cells[128] = cells[32].resize((128, 128), Image.NEAREST)
    cells[256] = cells[32].resize((256, 256), Image.NEAREST)

    cells[256].save(path, format="ICO",
                    append_images=[cells[s] for s in (128, 64, 48, 32, 24, 16)])


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    assets = os.path.join(root, "src", "Thopter.App", "Assets")
    out_dir = os.path.join(assets, "Wings")
    os.makedirs(out_dir, exist_ok=True)
    for f in range(FRAMES):
        render_frame(f).save(os.path.join(out_dir, f"wings-{f}.png"))
    print(f"wrote {FRAMES} frames to {out_dir}")

    icon_path = os.path.join(assets, "thopter.ico")
    save_icon(icon_path)
    print(f"wrote {icon_path}")


if __name__ == "__main__":
    main()
