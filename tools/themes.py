#!/usr/bin/env python3
"""Writes the generated themes, src/Passenger.App/Theme/<Name>.axaml, from the palettes below.

Each theme is a few colours per variant (its grounds, words, accent and hues); every token Tokens.axaml gives is worked
out from them here, then nudged in lightness until each pair UiRulesTests holds to WCAG AA passes, so a palette never
has to be tuned token by token. The token list is read from Tokens.axaml, so a token added there and not here fails
this script rather than leaving a theme without it. Adding a theme is a palette here, a `Look` and `just themes`.
"""
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
        about="soft pastels on a dusky plum by night and a pale lavender paper by day, a mauve accent and peach for the caret",
        shape="card", italic=True,
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
        about="a violet night of hot pink keywords, lemon strings and mint calls, its day a lilac-tinted page",
        shape="pill", italic=True,
        dark=dict(ground="#24252F", text="#F4F4EE", muted="#AEB2D0", accent="#B395F5", glance="#FF7CC6",
                  red="#FF6464", orange="#FFB56E", yellow="#EEF390", green="#5CF184", cyan="#8EE4F8", blue="#93A9F5",
                  purple="#BB96F7", pink="#FF7CC6", comment="#717EB0", member="#CBC4F2"),
        light=dict(ground="#FAF9FC", text="#2B2840", muted="#5B5775", accent="#7148CC", glance="#BF2F7C",
                   red="#C8253A", orange="#B35600", yellow="#8A6A00", green="#1C7D3A", cyan="#0A7590", blue="#3155C8",
                   purple="#6E44C9", pink="#B5266F", comment="#6F6A8C", member="#5A4A9E"),
        code=dict(keyword="pink", string="yellow", escape="pink", number="purple", type="cyan", struct="cyan",
                  method="green", member="member", event="orange", tag="pink", attribute="green", heading="purple"),
    ),
    "Saffron": dict(
        about="ink-dark slate with saffron and tangerine, sky-blue types and lime strings; by day the same on chalk",
        shape="pill", italic=True,
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
        about="a cool grey night lit with candy pink, butter, mint and sky, its day a blush-white page",
        shape="pill", italic=False,
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
        about="plain neutral greys with a clear blue accent, coral keywords and navy or ice strings: quiet, for long days",
        shape="card", italic=False,
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
        about="a soft blue-grey with lilac keywords, sage strings, cornflower calls and wheat types, day and night alike",
        shape="pill", italic=True,
        dark=dict(ground="#262A31", text="#B2B8C3", muted="#8C939F", accent="#4F8FE8", glance="#5EA9EB",
                  red="#DF6E78", orange="#D29B6A", yellow="#E3BF7E", green="#97C17A", cyan="#58B4C0", blue="#62ADED",
                  purple="#C47BDB", pink="#D87DB8", comment="#68707C", member="#DF6E78"),
        light=dict(ground="#FAFAFA", text="#383A42", muted="#5C5F69", accent="#3A6FE0", glance="#3A6FE0",
                   red="#C9483D", orange="#966707", yellow="#A67200", green="#468F45", cyan="#0B78AB", blue="#3A6FE0",
                   purple="#A429A2", pink="#B03A8A", comment="#8E9098", member="#C9483D"),
        code=dict(keyword="purple", string="green", escape="cyan", number="orange", type="yellow", struct="yellow",
                  method="blue", member="member", event="pink", tag="red", attribute="orange", heading="red"),
    ),
    "Orbit": dict(
        about="deep space with signal teal, aqua and periwinkle and a warm sand for strings; by day a pale observatory",
        shape="pill", italic=False,
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
        about="a navy night sky of violet keywords, apricot strings and seafoam members; its day a cool white page",
        shape="card", italic=True,
        dark=dict(ground="#03182A", text="#D4DCEA", muted="#93A3B8", accent="#8A62CF", glance="#FFCB8B",
                  red="#EF5A5A", orange="#F58D6E", yellow="#FBC98C", green="#ADD86A", cyan="#80D9C9", blue="#84ABFC",
                  purple="#C690E8", pink="#E58FC6", comment="#6E8890", member="#80D9C9", string="#E9C38E"),
        light=dict(ground="#FBFBFB", text="#403F53", muted="#5E5D70", accent="#7A3FB8", glance="#9A5A00",
                   red="#C83B3B", orange="#B35A2E", yellow="#8A5A00", green="#4F7F12", cyan="#0A8A8F", blue="#3F6DCF",
                   purple="#9446C0", pink="#A2097C", comment="#8A8F92", member="#0A8A8F", string="#B85A57"),
        code=dict(keyword="purple", string="string", escape="cyan", number="orange", type="yellow", struct="yellow",
                  method="blue", member="member", event="pink", tag="cyan", attribute="purple", heading="blue"),
    ),
    "Moss": dict(
        about="a green-grey woodland, sage and leaf with honey strings and clay types, easy on the eye night and day",
        shape="card", italic=True,
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
        about="a near-black terminal glass with electric lime, cyan and violet; by day crisp ink on white",
        shape="pill", italic=False,
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

# The shapes a theme draws its own way: Passenger's pills and crowned popups, or Harbour's cards joined to the editor.
SHAPES = {
    "pill": [("CornerRadius", "Passenger.ControlRadius", "6"), ("CornerRadius", "Passenger.ToolRadius", "8"),
             ("CornerRadius", "Passenger.PopupRadius", "10"), ("CornerRadius", "Passenger.CardRadius", "10"),
             ("CornerRadius", "Passenger.BadgeRadius", "6"), ("CornerRadius", "Passenger.ChipRadius", "10"),
             ("x:Double", "Passenger.TabStripHeight", "38"), ("Thickness", "Passenger.TabMargin", "2,5"),
             ("Thickness", "Passenger.TabPadding", "10,0,6,0"), ("CornerRadius", "Passenger.TabRadius", "7"),
             ("BoxShadows", "Passenger.TabShadow", "0 1 2 0 #1F000000"), ("Thickness", "Passenger.PopupEdge", "1,3,1,1")],
    "card": [("CornerRadius", "Passenger.ControlRadius", "8"), ("CornerRadius", "Passenger.ToolRadius", "8"),
             ("CornerRadius", "Passenger.PopupRadius", "12"), ("CornerRadius", "Passenger.CardRadius", "14"),
             ("CornerRadius", "Passenger.BadgeRadius", "99"), ("CornerRadius", "Passenger.ChipRadius", "99"),
             ("x:Double", "Passenger.TabStripHeight", "40"), ("Thickness", "Passenger.TabMargin", "1,6,1,0"),
             ("Thickness", "Passenger.TabPadding", "14,0,8,0"), ("CornerRadius", "Passenger.TabRadius", "10,10,0,0"),
             ("BoxShadows", "Passenger.TabShadow", "0 0 0 0 #00000000"), ("Thickness", "Passenger.PopupEdge", "1")],
}

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
    t["Passenger.FieldBorder"] = mix(panel, text, 0.2)
    t["Passenger.List"] = lst
    t["Passenger.Button"] = button
    t["Passenger.Popup"] = popup
    t["Passenger.PopupBorder"] = mix(popup, text, 0.16)
    t["Passenger.PopupShadow"] = "0 6 20 0 #80000000" if dark else "0 6 20 0 #2E000000"
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
    t["Passenger.WindowEdge"] = t["Passenger.FieldBorder"]

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
    t["Passenger.TextSelection"] = selection

    t["Passenger.Hover"] = hover
    t["Passenger.Selected"] = selected
    t["Passenger.RowSelected"] = row
    t["Passenger.Accent"] = accent
    t["Passenger.AccentHover"] = accent_hover
    t["Passenger.Focus"] = legible(glance, marks + [field], 3.1, dark)
    t["Passenger.CheckBorder"] = mix(panel, text, 0.45)
    t["Passenger.ScrollThumb"] = "#59808080" if dark else "#33000000"
    t["Passenger.ScrollThumbHover"] = "#8C808080" if dark else "#80000000"
    t["Passenger.ScrollTrackHover"] = "#1A808080"
    t["Passenger.Menu.Selected"] = accent
    t["Passenger.Menu.SelectedText"] = accent_text
    t["Passenger.Menu.SelectedKeys"] = accent_text

    # The title bar is the frame, its words the window's own.
    bar_hover, bar_pressed = (shift(frame, 0.05), shift(frame, 0.08)) if dark else (shift(frame, -0.05), shift(frame, -0.08))
    bars = [frame, bar_hover, bar_pressed]
    t["Passenger.TitleBar"] = frame
    t["Passenger.TitleBar.Text"] = legible(text, bars, 4.6, dark)
    t["Passenger.TitleBar.Icon"] = legible(t["Passenger.Icon"], [frame], 3.1, dark)
    t["Passenger.TitleBar.Hover"] = bar_hover
    t["Passenger.TitleBar.Pressed"] = bar_pressed
    t["Passenger.TitleBar.Rule"] = t["Passenger.StripeSeparator"]
    t["Passenger.TitleBar.Run"] = legible(hue("green"), [frame], 3.1, dark)
    t["Passenger.TitleBar.Stop"] = legible(hue("red"), [frame], 3.1, dark)
    t["Passenger.TitleBar.HotReload"] = legible(hue("orange"), [frame], 3.1, dark)
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
    tab = editor if theme["shape"] == "card" else mix(panel, accent, 0.14 if dark else 0.0)
    if not dark and theme["shape"] == "pill":
        tab = editor
    t["Passenger.Tab.Active"] = tab
    t["Passenger.Tab.ActiveText"] = legible(text, [tab], 4.6, dark)
    t["Passenger.Modified"] = legible(glance, [frame, panel], 3.1, dark)

    t["Passenger.Welcome.Tile"] = tile
    t["Passenger.Welcome.TileBorder"] = mix(tile, text, 0.1) if theme["shape"] == "pill" else tile
    t["Passenger.Welcome.TileShadow"] = "0 0 0 0 #00000000" if theme["shape"] == "pill" \
        else ("0 2 10 0 #59000000" if dark else "0 2 10 0 #1F000000")
    t["Passenger.Welcome.Chip"] = row
    t["Passenger.Welcome.ChipIcon"] = t["Passenger.Stripe.SelectedIcon"]
    t["Passenger.Welcome.Badge"] = accent

    t["Passenger.Run"] = legible(hue("green"), [panel, frame], 3.1, dark)
    t["Passenger.HotReload"] = legible(hue("orange"), [panel, frame], 3.1, dark)
    t["Passenger.Test.Passed"] = t["Passenger.Run"]
    t["Passenger.Test.Failed"] = legible(hue("red"), [panel], 3.1, dark)
    t["Passenger.Test.Skipped"] = mix(muted, panel, 0.15)
    t["Passenger.Test.Running"] = legible(hue("blue"), [panel], 3.1, dark)
    t["Passenger.Test.NotRun"] = t["Passenger.CheckBorder"]

    t["Passenger.Vcs.Added"] = hue("green")
    t["Passenger.Vcs.Modified"] = hue("blue")
    t["Passenger.Vcs.Deleted"] = mix(muted, panel, 0.15)
    t["Passenger.Vcs.Conflict"] = hue("red")
    files = [panel, row]
    t["Passenger.Vcs.File.Modified"] = legible(hue("blue"), files, 4.6, dark)
    t["Passenger.Vcs.File.Added"] = legible(hue("green"), files, 4.6, dark)
    t["Passenger.Vcs.File.Deleted"] = mix(muted, panel, 0.25)
    t["Passenger.Vcs.File.Unversioned"] = legible(hue("orange"), files, 4.6, dark)
    t["Passenger.Vcs.File.Conflicted"] = legible(hue("red"), files, 4.6, dark)
    for i, name in enumerate(["blue", "green", "yellow", "purple", "red", "cyan"]):
        t[f"Passenger.Graph.{i}"] = legible(hue(name), [panel], 2.2, dark)
    t["Passenger.Vcs.Ref"] = mix(panel, text, 0.1)
    t["Passenger.Vcs.RefText"] = legible(mix(text, muted, 0.5), [t["Passenger.Vcs.Ref"]], 4.6, dark)
    t["Passenger.Vcs.Favorite"] = legible(hue("yellow"), [panel], 3.1, dark)

    t["Passenger.Picker.RingInner"] = "#FFFFFF"
    t["Passenger.Picker.RingOuter"] = "#000000"
    t["Passenger.Picker.Checker"] = "#CCCCCC"

    # The terminal on the editor's ground, its ANSI colours the palette's hues, bright ones a step further.
    t["Passenger.Terminal.Foreground"] = legible(text, [editor], 4.6, dark)
    t["Passenger.Terminal.Background"] = editor
    t["Passenger.Terminal.Cursor"] = glance
    t["Passenger.Terminal.Selection"] = selection
    ansi = ["red", "green", "yellow", "blue", "purple", "cyan"]
    up = 0.07 if dark else -0.07
    t["Passenger.Terminal.Ansi0"] = mix(editor, text, 0.2) if dark else shift(text, -0.1)
    for i, name in enumerate(ansi, 1):
        t[f"Passenger.Terminal.Ansi{i}"] = hue(name) if dark else legible(hue(name), [editor], 3.0, dark)
        t[f"Passenger.Terminal.Ansi{i + 8}"] = shift(t[f"Passenger.Terminal.Ansi{i}"], up * (1 if dark else -0.6))
    t["Passenger.Terminal.Ansi7"] = mix(muted, text, 0.3)
    t["Passenger.Terminal.Ansi8"] = mix(editor, text, 0.42)
    t["Passenger.Terminal.Ansi15"] = shift(text, 0.08) if dark else mix(editor, text, 0.32)

    t["Passenger.File.Folder"] = mix(text, muted, 0.6)
    t["Passenger.File.SolutionFolder"] = hue("purple")
    t["Passenger.File.Plain"] = t["Passenger.File.Folder"]
    t.update(file_icons(hue, text, muted, panel, dark))

    t["Passenger.Editor.Background"] = editor
    t["Passenger.Editor.Gutter"] = editor
    t["Passenger.Editor.PopupBorder"] = t["Passenger.FieldBorder"]

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
    t["Passenger.Editor.Error"] = hue("red")
    t["Passenger.Editor.Warning"] = hue("yellow") if dark else hue("orange")
    t["Passenger.Editor.Info"] = muted
    t["Passenger.Editor.Breakpoint"] = hue("red")
    t["Passenger.Editor.BreakpointLine"] = mix(editor, hue("red"), 0.12)
    t["Passenger.Editor.BreakpointMuted"] = t["Passenger.Icon"]
    t["Passenger.Editor.Tracepoint"] = hue("yellow")
    t["Passenger.Editor.ExecutionLine"] = execution
    t["Passenger.Editor.ExecutionPoint"] = glance
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
    t["Passenger.Editor.SmartStepBorder"] = hue("purple")
    t["Passenger.Editor.Whitespace"] = mix(editor, text, 0.2)
    t["Passenger.Editor.SoftWrapSign"] = mix(editor, text, 0.38)
    t["Passenger.Editor.LinkUnderline"] = hue("blue")
    t["Passenger.Editor.Bookmark"] = hue("blue")
    for name, tone in (("Added", "green"), ("Modified", "blue"), ("Deleted", None), ("Conflict", "red")):
        other = hue(tone) if tone else text
        t[f"Passenger.Diff.{name}"] = mix(editor, other, (0.15 if tone else 0.08) if dark else (0.14 if tone else 0.07))
        t[f"Passenger.Diff.{name}Word"] = mix(editor, other, (0.32 if tone else 0.18) if dark else (0.3 if tone else 0.16))

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
    t["Passenger.Syntax.Excluded"] = mix(editor, text, 0.35)
    style = "Italic" if theme["italic"] else "Normal"
    t["Passenger.SyntaxStyle.Comment"] = ("string", style)
    t["Passenger.SyntaxStyle.DocComment"] = ("string", style)
    check(theme["name"], "Dark" if dark else "Light", t)
    return t


def solid(t, key):
    v = t[key]
    if isinstance(v, tuple):
        return [v[1], v[2]] if v[0] == "gradient" else [v[1]]
    return [v]


def check(name, which, t):
    """UiRulesTests' WCAG pairs, so a palette that cannot meet them fails here rather than in the tests."""
    text, mark = 4.5, 3
    pairs = []
    for g in ["Panel", "Frame", "Field", "Popup", "List", "Hover", "HoverOnFrame", "Selected", "RowSelected",
              "Editor.Background", "Banner", "Button", "Welcome.Tile"]:
        pairs += [("Text", g, text), ("TextMuted", g, text)]
    for g in ["Panel", "Frame", "Popup", "Editor.Background"]:
        pairs += [("Link", g, text), ("Error", g, text), ("Warning", g, text), ("Focus", g, mark), ("Icon", g, mark)]
    pairs += [("Focus", "Field", mark), ("LinkHover", "Panel", text), ("Accent", "Panel", mark),
              ("AccentText", "Accent", text), ("AccentText", "AccentHover", text), ("AccentText", "Welcome.Badge", text),
              ("Menu.SelectedText", "Menu.Selected", text), ("Tab.ActiveText", "Tab.Active", text),
              ("Stripe.SelectedIcon", "Stripe.Selected", mark), ("Welcome.ChipIcon", "Welcome.Chip", mark)]
    for f in ["Modified", "Added", "Unversioned", "Conflicted"]:
        pairs += [(f"Vcs.File.{f}", "Panel", text), (f"Vcs.File.{f}", "RowSelected", text)]
    for g in ["TitleBar", "TitleBar.Hover", "TitleBar.Pressed"]:
        pairs += [("TitleBar.Text", g, text), ("TitleBar.Focus", g, mark)]
    for m in ["Icon", "Run", "Stop", "HotReload"]:
        pairs.append((f"TitleBar.{m}", "TitleBar", mark))
    pairs += [("TitleBar.BadgeText", "TitleBar.Badge", text), ("Terminal.Foreground", "Terminal.Background", text)]
    for g in ["Editor.Background", "Editor.CurrentLine", "Editor.Selection", "Editor.ExecutionLine"]:
        pairs.append(("Editor.Text", g, text))
    pairs += [("Editor.LineNumber", "Editor.Gutter", text), ("Editor.LineNumberCurrent", "Editor.Gutter", text),
              ("Editor.InlineValue", "Editor.Background", text), ("Editor.InlineValue", "Editor.ExecutionLine", text),
              ("Editor.InlayHint", "Editor.InlayHintBack", text), ("Editor.Caret", "Editor.Background", mark),
              ("Editor.Caret", "Editor.CurrentLine", mark)]
    for kind in SYNTAX_KINDS:
        pairs += [(f"Syntax.{kind}", "Editor.Background", text), (f"Syntax.{kind}", "Editor.CurrentLine", text)]
    below = []
    for fore, ground, least in pairs:
        under = "Passenger.TitleBar" if ground.startswith("TitleBar.") else "Passenger.Frame" if ground == "HoverOnFrame" else "Passenger.Panel"
        grounds = solid(t, "Passenger.Hover" if ground == "HoverOnFrame" else "Passenger." + ground)
        worst = min(ratio(f, over(g, u)) for f in solid(t, "Passenger." + fore) for g in grounds for u in solid(t, under))
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


def element(kind, key, value):
    if isinstance(value, tuple) and value[0] == "gradient":
        return (f'<LinearGradientBrush x:Key="{key}" StartPoint="0%,0%" EndPoint="100%,0%">\n'
                f'                <GradientStop Offset="0" Color="{value[1]}" />\n'
                f'                <GradientStop Offset="1" Color="{value[2]}" />\n'
                f'            </LinearGradientBrush>')
    if isinstance(value, tuple) and value[0] == "opacity":
        return f'<SolidColorBrush x:Key="{key}" Color="{value[1]}" Opacity="{value[2]}" />'
    if isinstance(value, tuple) and value[0] == "string":
        return f'<x:String x:Key="{key}">{value[1]}</x:String>'
    if kind == "BoxShadows":
        return f'<BoxShadows x:Key="{key}">{value}</BoxShadows>'
    return f'<SolidColorBrush x:Key="{key}" Color="{value}" />'


def write(name, theme):
    theme["name"] = name
    order = token_order()
    lines = ['<ResourceDictionary xmlns="https://github.com/avaloniaui"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
             f'    <!-- {name}, one of Passenger\'s themes (Settings, Appearance): {theme["about"]}. Written by',
             '         tools/themes.py from its palette there (just themes), every token of Tokens.axaml again; change the palette',
             '         and run it again rather than editing this file. -->']
    for kind, key, value in SHAPES[theme["shape"]]:
        lines.append(f'    <{kind} x:Key="{key}">{value}</{kind}>')
    lines += ["", "    <ResourceDictionary.ThemeDictionaries>"]
    for which, dark in (("Dark", True), ("Light", False)):
        tokens = variant(theme, theme[which.lower()], dark)
        missing = [key for _, key in order if key not in tokens]
        extra = [key for key in tokens if key not in {k for _, k in order}]
        if missing or extra:
            raise SystemExit(f"{name} {which}: Tokens.axaml has {missing} that this script does not give, and it gives {extra} that Tokens.axaml lacks")
        lines.append(f'        <ResourceDictionary x:Key="{which}">')
        for kind, key in order:
            lines.append("            " + element(kind, key, tokens[key]))
        lines.append("        </ResourceDictionary>")
    lines += ["    </ResourceDictionary.ThemeDictionaries>", "</ResourceDictionary>", ""]
    with open(os.path.join(THEME, f"{name}.axaml"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def main():
    for name, theme in THEMES.items():
        write(name, theme)
    print(f"Wrote {len(THEMES)} themes: {', '.join(THEMES)}")


if __name__ == "__main__":
    sys.exit(main())
