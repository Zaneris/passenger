#!/usr/bin/env python3
"""Writes src/Passenger.App/Theme/FileIconSet.axaml, Passenger's file icons, from the sets the file-icons package
(https://github.com/file-icons) draws with: its own icons (ISC), MFixx (MIT) and GitHub's Octicons (MIT), plus the few
drawn here.

Each SVG is fetched once into artifacts/file-icons/ and kept there under its set's pinned URL; the list below is the only
place a shape is named.

The icons are drawn at one weight, Octicons' outline: a logo that is a solid badge (a block, shield or disc with its
letters cut out of it, OUTLINED) keeps its outer edge as a ring and its letters filled, so it reads as light as the rest.
"""
import hashlib
import math
import os
import re
import sys
import urllib.parse
import urllib.request

# Each set's commit, so every run draws the same shapes: a newer one is a new commit here, its changes seen in review.
SETS = {
    "fi": "https://raw.githubusercontent.com/file-icons/icons/e6e6e6ac8cb1d91867167c228c00a667f4d47101/svg/{}.svg",
    "mf": "https://raw.githubusercontent.com/file-icons/MFixx/aabb5badb3fbf70ea2446794126e4247e184d653/svg/{}.svg",
    "oc": "https://raw.githubusercontent.com/primer/octicons/2f3d788e5357d908137436fefd349685692b30da/icons/{}-16.svg",
}

# Resource key (FileIcon.KEY), the set and the icon's name in it.
ICONS = [
    # Octicons: the plain kinds, as file-icons draws them.
    ("File", "oc", "file"),
    ("Code", "oc", "file-code"),
    ("Binary", "oc", "file-binary"),
    ("Archive", "oc", "file-zip"),
    ("Book", "oc", "book"),
    ("Checklist", "oc", "checklist"),
    ("Markdown", "oc", "markdown"),
    ("Key", "oc", "key"),
    ("Lock", "oc", "lock"),
    ("Git", "oc", "git-branch"),
    ("GitHub", "oc", "mark-github"),
    ("Gears", "oc", "gear"),
    ("Audio", "oc", "unmute"),
    ("Law", "oc", "law"),
    ("History", "oc", "history"),
    ("Stack", "oc", "stack"),
    ("Database", "oc", "database"),
    # MFixx: the languages file-icons takes from Mfizz.
    ("CSharp", "mf", "csharp"),
    ("C", "mf", "c"),
    ("Cpp", "mf", "c++"),
    ("Java", "mf", "java"),
    ("JavaScript", "mf", "javascript"),
    ("Python", "mf", "python"),
    ("Ruby", "mf", "ruby"),
    ("Html", "mf", "html"),
    ("Css", "mf", "css"),
    ("Scala", "mf", "scala"),
    ("Elixir", "mf", "elixir"),
    ("Erlang", "mf", "erlang"),
    ("Clojure", "mf", "clojure"),
    ("Perl", "mf", "perl"),
    ("ObjectiveC", "mf", "objc"),
    ("Angular", "mf", "angular"),
    # file-icons' own.
    ("CSharpScript", "fi", "C#-Script"),
    ("TypeScript", "fi", "TypeScript"),
    ("Tsx", "fi", "TSX"),
    ("Jsx", "fi", "JSX"),
    ("Json", "fi", "JSON-1"),
    ("Yaml", "fi", "YAML"),
    ("Toml", "fi", "TOML"),
    ("Docker", "fi", "Docker"),
    ("DotEnv", "fi", "dotenv"),
    ("EditorConfig", "fi", "EditorConfig"),
    ("ESLint", "fi", "ESLint"),
    ("Prettier", "fi", "Prettier"),
    ("NuGet", "fi", "NuGet"),
    ("PowerShell", "fi", "PowerShell"),
    # Octicons' prompt, an outline, where file-icons' Terminal is a solid block.
    ("Terminal", "oc", "terminal"),
    ("Config", "fi", "Config"),
    ("ConfigJs", "fi", "Config-JS"),
    ("Diff", "fi", "Diff"),
    ("Patch", "fi", "Patch"),
    ("Font", "fi", "Font-Outline"),
    ("Video", "fi", "Video"),
    ("Image", "fi", "Image"),
    # The gopher, square, where Go's wordmark is three times as wide as high and too small to read at 16 pixels.
    ("Go", "fi", "Go-Old"),
    ("Kotlin", "fi", "Kotlin"),
    ("Lua", "fi", "Lua"),
    ("Php", "fi", "PHP"),
    ("R", "fi", "R"),
    ("Svelte", "fi", "Svelte"),
    ("Vue", "fi", "Vue"),
    ("Webpack", "fi", "Webpack"),
    ("Yarn", "fi", "Yarn"),
    ("Vite", "fi", "Vite"),
    ("GraphQL", "fi", "GraphQL"),
    ("Babel", "fi", "Babel"),
    ("TestJs", "fi", "Test-JS"),
    ("TestTypeScript", "fi", "Test-TypeScript"),
    ("Jupyter", "fi", "Jupyter"),
    ("CMake", "fi", "Cmake"),
    ("Gradle", "fi", "Gradle"),
    ("Kubernetes", "fi", "Kubernetes"),
    ("Terraform", "fi", "Terraform"),
    ("SQLite", "fi", "SQLite"),
    ("LaTeX", "fi", "LaTeX"),
    ("Pug", "fi", "Pug"),
    ("Tailwind", "fi", "Tailwind"),
    ("Jest", "fi", "Jest"),
    ("Storybook", "fi", "Storybook"),
    ("DevContainer", "fi", "DevContainer"),
    ("VSCode", "fi", "VSCode"),
    ("Robots", "fi", "Robots"),
    ("Mdx", "fi", "MDX"),
    ("Mermaid", "fi", "Mermaid"),
    ("Zig", "fi", "Zig"),
    ("Nix", "fi", "Nix"),
    ("Julia", "fi", "Julia"),
    ("Elm", "fi", "Elm"),
    ("Crystal", "fi", "Crystal"),
    ("WebAssembly", "fi", "WebAssembly"),
    ("Assembly", "fi", "Assembly-Generic"),
    ("Fortran", "fi", "Fortran"),
    ("Arduino", "fi", "Arduino"),
    ("Godot", "fi", "Godot"),
    ("WiX", "fi", "WiX"),
    ("AsciiDoc", "fi", "AsciiDoc"),
    ("ReStructuredText", "fi", "reStructuredText"),
    ("PreCommit", "fi", "Precommit"),
    ("Husky", "fi", "Husky"),
    ("Renovate", "fi", "Renovate"),
    ("Dependabot", "fi", "Dependabot"),
    ("Browserslist", "fi", "Browserslist"),
    ("Pnpm", "fi", "PNPM"),
    ("Deno", "fi", "Deno"),
    ("Vagrant", "fi", "Vagrant"),
    ("Ansible", "fi", "Ansible"),
    ("Lisp", "fi", "Lisp"),
    ("OCaml", "fi", "OCaml"),
    ("Codecov", "fi", "Codecov"),
    ("Rollup", "fi", "Rollup"),
    ("Stylus", "fi", "Stylus"),
    ("PostCss", "fi", "PostCSS"),
    ("Stylelint", "fi", "Stylelint"),
    ("Bazel", "fi", "Bazel"),
    ("NextJs", "fi", "NextJS"),
    ("Nuxt", "fi", "Nuxt"),
    ("Electron", "fi", "Electron"),
    ("Firebase", "fi", "Firebase"),
    ("Netlify", "fi", "Netlify"),
    ("GitLab", "fi", "GitLab"),
    ("Cucumber", "fi", "Cucumber"),
    ("Antlr", "fi", "ANTLR"),
    ("Solidity", "fi", "Solidity"),
    ("Prisma", "fi", "Prisma"),
    ("Matlab", "fi", "MATLAB"),
    ("Twig", "fi", "Twig"),
    ("Jinja", "fi", "Jinja"),
    ("Ejs", "fi", "EJS"),
    ("Moustache", "fi", "Moustache"),
    ("OpenApi", "fi", "OpenAPI"),
    ("Swagger", "fi", "Swagger"),
    ("Kusto", "fi", "Kusto"),
    ("LinqPad", "fi", "LINQPad"),
]

# Drawn here for the languages none of the sets has (in a 16 by 16 box, nonzero): F# as two chevrons opening right,
# Visual Basic as a V over a bar, Rust as a cog's ring, Swift as a swallow's wedge, Dart as an arrow head; and badges
# whose cut-outs are its pieces' gaps, so no ring can be drawn from them, redrawn to be outlined as the other badges
# are: npm's square with its n, Racket's disc with its lambda, Haxe's square with its diamond.
OWN = {
    "Npm": "M0 0 H16 V16 H0 Z M4 12 V4 H12 V12 H9.5 V6.5 H6.5 V12 Z",
    "Racket": "M8 0 A8 8 0 1 0 8 16 A8 8 0 1 0 8 0 Z M5 3.5 L7 3.5 L12 12.5 L10.2 12.5 L8.1 8.6 L5.9 12.5 L4.1 12.5 L7.2 7 L6.4 5.5 L5 5.5 Z",
    "Haxe": "M0 0 H16 V16 H0 Z M8 3 L13 8 L8 13 L3 8 Z",
    "FSharp": "M7 1.5 L1 8 L7 14.5 L7 11 L4.2 8 L7 5 Z M8.5 4.5 L5.3 8 L8.5 11.5 Z M9.5 1.5 L9.5 5 L12.3 8 L9.5 11 L9.5 14.5 L15.5 8 Z",
    "VisualBasic": "M1.5 2 L4 2 L8 10.5 L12 2 L14.5 2 L9.2 13 L6.8 13 Z M3 14 L13 14 L13 15.5 L3 15.5 Z",
    "Rust": "M8 0.5 L9.4 2 L11.3 1.4 L11.9 3.3 L13.9 3.6 L13.6 5.6 L15.2 6.8 L14 8.4 L15.2 10 L13.6 11.2 L13.9 13.2 L11.9 13.5 L11.3 15.4 L9.4 14.8 L8 16 L6.6 14.8 L4.7 15.4 L4.1 13.5 L2.1 13.2 L2.4 11.2 L0.8 10 L2 8.4 L0.8 6.8 L2.4 5.6 L2.1 3.6 L4.1 3.3 L4.7 1.4 L6.6 2 Z M5 5 L5 12 L6.6 12 L6.6 9.4 L8.2 9.4 L9.6 12 L11.4 12 L9.8 9.1 C10.6 8.8 11.1 8.1 11.1 7.2 C11.1 5.9 10.2 5 8.8 5 Z M6.6 6.4 L8.7 6.4 C9.2 6.4 9.5 6.8 9.5 7.2 C9.5 7.7 9.2 8 8.7 8 L6.6 8 Z",
    "Swift": "M1 4 C4 7 7 9.5 10 11 C7.5 12 4.5 11.5 1.5 9.5 C4 13 8 15 11.5 14 C13 13.6 14 14 15 15 C15 13.5 14.5 12.5 14 12 C15.5 9 14 5 10 1.5 C11.5 4 12 6.5 11 8.5 C8 6.5 5 5 1 4 Z",
    "Dart": "M4 1 L10.5 1 L15 5.5 L15 12 L12 15 L5.5 15 L1 10.5 L1 4 Z M4 3.5 L3.5 10 L6 12.5 L12.5 12.5 Z",
}

# The solid badges drawn as outlines: blocks, shields and discs whose letters are cut out of them.
OUTLINED = {
    "DotEnv", "Storybook", "Markdown", "Mdx", "ReStructuredText", "WebAssembly", "PowerShell",
    "Html", "Css", "ObjectiveC", "Angular", "Dart", "Rust", "Go", "Npm", "Racket", "Haxe",
    "Antlr", "NextJs", "Ansible", "Swagger", "Deno", "DevContainer", "Kubernetes", "Dependabot", "GitHub",
    "Lua",
}

# The outlined badges whose letters reach the edge (dotenv's name along its foot): drawn smaller, inside the ring.
INSIDE = {"DotEnv"}

# An outline's ring, as a share of the badge's larger side (Octicons' 1.5 of 16), and the gap inside it before letters
# moved in.
RING = 0.1
GAP = 0.08

NUMBER = re.compile(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
ARITY = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}


def path_data(d, places=2):
    """An SVG path as Avalonia's path markup: every number apart and rounded to PLACES, an arc's flags read as the one
    digit each is, since SVG lets them run together ("a.25.25 0 0 0-.25.25") where Avalonia's parser would read a
    longer number."""
    out = []
    i = 0
    command = None
    count = 0
    while i < len(d):
        c = d[i]
        if c in " ,\t\r\n":
            i += 1
            continue
        if c.upper() in ARITY:
            command = c
            count = 0
            out.append(c)
            i += 1
            continue
        if command is None:
            sys.exit(f"file-icons: a number before any command in {d[:40]}")
        arity = ARITY[command.upper()]
        if arity == 0:
            sys.exit(f"file-icons: a number after Z in {d[:40]}")
        if command.upper() == "A" and count % 7 in (3, 4):
            out.append(c)
            i += 1
        else:
            m = NUMBER.match(d, i)
            if not m:
                sys.exit(f"file-icons: can't read {d[i:i + 20]!r}")
            out.append(f"{round(float(m.group(0)), places):g}")
            i = m.end()
        count += 1
    return " ".join(out)


def segments(data):
    """Path markup as absolute segments, split into subpaths: each a list of ("M"|"L"|"C"|"Q"|"A"|"Z", values), with
    S, T, H and V and every relative command made absolute."""
    tokens = data.split()
    out, current = [], None
    x = y = sx = sy = 0.0
    last_c = last_q = None
    i = 0
    command = None
    while i < len(tokens):
        if tokens[i].upper() in ARITY and not NUMBER.fullmatch(tokens[i]):
            command = tokens[i]
            i += 1
            if command.upper() == "Z":
                current.append(("Z", ()))
                x, y = sx, sy
                last_c = last_q = None
                continue
            if i < len(tokens) and not NUMBER.fullmatch(tokens[i]):
                continue
        n = ARITY[command.upper()]
        v = [float(t) for t in tokens[i:i + n]]
        i += n
        rel = command.islower()
        kind = command.upper()
        ox, oy = (x, y) if rel else (0.0, 0.0)
        if kind == "M":
            x, y = v[0] + ox, v[1] + oy
            sx, sy = x, y
            current = [("M", (x, y))]
            out.append(current)
            command = "l" if rel else "L"
            last_c = last_q = None
            continue
        if kind == "L":
            x, y = v[0] + ox, v[1] + oy
            current.append(("L", (x, y)))
        elif kind == "H":
            x = v[0] + (x if rel else 0.0)
            current.append(("L", (x, y)))
        elif kind == "V":
            y = v[0] + (y if rel else 0.0)
            current.append(("L", (x, y)))
        elif kind == "C":
            c1, c2, end = (v[0] + ox, v[1] + oy), (v[2] + ox, v[3] + oy), (v[4] + ox, v[5] + oy)
            current.append(("C", c1 + c2 + end))
            last_c, (x, y) = c2, end
            last_q = None
            continue
        elif kind == "S":
            c1 = (2 * x - last_c[0], 2 * y - last_c[1]) if last_c else (x, y)
            c2, end = (v[0] + ox, v[1] + oy), (v[2] + ox, v[3] + oy)
            current.append(("C", c1 + c2 + end))
            last_c, (x, y) = c2, end
            last_q = None
            continue
        elif kind == "Q":
            c, end = (v[0] + ox, v[1] + oy), (v[2] + ox, v[3] + oy)
            current.append(("Q", c + end))
            last_q, (x, y) = c, end
            last_c = None
            continue
        elif kind == "T":
            c = (2 * x - last_q[0], 2 * y - last_q[1]) if last_q else (x, y)
            end = (v[0] + ox, v[1] + oy)
            current.append(("Q", c + end))
            last_q, (x, y) = c, end
            last_c = None
            continue
        elif kind == "A":
            start = (x, y)
            x, y = v[5] + ox, v[6] + oy
            current.append(("A", (v[0], v[1], v[2], v[3], v[4], x, y, start[0], start[1])))
        last_c = last_q = None
    return out


def arc_points(rx, ry, rotation, large, sweep, x2, y2, x1, y1):
    """Points along an SVG arc (its endpoint form turned into its centre's, SVG's implementation notes F.6.5)."""
    if rx == 0 or ry == 0 or (x1, y1) == (x2, y2):
        return [(x2, y2)]
    phi = math.radians(rotation)
    cos, sin = math.cos(phi), math.sin(phi)
    dx, dy = (x1 - x2) / 2, (y1 - y2) / 2
    px, py = cos * dx + sin * dy, -sin * dx + cos * dy
    rx, ry = abs(rx), abs(ry)
    scale = px * px / (rx * rx) + py * py / (ry * ry)
    if scale > 1:
        rx, ry = rx * math.sqrt(scale), ry * math.sqrt(scale)
    num = rx * rx * ry * ry - rx * rx * py * py - ry * ry * px * px
    den = rx * rx * py * py + ry * ry * px * px
    root = math.sqrt(max(0.0, num / den)) * (-1 if large == sweep else 1)
    cxp, cyp = root * rx * py / ry, -root * ry * px / rx
    cx, cy = cos * cxp - sin * cyp + (x1 + x2) / 2, sin * cxp + cos * cyp + (y1 + y2) / 2
    a1 = math.atan2((py - cyp) / ry, (px - cxp) / rx)
    a2 = math.atan2((-py - cyp) / ry, (-px - cxp) / rx)
    delta = a2 - a1
    if sweep and delta < 0:
        delta += 2 * math.pi
    elif not sweep and delta > 0:
        delta -= 2 * math.pi
    points = []
    for k in range(17):
        a = a1 + delta * k / 16
        ex, ey = rx * math.cos(a), ry * math.sin(a)
        points.append((cos * ex - sin * ey + cx, sin * ex + cos * ey + cy))
    return points


def bounds(subpath):
    points = []
    for kind, v in subpath:
        if kind == "A":
            points += arc_points(*v)
        elif kind != "Z":
            points += [(v[k], v[k + 1]) for k in range(0, len(v), 2)]
    xs, ys = [p[0] for p in points], [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def drawn(subpath, centre, inset, size, places):
    """A subpath as absolute path markup, drawn in towards its centre by INSET on each side of a SIZE (width, height)."""
    (cx, cy), (width, height) = centre, size
    kx, ky = (width - 2 * inset) / width, (height - 2 * inset) / height
    n = lambda value: f"{round(value, places):g}"
    px = lambda value: n(cx + (value - cx) * kx)
    py = lambda value: n(cy + (value - cy) * ky)
    out = []
    for kind, v in subpath:
        if kind == "A":
            out.append(f"A {n(v[0] * kx)} {n(v[1] * ky)} {n(v[2])} {v[3]:g} {v[4]:g} {px(v[5])} {py(v[6])}")
        elif kind == "Z":
            out.append("Z")
        else:
            out.append(kind + " " + " ".join(px(v[k]) + " " + py(v[k + 1]) for k in range(0, len(v), 2)))
    if out[-1] != "Z":
        out.append("Z")
    return " ".join(out)


def outlined(key, data, places):
    """A badge's outline: its largest subpath again, drawn inward by RING of the badge's size, so under the even-odd rule
    the badge becomes a ring and what was cut out of it is filled; an INSIDE badge's letters drawn in past the ring."""
    data = path_data(data, places)
    subpaths = segments(data)
    edge = max(subpaths, key=lambda s: (lambda b: (b[2] - b[0]) * (b[3] - b[1]))(bounds(s)))
    left, top, right, bottom = bounds(edge)
    centre, size = ((left + right) / 2, (top + bottom) / 2), (right - left, bottom - top)
    ring = RING * max(size)
    if key not in INSIDE:
        return data + " " + drawn(edge, centre, ring, size, places)
    letters = [drawn(s, centre, ring + GAP * max(size), size, places) for s in subpaths if s is not edge]
    return " ".join([drawn(edge, centre, 0, size, places), drawn(edge, centre, ring, size, places), *letters])


def attribute(element, name):
    m = re.search(rf'\s{name}="([^"]*)"', element)
    return m.group(1) if m else None


def number(element, name, default="0"):
    return float(attribute(element, name) or default)


def figures(svg):
    """Every shape of an SVG as path markup, and its fill rule; anything moved by a transform, or shapes of two fill
    rules, ends the script, since one StreamGeometry holds one rule and no transform. Numbers keep what a 16-pixel
    icon can show: two places in Octicons' 16 box, one in the fonts' 512."""
    if re.search(r"\stransform=", svg) or re.search(r"<(use|clipPath|mask|image|text)\b", svg):
        sys.exit("file-icons: a transform or reference this script doesn't draw")
    box = re.search(r'\sviewBox="[-\d.]+ [-\d.]+ ([\d.]+)', svg) or re.search(r'\swidth="([\d.]+)', svg)
    places = 2 if box and float(box.group(1)) <= 64 else 1
    parts = []
    rules = set()
    for element in re.findall(r"<(?:path|polygon|polyline|rect|circle|ellipse)\b[^>]*>", svg):
        if re.search(r'\sfill="none"', element):
            continue
        rules.add(attribute(element, "fill-rule") or attribute(element, "clip-rule") or "nonzero")
        tag = re.match(r"<(\w+)", element).group(1)
        if tag == "path":
            parts.append(path_data(attribute(element, "d"), places))
        elif tag in ("polygon", "polyline"):
            points = NUMBER.findall(attribute(element, "points"))
            pairs = [f"{round(float(points[k]), places):g} {round(float(points[k + 1]), places):g}" for k in range(0, len(points), 2)]
            parts.append("M " + " L ".join(pairs) + " Z")
        elif tag == "rect":
            x, y, w, h = number(element, "x"), number(element, "y"), number(element, "width"), number(element, "height")
            if attribute(element, "rx") or attribute(element, "ry"):
                sys.exit("file-icons: a rounded rect")
            parts.append(f"M {x:g} {y:g} H {x + w:g} V {y + h:g} H {x:g} Z")
        else:
            cx, cy = number(element, "cx"), number(element, "cy")
            rx = number(element, "r") if tag == "circle" else number(element, "rx")
            ry = number(element, "r") if tag == "circle" else number(element, "ry")
            parts.append(f"M {cx - rx:g} {cy:g} A {rx:g} {ry:g} 0 1 0 {cx + rx:g} {cy:g} A {rx:g} {ry:g} 0 1 0 {cx - rx:g} {cy:g} Z")
    if not parts or len(rules) != 1:
        sys.exit(f"file-icons: {len(parts)} shapes in {len(rules)} fill rules")
    return " ".join(parts), "F0" if rules.pop() == "evenodd" else "F1", places


def fetch(source, name, cache):
    # Kept by the URL it came from, so a set whose pin moves is fetched again.
    pinned = hashlib.sha256(SETS[source].encode()).hexdigest()[:16]
    path = os.path.join(cache, f"{source}-{pinned}", re.sub(r"[^\w.+#-]", "_", name) + ".svg")
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with urllib.request.urlopen(SETS[source].format(urllib.parse.quote(name))) as response:
            data = response.read()
        with open(path, "wb") as out:
            out.write(data)
    with open(path, encoding="utf-8") as svg:
        return svg.read()


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    cache = os.path.join(root, "artifacts", "file-icons")
    lines = [
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "    <!-- Written by tools/file-icons.py from file-icons' icons (ISC, John Gardner), MFixx (MIT, Fizzed, Inc. and",
        "         John Gardner) and Octicons (MIT, GitHub Inc.), see THIRD_PARTY_NOTICES.md, and the few drawn there; the",
        "         solid badges drawn as outlines there. F1 is the nonzero fill rule, F0 the even-odd one. -->",
    ]
    shapes = []
    for key, source, name in ICONS:
        try:
            shapes.append((key, *figures(fetch(source, name, cache))))
        except SystemExit as stop:
            sys.exit(f"{stop} ({source} {name})")
    for key, data in OWN.items():
        if any(key == known for known, *_ in shapes):
            sys.exit(f"file-icons: {key} twice")
        shapes.append((key, data, "F1", 2))
    if missing := OUTLINED - {key for key, *_ in shapes}:
        sys.exit(f"file-icons: no {', '.join(sorted(missing))} to outline")
    for key, data, rule, places in shapes:
        if key in OUTLINED:
            data, rule = outlined(key, data, places), "F0"
        lines.append(f'    <StreamGeometry x:Key="FileIcon.{key}">{rule} {data}</StreamGeometry>')
    lines.append("</ResourceDictionary>")
    target = os.path.join(root, "src", "Passenger.App", "Theme", "FileIconSet.axaml")
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines) + "\n")
    print(f"file-icons: {len(ICONS) + len(OWN)} written to {os.path.relpath(target, root)}")


if __name__ == "__main__":
    main()
