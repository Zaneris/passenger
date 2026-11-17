#!/usr/bin/env python3
"""Writes the generated themes, <Name>.json (theme format 1, docs/themes.md), from the palettes below, into the folder it
is given: a checkout of Zaneris/passenger-themes, or the copy the tests carry (tests/Passenger.Tests/Themes).

Each theme is a few colours per variant (its grounds, words, accent and hues); every token Tokens.axaml gives is worked
out from them here, then nudged in lightness until each pair Tokens.axaml declares (read:Text, read:Mark, read:Edge)
passes, so a palette never has to be tuned token by token. The token list is read from Tokens.axaml, so a token added
there and not here fails this script rather than leaving a theme without it. Adding a theme is a palette here, its file in that folder's index
(passenger-themes.json beside them) and `just themes FOLDER`. The themes Passenger carries (BUILT_IN) are written the
same way into its built-in source, src/Passenger.App/Theme/BuiltIn, when that is the folder given.
"""
import json
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
THEME = os.path.join(ROOT, "src", "Passenger.App", "Theme")

# ---------------------------------------------------------------------------------------------------------------------
# Colour arithmetic: sRGB hex, OKLab for moving lightness while hue and chroma hold, WCAG's luminance and CIELAB's L*.


def rgb(hexcode):
    h = hexcode.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


def hexof(c):
    return "#" + "".join(f"{round(max(0, min(1, v)) * 255):02X}" for v in c)


def _lin(v):
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def _gam(v):
    return 12.92 * v if v <= 0.0031308 else 1.055 * (max(v, 0) ** (1 / 2.4)) - 0.055


def oklab(c):
    r, g, b = (_lin(v) for v in c)
    l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b
    m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b
    s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b
    l, m, s = (math.copysign(abs(x) ** (1 / 3), x) for x in (l, m, s))
    return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s)


def fromlab(lab):
    L, a, b = lab
    l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3
    m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3
    s = (L - 0.0894841775 * a - 1.2914855480 * b) ** 3
    r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s
    g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s
    bb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
    return tuple(_gam(v) for v in (r, g, bb))


def in_gamut(c):
    return all(-0.0005 <= v <= 1.0005 for v in c)


def with_lightness(hexcode, L):
    """The colour at OKLab lightness L, its hue kept and its chroma lowered only as far as sRGB needs."""
    _, a, b = oklab(rgb(hexcode))
    for _ in range(40):
        c = fromlab((L, a, b))
        if in_gamut(c):
            return hexof(c)
        a, b = a * 0.93, b * 0.93
    return hexof(fromlab((L, 0, 0)))


def lightness(hexcode):
    return oklab(rgb(hexcode))[0]


def shift(hexcode, d):
    return with_lightness(hexcode, max(0.0, min(1.0, lightness(hexcode) + d)))


def mix(one, other, t):
    """One colour t of the way to the other, in OKLab."""
    p, q = oklab(rgb(one)), oklab(rgb(other))
    return hexof(fromlab(tuple(x + (y - x) * t for x, y in zip(p, q))))


def luminance(hexcode):
    r, g, b = (_lin(v) for v in rgb(hexcode[:7]))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def over(fore, ground):
    """A colour with alpha (#AARRGGBB) drawn over a ground, as the tests compose it."""
    if len(fore) == 9:
        a = int(fore[1:3], 16) / 255
        f = rgb("#" + fore[3:])
    else:
        a, f = 1.0, rgb(fore)
    g = rgb(ground)
    return hexof(tuple(round((x * a + y * (1 - a)) * 255) / 255 for x, y in zip(f, g)))


def ratio(fore, ground):
    a, b = luminance(over(fore, ground)), luminance(ground)
    return (max(a, b) + 0.05) / (min(a, b) + 0.05)


def cielab_l(hexcode):
    y = luminance(hexcode)
    return 116 * y ** (1 / 3) - 16 if y > 216 / 24389 else y * 24389 / 27


def wash(ink, alpha):
    return f"#{alpha:02X}{ink.lstrip('#')}"


def legible(fore, grounds, least, dark):
    """The colour moved away from its grounds in lightness until it reads on each at `least`:1."""
    colour = fore
    step = 0.006 if dark else -0.006
    for _ in range(200):
        if min(ratio(colour, g) for g in grounds) >= least:
            return colour
        colour = shift(colour, step)
    raise ValueError(f"{fore} cannot reach {least}:1 on {grounds}")


# ---------------------------------------------------------------------------------------------------------------------
# The palettes. Each variant names its editor's ground and the rest; hues not used for code fall back to the ones that
# are. The values are Passenger's own, each family taking its mood from a well-liked editor theme without its colours.

THEMES = {
    "Sorbet": dict(
        about="soft pastels on a dusky plum by night and a pale lavender paper by day, a mauve accent and "
              "peach for the caret; round as a scoop, peach, mauve and mint light in its corners, its "
              "comments and parameters in italics",
        shape="soft", italic=True,
        bar=[('purple', 0.18), ('pink', 0.22)],
        badge="pink",
        braces=["green", "pink", "cyan"],
        parameter="Italic",
        fonts={"code": {"family": "Victor Mono"}, "window": {"family": "Nunito"}},
        background={"image": "pictures/sorbet.webp", "opacity": 0.22, "places": ["welcome", "frame", "toolWindows"], "fit": "cover", "light": {"opacity": 0.3}},
        layout={"open": ["explorer"], "sizes": {"left": 250}, "editorTabs": "top", "toolWindows": "icons", "closedWindows": "leave"},
        languages={"markdown": {"Heading": "pink", "Code": "cyan"}},
        dark=dict(ground="#1C1B29", text="#D2D8F2", muted="#A4AAC8", accent="#C3A2F5", glance="#F5BE8A",
                  red="#F28AA6", orange="#F7B08A", yellow="#F3DCAA", green="#A4DFA0", cyan="#92DDD1", blue="#8DB2F7",
                  purple="#C7A8F5", pink="#F2C0E2", comment="#7F84A0", member="#B6BEFA"),
        light=dict(ground="#F2F2F7", text="#474A63", muted="#5E627A", accent="#7A38DB", glance="#C9550F",
                   red="#C8163A", orange="#C45410", yellow="#A86A08", green="#3A8A26", cyan="#12827F", blue="#2160D8",
                   purple="#7A38DB", pink="#B83F95", comment="#757A90", member="#4A55C9"),
        code=dict(keyword="purple", string="green", escape="pink", number="orange", type="yellow", struct="yellow",
                  method="blue", member="member", event="pink", tag="blue", attribute="yellow", heading="red"),
    ),
    "Nocturne": dict(
        about="a violet night of hot pink keywords, lemon strings and mint calls, its colours those of "
              "Dracula Theme by the Dracula Theme team (dracula-theme.theme-dracula on Open VSX, MIT), under "
              "pill shapes, a crescent moon and stars across the Welcome screen, the frame and the tool "
              "windows, and the Explorer on the right",
        shape="pill",
        vscode={"id": "dracula-theme.theme-dracula", "theme": "Dracula Theme", "version": "2.25.1"},
        fonts={"code": {"family": "Fira Code"}},
        background={"image": "pictures/nocturne.webp", "opacity": 0.24, "places": ["welcome", "frame", "toolWindows"], "fit": "cover"},
        layout={"places": {"rightTop": ["explorer", "commit"]}, "open": ["explorer"], "sizes": {"right": 270}},
    ),
    "Saffron": dict(
        about="ink-dark slate with saffron and tangerine, sky-blue types and lime strings; by day the same on"
              " chalk. A sunburst rises behind the Welcome screen and the window's frame, and the title bar "
              "glows from ink into ember",
        shape="pill", italic=True,
        bar=[('orange', 0.12), ('yellow', 0.24)],
        badge="red",
        braces=["green", "pink", "cyan"],
        parameter="Italic",
        fonts={"code": {"family": "Fira Code"}, "window": {"family": "Fira Sans"}},
        background={"image": "pictures/saffron.webp", "opacity": 0.2, "places": ["welcome", "frame"], "fit": "cover", "align": "bottom right"},
        languages={"json": {"JsonKey": "orange"}, "xaml": {"Tag": "orange", "AttributeName": "yellow"}},
        dark=dict(ground="#0E1219", text="#C4C2BA", muted="#8E939D", accent="#E8B04D", glance="#E8B04D",
                  red="#F0717A", orange="#FF9442", yellow="#FFB65A", green="#A8D553", cyan="#95E3C8", blue="#5CBDF7",
                  purple="#D3A9FC", pink="#F29668", comment="#646B78", member="#F07A7F"),
        light=dict(ground="#FBFBFA", text="#575F66", muted="#5F676E", accent="#B86E00", glance="#B86E00",
                   red="#D2464D", orange="#D96A1E", yellow="#B0780F", green="#5E8A00", cyan="#2A9A7E", blue="#2B85CC",
                   purple="#8B5CC0", pink="#C9622E", comment="#80868D", member="#D2464D"),
        code=dict(keyword="orange", string="green", escape="cyan", number="purple", type="blue", struct="blue",
                  method="yellow", member="member", event="pink", tag="blue", attribute="yellow", heading="orange"),
    ),
    "Bonbon": dict(
        about="a cool grey night lit with candy pink, butter, mint and sky, its day a blush-white page; round"
              " and soft, with sprinkles tossed across the tool windows and the Welcome screen and the "
              "terminal open below",
        shape="soft", italic=False,
        bar=[('pink', 0.16), ('purple', 0.2)],
        badge="purple",
        braces=["green", "cyan", "orange"],
        parameter="Italic",
        fonts={"code": {"family": "JetBrains Mono", "size": 14}, "window": {"family": "Nunito"}},
        background={"image": "pictures/bonbon.webp", "opacity": 0.18, "places": ["welcome", "toolWindows"], "fit": "tile", "light": {"opacity": 0.26}},
        layout={"places": {"bottomLeft": ["terminal", "run", "build"]}, "open": ["explorer", "terminal"], "sizes": {"left": 260, "bottom": 200}, "toolWindows": "tabs", "stripeIcons": "untabbed"},
        languages={"markdown": {"Heading": "pink"}},
        dark=dict(ground="#23262D", text="#C6CCD6", muted="#9AA1AE", accent="#F27AB0", glance="#F27AB0",
                  red="#F0808C", orange="#F7B48C", yellow="#FAD48C", green="#A8E0A0", cyan="#8FE3E8", blue="#7FC4FA",
                  purple="#C3A6FF", pink="#F28BC2", comment="#727A8A", member="#F0A0A8"),
        light=dict(ground="#FCFAFB", text="#3A3D45", muted="#5D616B", accent="#C22C77", glance="#C22C77",
                   red="#C23D4D", orange="#B0581C", yellow="#9A6A00", green="#3F8A3A", cyan="#14858C", blue="#2C6FC9",
                   purple="#7A4FD0", pink="#B8287A", comment="#878C96", member="#C23D4D"),
        code=dict(keyword="pink", string="green", escape="cyan", number="orange", type="yellow", struct="yellow",
                  method="blue", member="member", event="purple", tag="pink", attribute="yellow", heading="pink"),
    ),
    "Ledger": dict(
        about="plain neutral greys with a clear blue accent, coral keywords and navy or ice strings: quiet, "
              "for long days. Dense and square-cornered on a ledger's graph paper, its tool windows laid out "
              "for the day's accounts",
        shape="square", italic=False,
        braces=["green", "cyan", "pink"],
        fonts={"code": {"family": "IBM Plex Mono", "size": 13}, "window": {"family": "IBM Plex Sans"}},
        background={"image": "pictures/ledger.webp", "opacity": 0.12, "places": ["welcome", "toolWindows"], "fit": "tile", "light": {"opacity": 0.16}},
        layout={"places": {"leftTop": ["explorer", "commit"], "leftBottom": ["structure", "bookmarks"], "bottomLeft": ["build", "run", "debug", "tests", "terminal", "find"], "bottomRight": ["problems"]}, "open": ["explorer", "structure", "build", "problems"], "sizes": {"left": 280, "bottom": 220}, "toolWindows": "tabs", "stripeIcons": "untabbed", "findResults": "window"},
        dark=dict(ground="#0E1218", text="#E4E9F0", muted="#9099A3", accent="#2468D0", glance="#4D94F7",
                  red="#FA7D74", orange="#FCA65A", yellow="#E3B341", green="#82E390", cyan="#76D3E0", blue="#7EBEFA",
                  purple="#D0AAFC", pink="#F49AC6", comment="#8F97A1", member="#7EBEFA", string="#A8D2FA"),
        light=dict(ground="#FFFFFF", text="#1E2329", muted="#59626C", accent="#1F6FD6", glance="#0B62D6",
                   red="#C9202E", orange="#8F3A06", yellow="#8A5A00", green="#196C2E", cyan="#0F6F7A", blue="#0753AA",
                   purple="#7E4DD8", pink="#B0306E", comment="#6A737D", member="#0753AA", string="#0C3570"),
        code=dict(keyword="red", string="string", escape="cyan", number="blue", type="orange", struct="orange",
                  method="purple", member="member", event="pink", tag="green", attribute="blue", heading="blue"),
    ),
    "Slate": dict(
        about="a soft blue-grey with lilac keywords, sage strings, cornflower calls and wheat types, its "
              "colours those of One Dark Pro by Binaryify (zhuangtongfa.material-theme on Open VSX, MIT), "
              "under card shapes, a survey map's contours behind the code, and the terminal and problems open"
              " below",
        shape="card",
        vscode={"id": "zhuangtongfa.material-theme", "theme": "One Dark Pro", "version": "3.20.6"},
        fonts={"code": {"family": "JetBrains Mono"}},
        background={"image": "pictures/slate.webp", "opacity": 0.1, "places": ["editor", "welcome"], "fit": "cover"},
        layout={"places": {"bottomLeft": ["terminal", "build", "run"], "bottomRight": ["problems"]}, "open": ["explorer", "terminal", "problems"], "sizes": {"left": 260, "bottom": 220}, "toolWindows": "tabs"},
    ),
    "Orbit": dict(
        about="deep space with signal teal, aqua and periwinkle and a warm sand for strings; by day a pale "
              "observatory. Orbits circle a sun off the corner of every editor, the title bar runs teal into "
              "periwinkle, and the editor's tabs stand down its side",
        shape="pill", italic=False,
        bar=[('green', 0.16), ('purple', 0.2)],
        badge="purple",
        braces=["green", "orange", "cyan"],
        parameter="Italic",
        fonts={"code": {"family": "JetBrains Mono"}, "window": {"family": "Space Grotesk"}},
        background={"image": "pictures/orbit.webp", "opacity": 0.18, "places": ["editor", "welcome", "frame"], "fit": "cover", "light": {"opacity": 0.2}},
        layout={"places": {"rightTop": ["explorer", "structure"]}, "open": ["explorer"], "sizes": {"right": 280}, "editorTabs": "left"},
        languages={"markdown": {"Heading": "green", "Code": "yellow"}},
        dark=dict(ground="#16181D", text="#EDEFF7", muted="#9A9FAD", accent="#4CEFC6", glance="#FFD18F",
                  red="#F2667F", orange="#FFB37A", yellow="#FCD196", green="#4CEFC6", cyan="#0AD6EC", blue="#58B5FC",
                  purple="#B0ACFC", pink="#F8A6C0", comment="#858A98", member="#EDEFF7"),
        light=dict(ground="#F7F8FC", text="#252833", muted="#5A5E6C", accent="#0B7F91", glance="#9A5A00",
                   red="#C2304C", orange="#A85410", yellow="#946100", green="#0E8A6E", cyan="#007E8C", blue="#1C6FC4",
                   purple="#5752C9", pink="#B03A6A", comment="#737888", member="#252833"),
        code=dict(keyword="blue", string="yellow", escape="pink", number="pink", type="purple", struct="purple",
                  method="cyan", member="green", event="orange", tag="green", attribute="blue", heading="cyan"),
    ),
    "Nightjar": dict(
        about="a navy night sky of violet keywords, apricot strings and seafoam members, its colours those of"
              " Night Owl by Sarah Drasner (sdras.night-owl on Open VSX, MIT), under card shapes, a nightjar "
              "crossing the moon on the Welcome screen and behind the tool windows, and the editor's tabs down its "
              "side",
        shape="card",
        vscode={"id": "sdras.night-owl", "theme": "Night Owl", "version": "2.1.1"},
        fonts={"code": {"family": "Victor Mono"}},
        background={"image": "pictures/nightjar.webp", "opacity": 0.16, "places": ["welcome", "toolWindows"], "fit": "cover", "align": "top right"},
        layout={"open": ["explorer"], "editorTabs": "left", "sizes": {"left": 250}},
    ),
    "Moss": dict(
        about="a green-grey woodland, sage and leaf with honey strings and clay types, easy on the eye night "
              "and day; ferns grow from the editor's foot, and it opens calm, the Explorer alone",
        shape="card", italic=True,
        bar=[('green', 0.14), ('cyan', 0.16)],
        badge="cyan",
        braces=["orange", "cyan", "pink"],
        parameter="Italic",
        fonts={"code": {"family": "Source Code Pro"}, "window": {"family": "Source Sans 3"}},
        background={"image": "pictures/moss.webp", "opacity": 0.13, "places": ["editor", "welcome", "toolWindows"], "fit": "cover", "align": "bottom", "light": {"opacity": 0.18}},
        layout={"open": ["explorer"], "sizes": {"left": 280}, "closedWindows": "leave", "findResults": "popup"},
        dark=dict(ground="#1D2321", text="#D3DCD5", muted="#9AA69E", accent="#8DBF7E", glance="#E7C77A",
                  red="#E5847A", orange="#E9A97D", yellow="#E5C890", green="#9CCB86", cyan="#86CBB8", blue="#8EC6D8",
                  purple="#C2A6D8", pink="#D5A0D0", comment="#77857D", member="#B9D3C0"),
        light=dict(ground="#F7F8F3", text="#2C3530", muted="#56605A", accent="#4E7D3C", glance="#8C6418",
                   red="#B5463A", orange="#A2551B", yellow="#8C6418", green="#4C7A33", cyan="#2A7A68", blue="#2B6E85",
                   purple="#7A4F9A", pink="#8B428A", comment="#6E7A72", member="#3F6B57"),
        code=dict(keyword="green", string="yellow", escape="cyan", number="pink", type="orange", struct="orange",
                  method="blue", member="member", event="purple", tag="green", attribute="yellow", heading="green"),
    ),
    "Phosphor": dict(
        about="a near-black terminal glass with electric lime, cyan and violet; by day crisp ink on white. "
              "Sharp-cornered, a screen's scanlines behind the code and a lime glow round its popups by "
              "night, the terminal open below",
        shape="glass", italic=False,
        glow="0 0 22 0 #4D9FE04A",
        braces=["purple", "orange", "pink"],
        fonts={"code": {"family": "Cascadia Code", "size": 14}, "window": {"family": "Cascadia Mono"}},
        background={"image": "pictures/phosphor.webp", "opacity": 0.08, "places": ["editor", "toolWindows", "frame"], "fit": "tile", "light": "none"},
        layout={"places": {"bottomLeft": ["terminal", "run", "build", "debug"], "bottomRight": ["problems"]}, "open": ["explorer", "terminal"], "sizes": {"bottom": 300}, "toolWindows": "tabs"},
        dark=dict(ground="#121518", text="#DDE3E6", muted="#929BA3", accent="#B8EE52", glance="#C8F560",
                  red="#FF6F7D", orange="#FF9E64", yellow="#F2D35E", green="#C8F560", cyan="#73D8FF", blue="#7AA8FF",
                  purple="#B49CFF", pink="#FF7FB0", comment="#6A7480", member="#9FE8D8"),
        light=dict(ground="#FCFCFB", text="#16191C", muted="#555D64", accent="#3D7A12", glance="#3D7A12",
                   red="#C42A3A", orange="#B5530F", yellow="#8A6A00", green="#4A7A00", cyan="#0A6FA0", blue="#2F5FD0",
                   purple="#6544CC", pink="#B8236A", comment="#6F7780", member="#0B7766"),
        code=dict(keyword="cyan", string="green", escape="yellow", number="pink", type="purple", struct="purple",
                  method="orange", member="member", event="yellow", tag="cyan", attribute="purple", heading="green"),
    ),
}

# The themes Passenger carries beside its own, each with its fonts and default layout (docs/themes.md) as well.
BUILT_IN = {
    "Cubicle": dict(
        about="calm and dense for long working days: square corners, tight margins and a clear blue accent on cool greys, "
              "the explorer docked right and the build's output and the problems below",
        shape="square", italic=False,
        fonts={"code": {"size": 12}},
        layout={"places": {"rightTop": ["explorer", "commit"], "rightBottom": ["structure", "bookmarks"],
                           "bottomLeft": ["build", "run", "debug", "tests", "terminal", "find"], "bottomRight": ["problems"]},
                "open": ["explorer", "build", "problems"], "sizes": {"left": 260, "right": 280, "bottom": 200},
                "editorTabs": "top", "toolWindows": "tabs", "stripeIcons": "untabbed", "closedWindows": "leave", "findResults": "window"},
        dark=dict(ground="#1E1F24", text="#DCDEE3", muted="#A0A5AF", accent="#2D70D0", glance="#6AAAF5",
                  red="#F07B7B", orange="#E8A272", yellow="#E6D9A2", green="#8CC97E", cyan="#62C9B5", blue="#7AAEEB",
                  purple="#C6A0E6", pink="#E094C4", comment="#6EAA62", member="#A9CFF2", string="#E0A584",
                  number="#B9D9A4", method="#E6DCAB"),
        light=dict(ground="#FBFCFE", text="#1E2228", muted="#575E68", accent="#1F66C9", glance="#1F66C9",
                   red="#C22B2B", orange="#A84A12", yellow="#806000", green="#2E7D32", cyan="#127A86", blue="#1A55C2",
                   purple="#7442B8", pink="#A6327E", comment="#3E7B39", member="#22548F", string="#A3361B",
                   number="#8A2F9E", method="#6E4E00"),
        code=dict(keyword="blue", string="string", escape="yellow", number="number", type="cyan", struct="cyan",
                  method="method", member="member", event="pink", tag="blue", attribute="cyan", heading="blue"),
    ),
}

# The shapes a theme draws its own way: Passenger's pills and crowned popups, Harbour's cards joined to the editor,
# Cubicle's square, dense frame, its open tab joined to the editor under a line of blue, soft round sweets, or a terminal's
# sharp glass, its open tab underlined; and its margins, which the square and glass tighten and the soft loosen.
SHAPES = {
    "pill": [("CornerRadius", "Passenger.ControlRadius", "6"), ("CornerRadius", "Passenger.ToolRadius", "8"),
             ("CornerRadius", "Passenger.PopupRadius", "10"), ("CornerRadius", "Passenger.CardRadius", "10"),
             ("CornerRadius", "Passenger.BadgeRadius", "6"), ("CornerRadius", "Passenger.ChipRadius", "10"),
             ("x:Double", "Passenger.TabStripHeight", "38"), ("Thickness", "Passenger.TabMargin", "2,5"),
             ("Thickness", "Passenger.TabPadding", "10,0,6,0"), ("CornerRadius", "Passenger.TabRadius", "7"),
             ("BoxShadows", "Passenger.TabShadow", "0 1 2 0 #1F000000"), ("Thickness", "Passenger.PopupEdge", "1,3,1,1"),
             ("x:Double", "Passenger.EditorTextPadding", "6"), ("x:Double", "Passenger.ToolHeaderHeight", "38"),
             ("Thickness", "Passenger.ToolTitleMargin", "14,0,0,0"), ("Thickness", "Passenger.DialogFooterPadding", "16,10")],
    "card": [("CornerRadius", "Passenger.ControlRadius", "8"), ("CornerRadius", "Passenger.ToolRadius", "8"),
             ("CornerRadius", "Passenger.PopupRadius", "12"), ("CornerRadius", "Passenger.CardRadius", "14"),
             ("CornerRadius", "Passenger.BadgeRadius", "99"), ("CornerRadius", "Passenger.ChipRadius", "99"),
             ("x:Double", "Passenger.TabStripHeight", "40"), ("Thickness", "Passenger.TabMargin", "1,6,1,0"),
             ("Thickness", "Passenger.TabPadding", "14,0,8,0"), ("CornerRadius", "Passenger.TabRadius", "10,10,0,0"),
             ("BoxShadows", "Passenger.TabShadow", "0 0 0 0 #00000000"), ("Thickness", "Passenger.PopupEdge", "1"),
             ("x:Double", "Passenger.EditorTextPadding", "6"), ("x:Double", "Passenger.ToolHeaderHeight", "38"),
             ("Thickness", "Passenger.ToolTitleMargin", "14,0,0,0"), ("Thickness", "Passenger.DialogFooterPadding", "16,10")],
    "soft": [("CornerRadius", "Passenger.ControlRadius", "10"), ("CornerRadius", "Passenger.ToolRadius", "12"),
             ("CornerRadius", "Passenger.PopupRadius", "16"), ("CornerRadius", "Passenger.CardRadius", "18"),
             ("CornerRadius", "Passenger.BadgeRadius", "99"), ("CornerRadius", "Passenger.ChipRadius", "99"),
             ("x:Double", "Passenger.TabStripHeight", "40"), ("Thickness", "Passenger.TabMargin", "3,6"),
             ("Thickness", "Passenger.TabPadding", "14,0,8,0"), ("CornerRadius", "Passenger.TabRadius", "99"),
             ("BoxShadows", "Passenger.TabShadow", "0 2 6 0 #26000000"), ("Thickness", "Passenger.PopupEdge", "1"),
             ("x:Double", "Passenger.EditorTextPadding", "8"), ("x:Double", "Passenger.ToolHeaderHeight", "40"),
             ("Thickness", "Passenger.ToolTitleMargin", "16,0,0,0"), ("Thickness", "Passenger.DialogFooterPadding", "18,12")],
    "glass": [("CornerRadius", "Passenger.ControlRadius", "0"), ("CornerRadius", "Passenger.ToolRadius", "0"),
              ("CornerRadius", "Passenger.PopupRadius", "0"), ("CornerRadius", "Passenger.CardRadius", "0"),
              ("CornerRadius", "Passenger.BadgeRadius", "0"), ("CornerRadius", "Passenger.ChipRadius", "0"),
              ("x:Double", "Passenger.TabStripHeight", "32"), ("Thickness", "Passenger.TabMargin", "0"),
              ("Thickness", "Passenger.TabPadding", "12,0,8,0"), ("CornerRadius", "Passenger.TabRadius", "0"),
              ("BoxShadows", "Passenger.TabShadow", "inset 0 -2 0 0 #FF79B83A"), ("Thickness", "Passenger.PopupEdge", "1"),
              ("x:Double", "Passenger.EditorTextPadding", "6"), ("x:Double", "Passenger.ToolHeaderHeight", "32"),
              ("Thickness", "Passenger.ToolTitleMargin", "10,0,0,0"), ("Thickness", "Passenger.DialogFooterPadding", "12,8")],
    "square": [("CornerRadius", "Passenger.ControlRadius", "2"), ("CornerRadius", "Passenger.ToolRadius", "2"),
               ("CornerRadius", "Passenger.PopupRadius", "2"), ("CornerRadius", "Passenger.CardRadius", "2"),
               ("CornerRadius", "Passenger.BadgeRadius", "2"), ("CornerRadius", "Passenger.ChipRadius", "2"),
               ("x:Double", "Passenger.TabStripHeight", "30"), ("Thickness", "Passenger.TabMargin", "0"),
               ("Thickness", "Passenger.TabPadding", "10,0,6,0"), ("CornerRadius", "Passenger.TabRadius", "0"),
               ("BoxShadows", "Passenger.TabShadow", "inset 0 2 0 0 #FF2F74D6"), ("Thickness", "Passenger.PopupEdge", "1"),
               ("x:Double", "Passenger.EditorTextPadding", "4"), ("x:Double", "Passenger.ToolHeaderHeight", "30"),
               ("Thickness", "Passenger.ToolTitleMargin", "10,0,0,0"), ("Thickness", "Passenger.DialogFooterPadding", "12,8")],
}

# Kinds of code drawn in the code's plain words unless a palette's code roles name a hue for one (parameter, local, ...).
PLAIN_KINDS = ["Parameter", "Local", "Namespace", "Operator"]

SYNTAX_KINDS = ["Keyword", "ControlKeyword", "Comment", "DocComment", "String", "StringEscape", "Number",
                "Preprocessor", "Class", "Interface", "Struct", "Enum", "Delegate", "TypeParameter", "Method",
                "Property", "Event", "Field", "Constant", "EnumMember", "ErrorOutput", "UserInput", "Link",
                "BuildError", "BuildWarning", "Brace1", "Brace2", "Brace3", "Tag", "AttributeName", "AttributeValue",
                "Entity", "JsonKey", "YamlKey", "Heading", "Code", "Quote", "LinkDestination"]


# ---------------------------------------------------------------------------------------------------------------------
# Every token from a variant's palette.

def file_icons(hue, text, muted, panel, dark):
    """The file icons' colours (Passenger.FileIcon.*): file-icons' ten hues in a theme's own, each a mark that reads on
    the Explorer's ground."""
    icons = {name.capitalize(): hue(name) for name in ("red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink")}
    icons["Maroon"] = shift(mix(hue("red"), hue("orange"), 0.45), -0.08)
    icons["Grey"] = mix(text, muted, 0.8)
    return {f"Passenger.FileIcon.{name}": legible(colour, [panel], 3.1, dark) for name, colour in icons.items()}


def variant(theme, p, dark):
    t = {}
    hue = lambda name: p.get(name) or p[{"string": "green", "member": "blue"}.get(name, name)]
    text, muted = p["text"], p["muted"]

    # Grounds, darkest first (dark: frame, editor, tool windows; light: frame, tool windows, editor), each a step apart.
    editor = p["ground"]
    if dark:
        frame, panel = shift(editor, -0.04), shift(editor, 0.035)
        field, lst = shift(editor, -0.012), shift(editor, -0.02)
        button, popup = shift(panel, 0.03), shift(panel, 0.025)
        divider = frame
    else:
        frame, panel = shift(editor, -0.075), shift(editor, -0.03)
        field = lst = button = popup = shift(editor, 0.02) if lightness(editor) < 0.98 else editor
        divider = shift(frame, -0.04)
    for a, b in ((frame, panel), (panel, editor), (frame, editor)):
        assert abs(cielab_l(a) - cielab_l(b)) >= 2.5, f"{theme['name']}: grounds {a} and {b} are too close in lightness"

    ink = "#FFFFFF" if dark else "#000000"
    hover, selected = (wash("#" + mix(text, ink, 0.4)[1:], 0x14), wash("#" + mix(text, ink, 0.4)[1:], 0x24)) if dark \
        else (wash(text, 0x12), wash(text, 0x21))

    accent = p["accent"]
    white, black = "#FFFFFF", shift(frame, -0.03) if dark else "#141414"
    accent_text = white if ratio(white, accent) >= ratio(black, accent) else black
    accent = legible(accent, [accent_text], 4.6, accent_text != white)
    accent = legible(accent, [panel], 3.05, dark)
    accent_hover = shift(accent, 0.04 if accent_text != white else -0.04)
    row = mix(panel, accent, 0.24 if dark else 0.16)
    text_grounds = [panel, frame, field, popup, lst, over(hover, panel), over(hover, frame), over(selected, panel), row,
                    editor, button]
    glance = p["glance"]

    t["Passenger.Frame"] = frame
    t["Passenger.Panel"] = panel
    t["Passenger.Field"] = field
    edge = mix(panel, text, 0.2)
    t["Passenger.FieldBorder"] = legible(edge, [field, panel, popup], 3.05, dark)
    t["Passenger.List"] = lst
    t["Passenger.Button"] = button
    t["Passenger.Popup"] = popup
    t["Passenger.PopupBorder"] = mix(popup, text, 0.16)
    t["Passenger.PopupShadow"] = theme.get("glow") if dark and "glow" in theme else "0 6 20 0 #80000000" if dark else "0 6 20 0 #2E000000"
    second = hue("cyan") if hue("cyan") != accent else hue("blue")
    t["Passenger.Popup.Edge"] = ("gradient", accent, mix(second, accent, 0.2)) if theme["shape"] == "pill" \
        else t["Passenger.PopupBorder"]
    banner = mix(editor, accent, 0.12)
    text_grounds.append(banner)
    t["Passenger.Banner"] = banner
    t["Passenger.DropZone"] = ("opacity", accent, "0.22" if dark else "0.14")

    t["Passenger.Divider"] = divider
    t["Passenger.Separator"] = mix(panel, text, 0.1)
    t["Passenger.StripeSeparator"] = mix(frame, text, 0.22)
    t["Passenger.WindowEdge"] = edge

    tile = mix(editor, panel, 0.5)
    text_grounds.append(tile)
    t["Passenger.Text"] = legible(text, text_grounds, 4.6, dark)
    t["Passenger.TextMuted"] = legible(muted, text_grounds, 4.6, dark)
    t["Passenger.TextDisabled"] = mix(panel, text, 0.36)
    t["Passenger.DisabledBorder"] = t["Passenger.Separator"]
    marks = [panel, frame, popup, editor]
    t["Passenger.Icon"] = legible(mix(text, muted, 0.5), marks, 3.1, dark)
    link = legible(hue("blue"), marks, 4.6, dark)
    t["Passenger.Link"] = link
    t["Passenger.LinkHover"] = legible(shift(link, 0.06 if dark else -0.06), [panel], 4.6, dark)
    t["Passenger.AccentText"] = accent_text
    t["Passenger.BadgeText"] = field if dark else "#FFFFFF"
    t["Passenger.Error"] = legible(hue("red"), marks, 4.6, dark)
    t["Passenger.Warning"] = legible(hue("yellow") if dark else hue("orange"), marks, 4.6, dark)
    selection = mix(editor, hue("blue"), 0.3 if dark else 0.22)
    t["Passenger.TextSelection"] = legible(selection, [t["Passenger.Text"]], 4.6, not dark)

    t["Passenger.Hover"] = hover
    t["Passenger.Selected"] = selected
    t["Passenger.RowSelected"] = row
    t["Passenger.Accent"] = accent
    t["Passenger.AccentHover"] = accent_hover
    t["Passenger.Focus"] = legible(glance, marks + [field], 3.1, dark)
    t["Passenger.CheckBorder"] = legible(mix(panel, text, 0.45), [panel, popup, lst], 3.05, dark)
    t["Passenger.ScrollThumb"] = "#59808080" if dark else "#33000000"
    t["Passenger.ScrollThumbHover"] = "#8C808080" if dark else "#80000000"
    t["Passenger.ScrollTrackHover"] = "#1A808080"
    t["Passenger.Menu.Selected"] = accent
    t["Passenger.Menu.SelectedText"] = accent_text
    t["Passenger.Menu.SelectedKeys"] = accent_text

    # The title bar is the frame, or a sweep from it into the theme's hues, its words the window's own.
    if "bar" in theme:
        stops = [mix(frame, hue(name), share * (1 if dark else 0.7)) for name, share in theme["bar"]]
        bar_hover, bar_pressed = ("#1FFFFFFF", "#33FFFFFF") if dark else ("#14000000", "#24000000")
        bars = stops + [over(w, s) for w in (bar_hover, bar_pressed) for s in stops]
        t["Passenger.TitleBar"] = ("gradient", stops[0], stops[-1])
    else:
        bar_hover, bar_pressed = (shift(frame, 0.05), shift(frame, 0.08)) if dark else (shift(frame, -0.05), shift(frame, -0.08))
        bars = [frame, bar_hover, bar_pressed]
        t["Passenger.TitleBar"] = frame
    t["Passenger.TitleBar.Text"] = legible(text, bars, 4.6, dark)
    t["Passenger.TitleBar.Icon"] = legible(t["Passenger.Icon"], bars[:2], 3.1, dark)
    t["Passenger.TitleBar.Hover"] = bar_hover
    t["Passenger.TitleBar.Pressed"] = bar_pressed
    t["Passenger.TitleBar.Rule"] = t["Passenger.StripeSeparator"]
    t["Passenger.TitleBar.Run"] = legible(hue("green"), bars[:2], 3.1, dark)
    t["Passenger.TitleBar.Stop"] = legible(hue("red"), bars[:2], 3.1, dark)
    t["Passenger.TitleBar.HotReload"] = legible(hue("orange"), bars[:2], 3.1, dark)
    t["Passenger.TitleBar.Focus"] = legible(glance, bars, 3.1, dark)
    t["Passenger.TitleBar.Badge"] = accent
    t["Passenger.TitleBar.BadgeText"] = accent_text
    t["Passenger.WindowButton.Hover"] = "#1AFFFFFF" if dark else "#1A000000"
    t["Passenger.WindowButton.Pressed"] = "#33FFFFFF" if dark else "#33000000"
    t["Passenger.WindowButton.CloseHover"] = "#E81123"
    t["Passenger.WindowButton.ClosePressed"] = "#F1707A"
    t["Passenger.WindowButton.CloseGlyph"] = "#FFFFFF"

    stripe = mix(frame, accent, 0.28 if dark else 0.2)
    t["Passenger.Stripe.Selected"] = stripe
    t["Passenger.Stripe.SelectedIcon"] = legible(mix(text, accent, 0.55), [stripe, row], 3.1, dark)
    tab = editor if theme["shape"] in ("card", "square") else mix(panel, accent, 0.14 if dark else 0.0)
    if not dark and theme["shape"] == "pill":
        tab = editor
    t["Passenger.Tab.Active"] = tab
    t["Passenger.Tab.ActiveText"] = legible(text, [tab], 4.6, dark)
    t["Passenger.Modified"] = legible(glance, [frame, panel], 3.1, dark)

    t["Passenger.Welcome.Tile"] = tile
    t["Passenger.Welcome.TileBorder"] = mix(tile, text, 0.1) if theme["shape"] in ("pill", "square") else tile
    t["Passenger.Welcome.TileShadow"] = "0 0 0 0 #00000000" if theme["shape"] in ("pill", "square") \
        else ("0 2 10 0 #59000000" if dark else "0 2 10 0 #1F000000")
    t["Passenger.Welcome.Chip"] = row
    t["Passenger.Welcome.ChipIcon"] = t["Passenger.Stripe.SelectedIcon"]
    # The project's badge sweeps from the accent into another of the theme's hues, its letters reading on both.
    if "badge" in theme:
        other = legible(mix(accent, hue(theme["badge"]), 0.6), [accent_text], 4.6, accent_text != white)
        t["Passenger.Welcome.Badge"] = ("gradient", accent, other)
    else:
        t["Passenger.Welcome.Badge"] = accent

    t["Passenger.Run"] = legible(hue("green"), [panel, frame], 3.1, dark)
    t["Passenger.HotReload"] = legible(hue("orange"), [panel, frame], 3.1, dark)
    t["Passenger.Test.Passed"] = t["Passenger.Run"]
    t["Passenger.Test.Failed"] = legible(hue("red"), [panel], 3.1, dark)
    t["Passenger.Test.Skipped"] = legible(mix(muted, panel, 0.15), [panel], 3.1, dark)
    t["Passenger.Test.Running"] = legible(hue("blue"), [panel], 3.1, dark)
    t["Passenger.Test.NotRun"] = t["Passenger.CheckBorder"]

    t["Passenger.Vcs.Added"] = legible(hue("green"), [editor], 3.1, dark)
    t["Passenger.Vcs.Modified"] = legible(hue("blue"), [editor], 3.1, dark)
    t["Passenger.Vcs.Deleted"] = legible(mix(muted, panel, 0.15), [editor], 3.1, dark)
    t["Passenger.Vcs.Conflict"] = legible(hue("red"), [editor], 3.1, dark)
    files = [panel, row]
    t["Passenger.Vcs.File.Modified"] = legible(hue("blue"), files, 4.6, dark)
    t["Passenger.Vcs.File.Added"] = legible(hue("green"), files, 4.6, dark)
    t["Passenger.Vcs.File.Deleted"] = legible(mix(muted, panel, 0.25), files, 4.6, dark)
    t["Passenger.Vcs.File.Unversioned"] = legible(hue("orange"), files, 4.6, dark)
    t["Passenger.Vcs.File.Conflicted"] = legible(hue("red"), files, 4.6, dark)
    for i, name in enumerate(["blue", "green", "yellow", "purple", "red", "cyan"]):
        t[f"Passenger.Graph.{i}"] = legible(hue(name), [panel], 2.2, dark)
    t["Passenger.Vcs.Ref"] = mix(panel, text, 0.1)
    t["Passenger.Vcs.RefText"] = legible(mix(text, muted, 0.5), [t["Passenger.Vcs.Ref"]], 4.6, dark)
    t["Passenger.Vcs.Favorite"] = legible(hue("yellow"), [panel], 3.1, dark)
    t["Passenger.Vcs.Signature.Good"] = t["Passenger.Run"]
    t["Passenger.Vcs.Signature.Unverified"] = t["Passenger.Test.Skipped"]
    t["Passenger.Vcs.Signature.Bad"] = t["Passenger.Test.Failed"]
    t["Passenger.Vcs.Signature.Expired"] = t["Passenger.Vcs.Favorite"]

    t["Passenger.Picker.RingInner"] = "#FFFFFF"
    t["Passenger.Picker.RingOuter"] = "#000000"
    t["Passenger.Picker.Checker"] = "#CCCCCC"

    # The terminal on the editor's ground, its ANSI colours the palette's hues, bright ones a step further.
    t["Passenger.Terminal.Foreground"] = legible(text, [editor], 4.6, dark)
    t["Passenger.Terminal.Background"] = editor
    t["Passenger.Terminal.Cursor"] = legible(glance, [editor], 3.1, dark)
    t["Passenger.Terminal.Selection"] = legible(selection, [t["Passenger.Terminal.Foreground"]], 4.6, not dark)
    ansi = ["red", "green", "yellow", "blue", "purple", "cyan"]
    up = 0.07 if dark else -0.07
    t["Passenger.Terminal.Ansi0"] = mix(editor, text, 0.2) if dark else shift(text, -0.1)
    for i, name in enumerate(ansi, 1):
        t[f"Passenger.Terminal.Ansi{i}"] = hue(name) if dark else legible(hue(name), [editor], 3.0, dark)
        t[f"Passenger.Terminal.Ansi{i + 8}"] = shift(t[f"Passenger.Terminal.Ansi{i}"], up * (1 if dark else -0.6))
    t["Passenger.Terminal.Ansi7"] = mix(muted, text, 0.3)
    t["Passenger.Terminal.Ansi8"] = mix(editor, text, 0.42)
    t["Passenger.Terminal.Ansi15"] = shift(text, 0.08) if dark else mix(editor, text, 0.32)

    t["Passenger.File.Folder"] = legible(mix(text, muted, 0.6), [panel], 3.1, dark)
    t["Passenger.File.SolutionFolder"] = legible(hue("purple"), [panel], 3.1, dark)
    t["Passenger.File.Plain"] = t["Passenger.File.Folder"]
    t.update(file_icons(hue, text, muted, panel, dark))

    t["Passenger.Editor.Background"] = editor
    t["Passenger.Editor.Gutter"] = editor
    t["Passenger.Editor.PopupBorder"] = edge

    # The editor's own colour scheme.
    current = shift(editor, 0.025 if dark else -0.018)
    execution = mix(editor, hue("blue"), 0.18)
    code_grounds = [editor, current]
    t["Passenger.Editor.Text"] = legible(text, [editor, current, selection, execution], 4.6, dark)
    t["Passenger.Editor.LineNumber"] = legible(mix(muted, editor, 0.25), [editor], 4.6, dark)
    t["Passenger.Editor.LineNumberCurrent"] = legible(glance, [editor], 4.6, dark)
    t["Passenger.Editor.CurrentLine"] = current
    t["Passenger.Editor.Selection"] = selection
    t["Passenger.Editor.SelectionInactive"] = mix(editor, text, 0.08)
    t["Passenger.Editor.Caret"] = legible(glance, code_grounds, 3.1, dark)
    t["Passenger.Editor.SearchMatch"] = mix(editor, hue("yellow"), 0.18 if dark else 0.28)
    t["Passenger.Editor.SearchCurrent"] = mix(editor, hue("yellow"), 0.45 if dark else 0.55)
    # A problem's squiggle is a mark on the code, and a count of them in the gutter carries a badge's letters.
    badge = t["Passenger.BadgeText"]
    t["Passenger.Editor.Error"] = legible(legible(hue("red"), [editor], 3.1, dark), [badge], 4.6, badge != "#FFFFFF")
    t["Passenger.Editor.Warning"] = legible(legible(hue("yellow") if dark else hue("orange"), [editor], 3.1, dark), [badge], 4.6,
                                            badge != "#FFFFFF")
    t["Passenger.Editor.Info"] = legible(muted, [editor], 3.1, dark)
    t["Passenger.Editor.Breakpoint"] = legible(hue("red"), [editor], 3.1, dark)
    t["Passenger.Editor.BreakpointLine"] = mix(editor, hue("red"), 0.12)
    t["Passenger.Editor.BreakpointMuted"] = t["Passenger.Icon"]
    t["Passenger.Editor.Tracepoint"] = legible(hue("yellow"), [editor], 3.1, dark)
    t["Passenger.Editor.ExecutionLine"] = execution
    t["Passenger.Editor.ExecutionPoint"] = legible(glance, [editor], 3.1, dark)
    t["Passenger.Editor.ExceptionBack"] = mix(editor, hue("red"), 0.18)
    t["Passenger.Editor.InlineValue"] = legible(muted, [editor, execution], 4.6, dark)
    hint_back = mix(editor, text, 0.06)
    t["Passenger.Editor.InlayHint"] = legible(muted, [hint_back], 4.6, dark)
    t["Passenger.Editor.InlayHintBack"] = hint_back
    t["Passenger.Editor.UsageRead"] = mix(editor, hue("blue"), 0.14)
    t["Passenger.Editor.UsageWrite"] = mix(editor, hue("pink"), 0.14)
    t["Passenger.Editor.UsageReadMark"] = hue("blue")
    t["Passenger.Editor.UsageWriteMark"] = hue("pink")
    t["Passenger.Editor.MatchedBrace"] = mix(editor, hue("cyan"), 0.2)
    t["Passenger.Editor.SmartStepTarget"] = mix(editor, hue("purple"), 0.2)
    t["Passenger.Editor.SmartStepChosen"] = mix(editor, hue("purple"), 0.38)
    t["Passenger.Editor.SmartStepBorder"] = legible(hue("purple"), [editor], 3.1, dark)
    t["Passenger.Editor.Whitespace"] = mix(editor, text, 0.2)
    t["Passenger.Editor.SoftWrapSign"] = mix(editor, text, 0.38)
    t["Passenger.Editor.LinkUnderline"] = hue("blue")
    t["Passenger.Editor.Bookmark"] = legible(hue("blue"), [editor], 3.1, dark)
    for name, tone in (("Added", "green"), ("Modified", "blue"), ("Deleted", None), ("Conflict", "red")):
        other = hue(tone) if tone else text
        t[f"Passenger.Diff.{name}"] = mix(editor, other, (0.15 if tone else 0.08) if dark else (0.14 if tone else 0.07))
        t[f"Passenger.Diff.{name}Word"] = mix(editor, other, (0.32 if tone else 0.18) if dark else (0.3 if tone else 0.16))

    # A wash under the code gives way where the code's words would not read on it.
    for under_code in ["SelectionInactive", "SearchMatch", "SearchCurrent", "BreakpointLine", "ExceptionBack", "UsageRead", "UsageWrite",
                  "MatchedBrace", "SmartStepTarget", "SmartStepChosen"]:
        t[f"Passenger.Editor.{under_code}"] = legible(t[f"Passenger.Editor.{under_code}"], [t["Passenger.Editor.Text"]], 4.6, not dark)
    for name in ("Added", "Modified", "Deleted", "Conflict"):
        for key in (f"Passenger.Diff.{name}", f"Passenger.Diff.{name}Word"):
            t[key] = legible(t[key], [t["Passenger.Editor.Text"]], 4.6, not dark)

    roles = theme["code"]
    pick = lambda role: hue(roles.get(role, role))
    syntax = {
        "Keyword": pick("keyword"), "ControlKeyword": pick("keyword"), "Comment": hue("comment"),
        "DocComment": mix(hue("comment"), pick("type"), 0.35), "String": pick("string"), "StringEscape": pick("escape"),
        "Number": pick("number"), "Preprocessor": mix(muted, text, 0.2), "Class": pick("type"),
        "Interface": pick("type"), "Struct": mix(pick("struct"), text, 0.25), "Enum": mix(pick("struct"), text, 0.25),
        "Delegate": mix(pick("struct"), text, 0.25), "TypeParameter": pick("type"), "Method": pick("method"),
        "Property": pick("member"), "Event": pick("event"), "Field": pick("member"), "Constant": pick("number"),
        "EnumMember": pick("member"), "ErrorOutput": hue("red"), "UserInput": hue("green"), "Link": hue("blue"),
        "BuildError": hue("red"), "BuildWarning": hue("yellow") if dark else hue("orange"),
        "Brace1": hue("yellow"), "Brace2": hue("purple"), "Brace3": hue("blue"), "Tag": pick("tag"),
        "AttributeName": pick("attribute"), "AttributeValue": pick("string"), "Entity": pick("escape"),
        "JsonKey": pick("member"), "YamlKey": pick("keyword"), "Heading": pick("heading"), "Code": pick("string"),
        "Quote": hue("comment"), "LinkDestination": hue("blue"),
    }
    for kind in SYNTAX_KINDS:
        t[f"Passenger.Syntax.{kind}"] = legible(syntax[kind], code_grounds, 4.6, dark)
    for kind in PLAIN_KINDS:
        role = roles.get(kind.lower())
        t[f"Passenger.Syntax.{kind}"] = legible(hue(role), code_grounds, 4.6, dark) if role else t["Passenger.Editor.Text"]
    t["Passenger.Syntax.Excluded"] = mix(editor, text, 0.35)
    style = "Italic" if theme["italic"] else "Normal"
    t["Passenger.SyntaxStyle.Comment"] = ("string", style)
    t["Passenger.SyntaxStyle.DocComment"] = ("string", style)
    # The fourth to sixth brace colours, a parameter's style, and the code's colours in a language over the theme's own.
    for level, name in enumerate(theme.get("braces", []), 4):
        t[f"Passenger.Syntax.Brace{level}"] = legible(hue(name), code_grounds, 4.6, dark)
    if "parameter" in theme:
        t["Passenger.SyntaxStyle.Parameter"] = ("string", theme["parameter"])
    t["languages"] = {language: {f"Syntax.{kind}": legible(hue(name), code_grounds, 4.6, dark) for kind, name in kinds.items()}
                      for language, kinds in theme.get("languages", {}).items()}
    check(theme["name"], "Dark" if dark else "Light", t)
    return t


def solid(t, key):
    v = t[key]
    if isinstance(v, tuple):
        return [v[1], v[2]] if v[0] == "gradient" else [v[1]]
    return [v]


def declared_pairs():
    """What each colour is read on, as Tokens.axaml declares it on its Dark dictionary's colours (read:Text at 4.5:1,
    read:Mark and read:Edge at 3:1, a ground laid over another named Ground/Under, else over Panel), the fourth to sixth
    braces read as the first is: the pairs UiRulesTests holds every theme to."""
    import xml.etree.ElementTree as ElementTree
    x, read = "{http://schemas.microsoft.com/winfx/2006/xaml}", "{urn:passenger:contrast}"
    root = ElementTree.parse(os.path.join(THEME, "Tokens.axaml")).getroot()
    dark = next(d for d in root.iter() if d.get(x + "Key") == "Dark")
    pairs = []
    for colour in dark:
        key = colour.get(x + "Key", "")[len("Passenger."):]
        for kind, least in (("Text", 4.5), ("Mark", 3), ("Edge", 3)):
            for ground in (colour.get(read + kind) or "").split():
                laid, _, under = ground.partition("/")
                pairs.append((key, laid, under or "Panel", least))
    braces = [p for p in pairs if p[0] == "Syntax.Brace1"]
    return pairs + [(f"Syntax.Brace{level}",) + p[1:] for level in (4, 5, 6) for p in braces]


PAIRS = declared_pairs()


def check(name, which, t):
    """UiRulesTests' WCAG pairs, so a palette that cannot meet them fails here rather than in the tests."""
    below = []
    for fore, ground, under, least in PAIRS:
        if "Passenger." + fore not in t:
            continue
        worst = min(ratio(f, over(g, u)) for f in solid(t, "Passenger." + fore) for g in solid(t, "Passenger." + ground)
                    for u in solid(t, "Passenger." + under))
        if worst < least:
            below.append(f"{name} {which}: {fore} on {ground} is {worst:.2f}:1, under {least}:1")
    if below:
        raise SystemExit("\n".join(below))


# ---------------------------------------------------------------------------------------------------------------------
# Writing.

def token_order():
    """Tokens.axaml's colour tokens in its order, with the element each is, from its Dark dictionary."""
    with open(os.path.join(THEME, "Tokens.axaml"), encoding="utf-8") as f:
        source = f.read()
    dark = source[source.index('<ResourceDictionary x:Key="Dark">'):source.index('<ResourceDictionary x:Key="Light">')]
    return re.findall(r'<([\w:]+) x:Key="(Passenger\.[^"]+)"', dark)


def value_of(kind, value):
    """A token's value as theme format 1 writes it."""
    if isinstance(value, tuple) and value[0] == "gradient":
        return ('{"start": [0, 0], "end": [1, 0], "stops": [{"offset": 0, "colour": "%s"}, {"offset": 1, "colour": "%s"}]}'
                % (value[1], value[2]))
    if isinstance(value, tuple) and value[0] == "opacity":
        return '{"colour": "%s", "opacity": %s}' % (value[1], value[2])
    if isinstance(value, tuple) and value[0] == "string":
        return json.dumps(value[1])
    if kind in ("CornerRadius", "Thickness"):
        numbers = value.split(",")
        if len(numbers) == 2 and kind == "Thickness":
            # Avalonia's "h,v": left and right h, top and bottom v.
            numbers = numbers * 2
        if len(numbers) not in (1, 4):
            raise SystemExit(f"{kind} {value} is neither one number nor four")
        return numbers[0] if len(numbers) == 1 else "[" + ", ".join(numbers) + "]"
    if kind == "x:Double":
        return value
    return json.dumps(value)


def members(entries):
    """An object's members, one to a line, under its member's line."""
    return ",\n".join(f"    {json.dumps(key[len('Passenger.'):])}: {value}" for key, value in entries)


OPTIONAL = {"Passenger.Syntax.Brace4": "SolidColorBrush", "Passenger.Syntax.Brace5": "SolidColorBrush",
            "Passenger.Syntax.Brace6": "SolidColorBrush", "Passenger.SyntaxStyle.Parameter": "x:String"}


def write(folder, name, theme):
    theme["name"] = name
    order = token_order()
    about = theme["about"]
    lines = ["{", '  "format": 1,', f'  "name": {json.dumps(name)},',
             f'  "description": {json.dumps(about[0].upper() + about[1:] + ".")},']
    if "vscode" in theme:
        lines.append(f'  "vscode": {json.dumps(theme["vscode"])},')
    lines += ['  "tokens": {', members((key, value_of(kind, value)) for kind, key, value in SHAPES[theme["shape"]]), "  },"]
    for member in ("fonts", "layout", "background"):
        if member in theme:
            lines.append(f'  "{member}": {json.dumps(theme[member])},')
    variants = [] if "vscode" in theme else [("Dark", True), ("Light", False)]
    languages = {}
    sections = []
    for which, dark in variants:
        tokens = variant(theme, theme[which.lower()], dark)
        for language, given in tokens.pop("languages").items():
            languages.setdefault(language, {})[which.lower()] = given
        missing = [key for _, key in order if key not in tokens]
        extra = [key for key in tokens if key not in {k for _, k in order} and key not in OPTIONAL]
        if missing or extra:
            raise SystemExit(f"{name} {which}: Tokens.axaml has {missing} that this script does not give, and it gives {extra} that Tokens.axaml lacks")
        listed = [(kind, key) for kind, key in order] + [(kind, key) for key, kind in OPTIONAL.items() if key in tokens]
        sections.append((which.lower(), members((key, value_of(kind, tokens[key])) for kind, key in listed)))
    if languages:
        lines.append(f'  "languages": {json.dumps(languages)},')
    for i, (which, body) in enumerate(sections):
        lines += [f'  "{which}": {{', body, "  }," if i < len(sections) - 1 else "  }"]
    if not sections:
        lines[-1] = lines[-1].rstrip(",")
    lines += ["}", ""]
    with open(os.path.join(folder, f"{name}.json"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def main():
    if len(sys.argv) != 2 or not os.path.isfile(os.path.join(sys.argv[1], "passenger-themes.json")):
        raise SystemExit("themes.py takes the folder of a source to write into, holding its passenger-themes.json: "
                         "a checkout of Zaneris/passenger-themes, tests/Passenger.Tests/Themes or src/Passenger.App/Theme/BuiltIn")
    folder = sys.argv[1]
    built_in = os.path.realpath(folder) == os.path.realpath(os.path.join(THEME, "BuiltIn"))
    themes = BUILT_IN if built_in else THEMES
    with open(os.path.join(folder, "passenger-themes.json"), encoding="utf-8") as f:
        listed = json.load(f)["themes"]
    unlisted = [name for name in themes if f"{name}.json" not in listed]
    if unlisted:
        raise SystemExit(f"{folder}'s passenger-themes.json lists no {', '.join(unlisted)}: add each to its themes")
    for name, theme in themes.items():
        write(folder, name, theme)
    print(f"Wrote {len(themes)} themes into {folder}: {', '.join(themes)}")


if __name__ == "__main__":
    sys.exit(main())
