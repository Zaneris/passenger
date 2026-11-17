#!/usr/bin/env python3
"""Draws the background pictures of the themes tools/themes.py writes (theme format 1's `background`, docs/themes.md)
into pictures/ under the folder it is given: a checkout of Zaneris/passenger-themes, or the tests' copy. Each picture is
made here from plain geometry and seeded randomness, nothing taken from elsewhere, in one or a few of its theme's
mid-tone hues on a clear ground, so it reads under Dark and Light alike at the low opacity the theme draws it at. The
pictures are written as PNG and turned into WebP by cwebp (Homebrew's webp), small to fetch.

  python3 -I tools/theme-pictures.py FOLDER   (just themes FOLDER)
"""
import math
import os
import random
import struct
import subprocess
import sys
import tempfile
import zlib
from array import array


class Canvas:
    """A picture of premultiplied RGBA in floats, drawn on by covering each pixel a shape reaches by its share."""

    def __init__(self, width, height, tile=False):
        self.width, self.height, self.tile = width, height, tile
        self.pixels = array("f", bytes(4 * 4 * width * height))

    def put(self, x, y, colour, alpha):
        if self.tile:
            x, y = x % self.width, y % self.height
        elif not (0 <= x < self.width and 0 <= y < self.height):
            return
        if alpha <= 0:
            return
        alpha = min(alpha, 1.0)
        i = 4 * (y * self.width + x)
        p = self.pixels
        keep = 1 - alpha
        p[i] = colour[0] * alpha + p[i] * keep
        p[i + 1] = colour[1] * alpha + p[i + 1] * keep
        p[i + 2] = colour[2] * alpha + p[i + 2] * keep
        p[i + 3] = alpha + p[i + 3] * keep

    def glow(self, cx, cy, radius, colour, alpha):
        """A soft round light, fading as a Gaussian to nothing at three radii."""
        reach = int(radius * 3) + 1
        for y in range(int(cy) - reach, int(cy) + reach + 1):
            for x in range(int(cx) - reach, int(cx) + reach + 1):
                d2 = ((x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2) / (radius * radius)
                if d2 < 9:
                    self.put(x, y, colour, alpha * math.exp(-d2 * 1.4))

    def disc(self, cx, cy, radius, colour, alpha, hole=None):
        """A filled circle with a smooth edge; `hole` (cx, cy, radius) takes a circle out of it, a crescent."""
        reach = int(radius) + 2
        for y in range(int(cy) - reach, int(cy) + reach + 1):
            for x in range(int(cx) - reach, int(cx) + reach + 1):
                cover = min(1.0, max(0.0, radius - math.hypot(x + 0.5 - cx, y + 0.5 - cy) + 0.5))
                if hole is not None:
                    cover *= min(1.0, max(0.0, math.hypot(x + 0.5 - hole[0], y + 0.5 - hole[1]) - hole[2] + 0.5))
                self.put(x, y, colour, alpha * cover)

    def stroke(self, points, width, colour, alpha, fade=None):
        """A polyline of the given width with smooth edges; `fade` gives each point's share of the alpha."""
        half = width / 2
        best = {}
        for k in range(len(points) - 1):
            (x0, y0), (x1, y1) = points[k], points[k + 1]
            share0 = fade[k] if fade else 1.0
            dx, dy = x1 - x0, y1 - y0
            length2 = dx * dx + dy * dy or 1e-9
            for y in range(int(min(y0, y1) - half) - 1, int(max(y0, y1) + half) + 2):
                for x in range(int(min(x0, x1) - half) - 1, int(max(x0, x1) + half) + 2):
                    t = max(0.0, min(1.0, ((x + 0.5 - x0) * dx + (y + 0.5 - y0) * dy) / length2))
                    d = math.hypot(x + 0.5 - (x0 + t * dx), y + 0.5 - (y0 + t * dy))
                    cover = min(1.0, max(0.0, half - d + 0.5)) * share0
                    if cover > best.get((x, y), 0):
                        best[(x, y)] = cover
        for (x, y), cover in best.items():
            self.put(x, y, colour, alpha * cover)

    def ellipse(self, cx, cy, rx, ry, tilt, width, colour, alpha, steps=360, arc=(0, 2 * math.pi)):
        a0, a1 = arc
        points = []
        for k in range(steps + 1):
            a = a0 + (a1 - a0) * k / steps
            x, y = rx * math.cos(a), ry * math.sin(a)
            points.append((cx + x * math.cos(tilt) - y * math.sin(tilt), cy + x * math.sin(tilt) + y * math.cos(tilt)))
        self.stroke(points, width, colour, alpha)

    def png(self, path):
        rows = bytearray()
        p = self.pixels
        for y in range(self.height):
            rows.append(0)
            for x in range(self.width):
                i = 4 * (y * self.width + x)
                a = p[i + 3]
                if a <= 0.0005:
                    rows += b"\0\0\0\0"
                    continue
                rows += bytes(min(255, round(p[i + c] / a * 255)) for c in range(3)) + bytes([min(255, round(a * 255))])
        def chunk(kind, data):
            return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
        with open(path, "wb") as f:
            f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", self.width, self.height, 8, 6, 0, 0, 0))
                    + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b""))


def rgb(hexcode):
    h = hexcode.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


# ---------------------------------------------------------------------------------------------------------------------
# Each theme's picture.

def harbour():
    """Tide lines: eleven swells along the foot, each a sum of slow sines, quieter toward the horizon, and the glints of
    a low sun on the water."""
    c = Canvas(1600, 1000)
    rnd = random.Random(11)
    teal, sand = rgb("#2EA898"), rgb("#D9A55B")
    for k in range(11):
        base = 560 + k * 38
        amp = 4 + k * 2.2
        phase = rnd.uniform(0, 6.28)
        points = [(x, base + amp * math.sin(x / (90 + 9 * k) + phase) + amp * 0.5 * math.sin(x / 37 + phase * 2))
                  for x in range(-10, 1611, 6)]
        fade = [0.35 + 0.65 * (x / 1600) for x, _ in points]
        c.stroke(points, 2.2 + k * 0.18, teal, 0.55 + k * 0.035, fade)
    c.disc(1270, 470, 70, sand, 0.55)
    for _ in range(46):
        x, y = rnd.uniform(1000, 1560), rnd.uniform(560, 760)
        c.stroke([(x, y), (x + rnd.uniform(10, 34), y)], 2, sand, rnd.uniform(0.35, 0.8))
    return c


def sorbet():
    """Scoops of soft light: peach, mauve and mint glows gathered toward the corners."""
    c = Canvas(1600, 1000)
    rnd = random.Random(5)
    hues = [rgb("#F2A57E"), rgb("#B48CE8"), rgb("#7CCFB6"), rgb("#F28FB8")]
    for _ in range(26):
        corner = rnd.choice([(0, 0), (1600, 0), (0, 1000), (1600, 1000), (1600, 500)])
        x = corner[0] + rnd.gauss(0, 260) * (1 if corner[0] == 0 else -1)
        y = corner[1] + rnd.gauss(0, 220) * (1 if corner[1] == 0 else -1)
        c.glow(abs(x) if corner[0] == 0 else x, abs(y) if corner[1] == 0 else y, rnd.uniform(40, 120), rnd.choice(hues), rnd.uniform(0.35, 0.8))
    return c


def nocturne():
    """A crescent moon high on the right over a scatter of stars, a few of them crossed with light."""
    c = Canvas(1600, 1000)
    rnd = random.Random(7)
    moon, star, rose = rgb("#E9DD8A"), rgb("#C7B6F7"), rgb("#F07AB8")
    c.glow(1330, 210, 120, moon, 0.25)
    c.disc(1330, 210, 92, moon, 0.85, hole=(1372, 182, 84))
    for _ in range(220):
        x, y, r = rnd.uniform(0, 1600), rnd.uniform(0, 1000) ** 1.0, rnd.uniform(0.7, 2.4)
        c.disc(x, y * rnd.uniform(0.4, 1.0), r, rnd.choice([star, star, rose]), rnd.uniform(0.35, 0.9))
    for _ in range(9):
        x, y = rnd.uniform(200, 1500), rnd.uniform(40, 520)
        size = rnd.uniform(9, 20)
        c.stroke([(x - size, y), (x + size, y)], 1.6, star, 0.8)
        c.stroke([(x, y - size), (x, y + size)], 1.6, star, 0.8)
        c.disc(x, y, 2.6, star, 0.95)
    return c


def saffron():
    """A sunburst rising from the lower right corner, its rays fading outward, over a band of halftone dots."""
    c = Canvas(1600, 1000)
    sun, ember = rgb("#E8A33D"), rgb("#E9663C")
    ox, oy = 1600, 1000
    for k in range(30):
        a = math.pi + (math.pi / 2) * (k + 0.5) / 30
        if k % 2:
            continue
        length = 1150
        points = [(ox + math.cos(a) * r, oy + math.sin(a) * r) for r in range(0, length, 12)]
        fade = [max(0.0, 1 - r / length) for r in range(0, length, 12)]
        c.stroke(points, 22, sun, 0.5, fade)
    c.disc(ox, oy, 150, ember, 0.75)
    c.glow(ox - 40, oy - 40, 140, sun, 0.4)
    for gy in range(0, 1000, 34):
        for gx in range(0, 1600, 34):
            t = (gx / 1600 + (1000 - gy) / 1000) / 2
            r = max(0.0, 8.5 * (0.5 - t))
            if r > 1.2:
                c.disc(gx + (17 if (gy // 34) % 2 else 0), gy, r, ember, 0.55)
    return c


def bonbon():
    """Sprinkles: rounded strokes of candy colours tossed across a tile that repeats without a seam."""
    c = Canvas(480, 480, tile=True)
    rnd = random.Random(3)
    hues = [rgb("#F07AB0"), rgb("#F2C14C"), rgb("#62C995"), rgb("#56A8F5"), rgb("#B48CF2")]
    placed = []
    while len(placed) < 34:
        x, y = rnd.uniform(0, 480), rnd.uniform(0, 480)
        if all(min(abs(x - px), 480 - abs(x - px)) ** 2 + min(abs(y - py), 480 - abs(y - py)) ** 2 > 52 ** 2 for px, py in placed):
            placed.append((x, y))
    for x, y in placed:
        a, length = rnd.uniform(0, math.pi), rnd.uniform(14, 26)
        dx, dy = math.cos(a) * length / 2, math.sin(a) * length / 2
        c.stroke([(x - dx, y - dy), (x + dx, y + dy)], 8, rnd.choice(hues), 0.9)
    return c


def ledger():
    """Graph paper: a fine grid every 16 pixels, a firmer rule every 80, on a tile that repeats."""
    c = Canvas(160, 160, tile=True)
    ink = rgb("#4A7BD0")
    for k in range(0, 160, 16):
        firm = k % 80 == 0
        c.stroke([(k + 0.5, 0), (k + 0.5, 160)], 1.4 if firm else 1, ink, 0.7 if firm else 0.32)
        c.stroke([(0, k + 0.5), (160, k + 0.5)], 1.4 if firm else 1, ink, 0.7 if firm else 0.32)
    return c


def slate():
    """A survey map's contours: the levels of a field of slow sines, closer where it climbs, thicker every fifth."""
    c = Canvas(1400, 900)
    ink = rgb("#7E95C2")
    rnd = random.Random(19)
    waves = [(rnd.uniform(0.002, 0.006), rnd.uniform(0.002, 0.006), rnd.uniform(0, 6.28), rnd.uniform(0.6, 1.0)) for _ in range(5)]
    def field(x, y):
        return sum(w * math.sin(x * fx + y * fy + p) for fx, fy, p, w in waves) + 1.6 * math.exp(-((x - 300) ** 2 + (y - 650) ** 2) / 90000.0)
    step = 3
    grid = [[field(x, y) for x in range(0, 1400 + step, step)] for y in range(0, 900 + step, step)]
    levels = 0.3
    for y in range(900):
        gy, ty = divmod(y, step)
        ty /= step
        for x in range(1400):
            gx, tx = divmod(x, step)
            tx /= step
            row0, row1 = grid[gy], grid[gy + 1]
            v = (row0[gx] * (1 - tx) + row0[gx + 1] * tx) * (1 - ty) + (row1[gx] * (1 - tx) + row1[gx + 1] * tx) * ty
            gxv = (row0[gx + 1] - row0[gx]) / step
            gyv = (row1[gx] - row0[gx]) / step
            slope = math.hypot(gxv, gyv) / levels + 1e-6
            phase = v / levels
            near = abs(phase - round(phase)) / slope
            index = round(phase)
            width = 1.4 if index % 5 == 0 else 0.7
            cover = max(0.0, min(1.0, width - near + 0.5))
            if cover > 0:
                c.put(x, y, ink, cover * (0.9 if index % 5 == 0 else 0.6))
    return c


def orbit():
    """A system seen from its edge: a sun's glow off the lower left, tilted orbits round it, their planets, and stars."""
    c = Canvas(1600, 1000)
    rnd = random.Random(23)
    teal, peri, sand = rgb("#3FD4B0"), rgb("#8E9BFF"), rgb("#E8C48A")
    cx, cy = 120, 1040
    c.glow(cx, cy, 220, sand, 0.7)
    for k, (rx, ry) in enumerate([(360, 150), (560, 230), (780, 320), (1030, 420), (1300, 520), (1600, 640)]):
        c.ellipse(cx, cy, rx, ry, -0.32, 1.6, peri if k % 2 else teal, 0.7, steps=480)
        if k in (1, 2, 4, 5):
            a = rnd.uniform(3.6, 5.6)
            x, y = rx * math.cos(a), ry * math.sin(a)
            px, py = cx + x * math.cos(-0.32) - y * math.sin(-0.32), cy + x * math.sin(-0.32) + y * math.cos(-0.32)
            size = rnd.uniform(9, 22)
            c.glow(px, py, size * 1.4, teal if k % 2 else sand, 0.35)
            c.disc(px, py, size, teal if k % 2 else sand, 0.9)
    for _ in range(170):
        c.disc(rnd.uniform(0, 1600), rnd.uniform(0, 1000), rnd.uniform(0.6, 2.0), rnd.choice([peri, teal, sand]), rnd.uniform(0.3, 0.85))
    return c


def nightjar():
    """A moonlit night: a full moon in its halo, long thin clouds drifting under it, stars, and a nightjar on the wing."""
    c = Canvas(1600, 1000)
    rnd = random.Random(29)
    moon, cloud, star = rgb("#D6E4FF"), rgb("#7FB3E6"), rgb("#C9D8F5")
    c.glow(1240, 230, 170, moon, 0.35)
    c.disc(1240, 230, 78, moon, 0.9)
    for _ in range(9):
        y, x0 = rnd.uniform(200, 560), rnd.uniform(400, 1100)
        length = rnd.uniform(260, 620)
        for k in range(0, int(length), 18):
            c.glow(x0 + k, y + 6 * math.sin(k / 70), 16, cloud, 0.12 * math.sin(math.pi * k / length))
    for _ in range(160):
        c.disc(rnd.uniform(0, 1600), rnd.uniform(0, 700), rnd.uniform(0.6, 1.9), star, rnd.uniform(0.3, 0.85))
    # The bird: two swept wings of curves from a small body, gliding left of the moon.
    bx, by = 1010, 360
    for side in (-1, 1):
        wing = [(bx + side * t * 120, by - 38 * math.sin(math.pi * t) + 26 * t * t) for t in [k / 24 for k in range(25)]]
        c.stroke(wing, 7, moon, 0.85, [1 - 0.6 * k / 24 for k in range(25)])
    c.disc(bx, by + 4, 9, moon, 0.9)
    c.stroke([(bx, by + 8), (bx, by + 40)], 8, moon, 0.85)
    return c


def moss():
    """Two ferns grown by Barnsley's iterated maps, one rising from the lower right corner and a smaller one opposite."""
    c = Canvas(1600, 1000)
    leaf, deep = rgb("#6FB062"), rgb("#3E8E6A")
    rnd = random.Random(31)
    for (ox, oy, scale, lean, ink) in [(1380, 1010, 92, -0.18, leaf), (190, 1010, 58, 0.22, deep)]:
        density = {}
        x, y = 0.0, 0.0
        for _ in range(260000):
            r = rnd.random()
            if r < 0.01:
                x, y = 0.0, 0.16 * y
            elif r < 0.86:
                x, y = 0.85 * x + 0.04 * y, -0.04 * x + 0.85 * y + 1.6
            elif r < 0.93:
                x, y = 0.2 * x - 0.26 * y, 0.23 * x + 0.22 * y + 1.6
            else:
                x, y = -0.15 * x + 0.28 * y, 0.26 * x + 0.24 * y + 0.44
            px = ox + scale * (x * math.cos(lean) + y * math.sin(lean))
            py = oy - scale * (-x * math.sin(lean) + y * math.cos(lean))
            key = (int(px), int(py))
            density[key] = density.get(key, 0) + 1
        for (px, py), n in density.items():
            c.put(px, py, ink, min(1.0, 0.25 + n * 0.12))
    return c


def phosphor():
    """A screen's scanlines: one lit row in four, on a tile that repeats."""
    c = Canvas(8, 4, tile=True)
    lime = rgb("#9FE04A")
    for x in range(8):
        c.put(x, 0, lime, 0.9)
        c.put(x, 1, lime, 0.25)
    return c


PICTURES = {"harbour": harbour, "sorbet": sorbet, "nocturne": nocturne, "saffron": saffron, "bonbon": bonbon,
            "ledger": ledger, "slate": slate, "orbit": orbit, "nightjar": nightjar, "moss": moss, "phosphor": phosphor}


def main():
    if len(sys.argv) < 2 or not os.path.isfile(os.path.join(sys.argv[1], "passenger-themes.json")):
        raise SystemExit("theme-pictures.py takes the folder of a source holding its passenger-themes.json, and the names of "
                         "the pictures to draw (all of them if none): " + ", ".join(PICTURES))
    folder = os.path.join(sys.argv[1], "pictures")
    os.makedirs(folder, exist_ok=True)
    for name in sys.argv[2:] or PICTURES:
        picture = PICTURES[name]()
        with tempfile.TemporaryDirectory() as scratch:
            png = os.path.join(scratch, f"{name}.png")
            picture.png(png)
            subprocess.run(["cwebp", "-quiet", "-q", "75", "-alpha_q", "80", "-m", "6", png, "-o", os.path.join(folder, f"{name}.webp")], check=True)
        print(f"{name}.webp: {os.path.getsize(os.path.join(folder, f'{name}.webp')) // 1024} KB")


if __name__ == "__main__":
    sys.exit(main())
