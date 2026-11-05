#!/usr/bin/env python3
"""Writes src/Passenger.Workspace/GDScript/UnicodeIdentifiers.cs from Unicode's security and character data.

What Godot's spoof check of an identifier reads (ICU's uspoof_check, through TextServerAdvanced::spoof_check): the
characters an identifier may have (Identifier_Status Allowed, ICU's recommended and inclusion sets), each one's augmented
script set (Script_Extensions, Han's, Hiragana's, Katakana's, Hangul's and Bopomofo's augmented as UTS #39 section 5.1
says, Common and Inherited every script), the characters whose combining class is neither 0 nor 230, which a hidden dot
looks past, and those after which a combining dot above is hidden (i, j, dotless i and j, l, the Soft_Dotted, and those
confusables.txt reads as one of them). VERSION is the Unicode version of the ICU Godot carries (ICU 78.3 in Godot 4.7).
The files are fetched once into artifacts/unicode/<version>/. THIRD_PARTY_NOTICES.md carries the Unicode licence.
"""
import os
import urllib.request

VERSION = "17.0.0"
FILES = {
    "IdentifierStatus.txt": f"https://www.unicode.org/Public/{VERSION}/security/IdentifierStatus.txt",
    "confusables.txt": f"https://www.unicode.org/Public/{VERSION}/security/confusables.txt",
    "Scripts.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/Scripts.txt",
    "ScriptExtensions.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/ScriptExtensions.txt",
    "PropertyValueAliases.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/PropertyValueAliases.txt",
    "UnicodeData.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/UnicodeData.txt",
    "PropList.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/PropList.txt",
}

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Passenger.Workspace", "GDScript", "UnicodeIdentifiers.cs")


def fetch(name, cache):
    path = os.path.join(cache, name)
    if not os.path.exists(path):
        with urllib.request.urlopen(FILES[name]) as response:
            data = response.read()
        with open(path, "wb") as out:
            out.write(data)
    with open(path, encoding="utf-8-sig") as text:
        return text.read()


def rows(text):
    for line in text.splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            yield [field.strip() for field in line.split(";")]


def points(field):
    if ".." in field:
        first, last = field.split("..")
        return range(int(first, 16), int(last, 16) + 1)
    return [int(field, 16)]


def ranges(values):
    """Runs of consecutive code points with one value: (first, last, value)."""
    out = []
    for point in sorted(values):
        value = values[point]
        if out and out[-1][1] == point - 1 and out[-1][2] == value:
            out[-1][1] = point
        else:
            out.append([point, point, value])
    return out


def main():
    cache = os.path.join(ROOT, "artifacts", "unicode", VERSION)
    os.makedirs(cache, exist_ok=True)
    allowed = set()
    for fields in rows(fetch("IdentifierStatus.txt", cache)):
        if fields[1] == "Allowed":
            allowed.update(points(fields[0]))
    short = {}
    for fields in rows(fetch("PropertyValueAliases.txt", cache)):
        if fields[0] == "sc":
            short[fields[2]] = fields[1]
            short[fields[1]] = fields[1]
    scripts = {}
    for fields in rows(fetch("Scripts.txt", cache)):
        for point in points(fields[0]):
            scripts[point] = {short[fields[1]]}
    for fields in rows(fetch("ScriptExtensions.txt", cache)):
        for point in points(fields[0]):
            scripts[point] = {short[name] for name in fields[1].split()}
    augmented = {}
    for point in allowed:
        found = set(scripts.get(point, {"Zzzz"}))
        if "Hani" in found:
            found |= {"Hanb", "Jpan", "Kore"}
        if "Hira" in found or "Kana" in found:
            found.add("Jpan")
        if "Hang" in found:
            found.add("Kore")
        if "Bopo" in found:
            found.add("Hanb")
        augmented[point] = "" if found & {"Zyyy", "Zinh"} else " ".join(sorted(found))
    sets = sorted(set(augmented.values()))
    index = {value: i for i, value in enumerate(sets)}
    allowed_runs = ranges({point: index[augmented[point]] for point in allowed})

    combining = {}
    for fields in rows(fetch("UnicodeData.txt", cache)):
        ccc = int(fields[3])
        if ccc not in (0, 230):
            combining[int(fields[0], 16)] = 1
    combining_runs = ranges(combining)

    leads = {0x69, 0x6A, 0x131, 0x237, 0x6C}
    for fields in rows(fetch("PropList.txt", cache)):
        if fields[1] == "Soft_Dotted":
            leads.update(points(fields[0]))
    base = set(leads)
    for fields in rows(fetch("confusables.txt", cache)):
        source = int(fields[0], 16)
        target = [int(p, 16) for p in fields[1].split()]
        if target and target[-1] != source and target[-1] in base:
            leads.add(source)

    def table(name, numbers, width=12):
        lines = []
        for i in range(0, len(numbers), width):
            lines.append("        " + ", ".join(f"0x{n:X}" if isinstance(n, int) and n > 9 else str(n) for n in numbers[i:i + width]) + ",")
        return f"    private static readonly int[] {name} =\n    [\n" + "\n".join(lines) + "\n    ];\n"

    flat_allowed = [n for run in allowed_runs for n in run]
    flat_combining = [n for run in combining_runs for n in run[:2]]
    with open(OUT, "w", encoding="utf-8", newline="\n") as out:
        out.write("// Written by tools/unicode-identifiers.py from Unicode " + VERSION + "'s security and character data: do not edit.\n")
        out.write("namespace Passenger.Workspace.GDScript;\n\n")
        out.write("/// <summary>What an identifier's spoof check reads of Unicode " + VERSION + " (<see cref=\"IdentifierSpoofing\"/>): the characters\n")
        out.write("/// allowed, each run's augmented script set, the combining marks a hidden dot looks past, and the characters a dot\n")
        out.write("/// above hides on.</summary>\n")
        out.write("internal static partial class UnicodeIdentifiers\n{\n")
        out.write("    /// <summary>Each allowed run's script set by index, the first (empty) every script.</summary>\n")
        out.write("    private static readonly string[] ScriptSets =\n    [\n")
        for value in sets:
            out.write(f"        \"{value}\",\n")
        out.write("    ];\n\n")
        out.write("    /// <summary>The allowed characters' runs: first, last and script set, three numbers each, in order.</summary>\n")
        out.write(table("AllowedRuns", flat_allowed))
        out.write("\n    /// <summary>The runs of characters whose combining class is neither 0 nor 230: first and last, in order.</summary>\n")
        out.write(table("CombiningRuns", flat_combining))
        out.write("\n    /// <summary>The characters a combining dot above is hidden on, in order.</summary>\n")
        out.write(table("DotLeads", sorted(leads)))
        out.write("}\n")


if __name__ == "__main__":
    main()
