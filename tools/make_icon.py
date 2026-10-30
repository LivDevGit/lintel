"""Draws Lintel's app icon (a beam resting on two posts) and saves a multi-size .ico."""
import sys
from PIL import Image, ImageDraw

def draw(size):
    s = size * 4  # supersample, then downscale for smooth edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    # Dark tile with a subtle violet-to-pink glow along the bottom.
    tile = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    grad = Image.new("RGBA", (s, s))
    for y in range(s):
        t = y / s
        grad.paste((int(16 + 70 * t ** 2), int(16 + 18 * t ** 2), int(28 + 90 * t ** 2), 255), (0, y, s, y + 1))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=255)
    tile.paste(grad, (0, 0), mask)
    img.alpha_composite(tile)
    d = ImageDraw.Draw(img)
    white = (245, 245, 250, 255)
    # Beam (the island) and two posts.
    d.rounded_rectangle([s * 0.16, s * 0.24, s * 0.84, s * 0.40], radius=int(s * 0.08), fill=white)
    d.rectangle([s * 0.27, s * 0.40, s * 0.37, s * 0.80], fill=white)
    d.rectangle([s * 0.63, s * 0.40, s * 0.73, s * 0.80], fill=white)
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
big = draw(256)
big.save(out, sizes=[(n, n) for n in sizes], append_images=[draw(n) for n in sizes[:-1]])
big.save(out.replace(".ico", ".png"))
print("wrote", out)
