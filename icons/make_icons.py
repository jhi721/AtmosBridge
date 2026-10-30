# Regenerates the .ico files and installer images here: python icons/make_icons.py (needs Pillow)
# One shape for every exe, the sound field from above: listener, rings, objects moving around;
# the accent color tells them apart.
import math
from pathlib import Path
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
BG = "#1C1A19"
ICONS = {
    "atmosbridge": "#FF7A1A",
    "thd-objects": "#2EC4B6",  # TrueHD helper
    "dtsx-objects": "#7C8CFF",  # DTS helper
}
# Installer images in the sizes Setup picks from by DPI: WizardImageFile (164:314) and WizardSmallImageFile
SIDEBAR = [(202, 386), (336, 643), (534, 1022)]
HEADER = [55, 83, 110, 138]
SS = 8  # supersampling


def rgb(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))


def mix(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def field(img, cx, cy, scale, accent, small=False):
    """Draws the 100-unit design centered at (cx, cy) px, scale px per unit."""
    d = ImageDraw.Draw(img)
    u = lambda v: v * scale
    px = lambda x, y: (cx + u(x - 50), cy + u(y - 50))
    bg, acc = rgb(BG), rgb(accent)
    light, faint, white = mix(acc, (255, 255, 255), 0.6), mix(acc, bg, 0.55), (255, 255, 255)

    def ring(r, color, w):
        d.ellipse((*px(50 - r - w / 2, 50 - r - w / 2), *px(50 + r + w / 2, 50 + r + w / 2)), outline=color, width=round(u(w)))

    def dot(x, y, r, color, outline=0):
        if outline:
            d.ellipse((*px(x - r - outline, y - r - outline), *px(x + r + outline, y + r + outline)), fill=bg)
        d.ellipse((*px(x - r, y - r), *px(x + r, y + r)), fill=color)

    ring(31, acc, 7 if small else 5)
    if not small:
        ring(17, faint, 4)
        # Trail behind the moving object
        for ang in (330, 345, 360):
            dot(50 + 31 * math.cos(math.radians(ang)), 50 + 31 * math.sin(math.radians(ang)), 2.5, light)
    dot(50, 50, 8 if small else 6, white)  # listener
    obj = 9 if small else 6
    dot(71.9, 28.1, obj, acc, 3)
    dot(23, 65.5, obj, acc, 3)
    if not small:
        dot(62, 38, 3.75, light, 2.5)


def icon(size, accent):
    n = size * SS
    img = Image.new("RGB", (n, n), BG)
    field(img, n / 2, n / 2, n / 100, accent, small=size <= 24)  # inner ring, trail, third object turn to mush there
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, n - 1, n - 1), radius=n * 0.22, fill=255)
    img.putalpha(mask)
    return img.resize((size, size), Image.LANCZOS)


def sidebar(w, h, accent):
    W, H = w * SS, h * SS
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    cx, cy, scale = W / 2, H * 0.56, W / 100 * 0.85  # outer rings run off the panel edges
    for r, t in ((46, 0.8), (62, 0.86), (80, 0.91), (100, 0.95)):  # ever fainter rings further out
        rr = r * scale
        d.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), outline=mix(rgb(accent), rgb(BG), t), width=round(1.5 * scale))
    field(img, cx, cy, scale, accent)
    return img.resize((w, h), Image.LANCZOS)


here = Path(__file__).parent
for name, accent in ICONS.items():
    imgs = [icon(s, accent) for s in SIZES]
    imgs[-1].save(here / f"{name}.ico", sizes=[(s, s) for s in SIZES], append_images=imgs[:-1])
main = ICONS["atmosbridge"]
for w, h in SIDEBAR:
    sidebar(w, h, main).save(here / f"sidebar-{w}.png")
for s in HEADER:
    icon(s, main).save(here / f"header-{s}.png")
