#!/usr/bin/env python3
"""Writes src/Passenger.Terminal/WideCharacters.cs from the Unicode Character Database.

A character is two columns wide when its East_Asian_Width is Wide or Fullwidth, or when it is presented as an emoji
(Emoji_Presentation). The files are fetched once into artifacts/unicode/<version>/. VERSION is the Unicode version
.NET's CharUnicodeInfo knows, which the terminal's zero-width rule reads, so the two stay one version.
THIRD_PARTY_NOTICES.md carries the Unicode licence.
"""
import importlib.util
import os

_spec = importlib.util.spec_from_file_location("unicode", os.path.join(os.path.dirname(os.path.abspath(__file__)), "unicode.py"))
unicode = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(unicode)

VERSION = "16.0.0"
FILES = {
    "EastAsianWidth.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/EastAsianWidth.txt",
    "emoji-data.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/emoji/emoji-data.txt",
}

# EastAsianWidth.txt's header: unassigned code points in these default to Wide.
WIDE_DEFAULTS = [(0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xF900, 0xFAFF), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD)]


def code_points(text, values):
    found = set()
    for fields in unicode.rows(text):
        if fields[1] in values:
            found.update(unicode.points(fields[0]))
    return found


def main():
    root = unicode.ROOT
    cache = unicode.cache(VERSION)
    wide = code_points(unicode.fetch(FILES, "EastAsianWidth.txt", cache), {"W", "F"})
    for first, last in WIDE_DEFAULTS:
        wide.update(range(first, last + 1))
    wide |= code_points(unicode.fetch(FILES, "emoji-data.txt", cache), {"Emoji_Presentation"})
    merged = unicode.ranges(dict.fromkeys(wide))
    numbers = [f"0x{point:X}" for run in merged for point in run[:2]]
    rows = [", ".join(numbers[i:i + 12]) + "," for i in range(0, len(numbers), 12)]
    lines = [
        f"// Written by tools/unicode-widths.py from the Unicode Character Database {VERSION} (EastAsianWidth.txt and",
        "// emoji-data.txt, under the Unicode License v3; see THIRD_PARTY_NOTICES.md). Run it again rather than edit this.",
        "namespace Passenger.Terminal;",
        "",
        "/// <summary>The characters a terminal draws two columns wide: East Asian Wide and Fullwidth, and those presented as",
        "/// emoji (the regional indicators besides).</summary>",
        "internal static partial class WideCharacters",
        "{",
        "    /// <summary>The first and last code point of each range, in order.</summary>",
        "    private static ReadOnlySpan<int> Ranges =>",
        "    [",
        *[f"        {row}" for row in rows],
        "    ];",
        "}",
    ]
    target = os.path.join(root, "src", "Passenger.Terminal", "WideCharacters.cs")
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines) + "\n")
    print(f"unicode-widths: {len(merged)} ranges written to {os.path.relpath(target, root)}")


if __name__ == "__main__":
    main()
