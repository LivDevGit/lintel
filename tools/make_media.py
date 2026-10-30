"""Compose Lintel's recorded demo frames onto a generated wallpaper; build README media."""
import glob
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

DEMO, OUT = sys.argv[1], sys.argv[2]
os.makedirs(OUT, exist_ok=True)
FONTS = "C:/Windows/Fonts/"


def wallpaper(w, h, seed=4):
    """Deep night-blue gradient with soft aurora ribbons and fine grain."""
    rnd = random.Random(seed)
    # Night-blue base, a touch lighter towards the top where the "screen edge" is.
    top, bottom = Image.new("RGB", (w, h), (20, 24, 58)), Image.new("RGB", (w, h), (8, 9, 24))
    base = Image.composite(bottom, top, Image.linear_gradient("L").resize((w, h)))
    glow = Image.new("RGB", (w, h), (0, 0, 0))
    d = ImageDraw.Draw(glow)
    ribbons = [((0x2E, 0xE6, 0xC5), 0.18), ((0x7B, 0x5C, 0xFF), 0.52), ((0xFF, 0x4F, 0xA3), 0.86), ((0x3A, 0x9B, 0xFF), 0.35)]
    for color, cx in ribbons:
        for _ in range(3):
            x = int(w * (cx + rnd.uniform(-0.08, 0.08)))
            y = int(h * rnd.uniform(0.35, 1.05))
            rx, ry = int(w * rnd.uniform(0.10, 0.22)), int(h * rnd.uniform(0.25, 0.55))
            d.ellipse([x - rx, y - ry, x + rx, y + ry], fill=tuple(int(c * 0.55) for c in color))
    glow = glow.filter(ImageFilter.GaussianBlur(radius=max(w, h) * 0.08))
    combined = ImageChops.add(base, glow)
    # Fine grain keeps the gradients from banding.
    grain = Image.effect_noise((w, h), 40).convert("RGB")
    return Image.blend(combined, grain, 0.035)


def alpha_bbox(paths):
    box = None
    for p in paths[:: max(1, len(paths) // 40)] + paths[-1:]:
        bb = Image.open(p).getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
        if bb:
            box = bb if box is None else (min(box[0], bb[0]), min(box[1], bb[1]), max(box[2], bb[2]), max(box[3], bb[3]))
    return box


def scene(name, fps=30):
    paths = sorted(glob.glob(os.path.join(DEMO, name, "f*.png")))
    first = Image.open(paths[0])
    W, H = first.size
    # Frames cover the 660 DIP-wide window at whatever --demo-scale was used; aim for 1.24 px per DIP.
    scale = 1.24 / (W / 660)
    box = alpha_bbox(paths)
    pad = int(35 * W / 660)
    left, right = max(0, box[0] - pad), min(W, box[2] + pad)
    bottom = min(H, box[3] + pad)
    crop = (left, 0, right, bottom)  # keep the top edge: the island lives at the top of a screen
    bg = wallpaper(right - left, bottom, seed=hash(name) % 100)
    frames = []
    for p in paths:
        fr = Image.open(p).convert("RGBA").crop(crop)
        comp = bg.convert("RGBA")
        comp.alpha_composite(fr)
        comp = comp.convert("RGB").resize((int(comp.width * scale), int(comp.height * scale)), Image.LANCZOS)
        frames.append(comp)
    # Real capture times -> per-frame durations, so playback matches the actual animation speed.
    times = [int(t) for t in open(os.path.join(DEMO, name, "times.txt")).read().split()]
    durations = [max(20, b - a) for a, b in zip(times, times[1:])][: len(frames)]
    durations += [int(1000 / fps)] * (len(frames) - len(durations))
    durations[-1] += 700  # hold the last frame a moment before looping
    out = os.path.join(OUT, f"{name}.webp")
    frames[0].save(out, save_all=True, append_images=frames[1:], duration=durations, loop=0, quality=82, method=5)
    print(f"  {name}: {sum(durations) / 1000:.1f}s of real time")
    print(name, len(frames), "frames", frames[0].size, os.path.getsize(out) // 1024, "KB")


def banner():
    W, H = 1760, 640
    bg = wallpaper(W, H, seed=9).convert("RGBA")
    hero = Image.open(os.path.join(DEMO, "hero.png")).convert("RGBA")
    bb = hero.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
    hero = hero.crop(bb)
    target_w = 960
    hero = hero.resize((target_w, int(hero.height * target_w / hero.width)), Image.LANCZOS)
    bg.alpha_composite(hero, (W - target_w - 70, (H - hero.height) // 2 + 10))

    d = ImageDraw.Draw(bg)
    title = ImageFont.truetype(FONTS + "seguisb.ttf", 128)
    tag = ImageFont.truetype(FONTS + "segoeui.ttf", 40)
    chip = ImageFont.truetype(FONTS + "seguisb.ttf", 26)
    x = 90
    d.text((x, 150), "Lintel", font=title, fill=(245, 245, 250))
    d.text((x + 4, 320), "A dynamic island for", font=tag, fill=(200, 204, 222))
    d.text((x + 4, 372), "the top of Windows 11", font=tag, fill=(200, 204, 222))
    # Translucent chips go on their own layer so they blend instead of overwriting pixels.
    layer = Image.new("RGBA", bg.size, (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    cx, cy = x + 4, 462
    for label in ["Music", "Agenda", "Shelf", "Power"]:
        tw = ld.textlength(label, font=chip)
        ld.rounded_rectangle([cx, cy, cx + tw + 36, cy + 46], radius=23, fill=(255, 255, 255, 30), outline=(255, 255, 255, 70), width=2)
        ld.text((cx + 18, cy + 7), label, font=chip, fill=(240, 241, 250, 255))
        cx += tw + 50
    bg.alpha_composite(layer)
    out = os.path.join(OUT, "banner.png")
    bg.convert("RGB").save(out, optimize=True)
    print("banner", os.path.getsize(out) // 1024, "KB")


which = sys.argv[3:] or ["banner", "power", "music", "volume", "shelf"]
for item in which:
    banner() if item == "banner" else scene(item)
