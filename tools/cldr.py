#!/usr/bin/env python3
"""Writes src/Passenger.Core/PluralRules.Cldr.cs and src/Passenger.Core/LikelyScripts.Cldr.cs from the Unicode CLDR.

PluralRules.Cldr.cs holds each language's plural rules (supplemental/plurals.xml): the category a count takes, each rule
turned into C# over the operands CLDR defines (n, i, v, w, f, t, e). LikelyScripts.Cldr.cs holds the script each language
is written in where its tag names none (supplemental/likelySubtags.xml), which the words' fallback reads so it never
crosses into another script. The files are fetched once into artifacts/cldr/<version>/. THIRD_PARTY_NOTICES.md carries
the Unicode licence.
"""
import importlib.util
import os
import re
import xml.etree.ElementTree as ElementTree

_spec = importlib.util.spec_from_file_location("fetched", os.path.join(os.path.dirname(os.path.abspath(__file__)), "fetched.py"))
fetched = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fetched)

VERSION = "49"
BASE = f"https://raw.githubusercontent.com/unicode-org/cldr/release-{VERSION}/common/supplemental/"
OPERANDS = {"n": "o.N", "i": "o.I", "v": "o.V", "w": "o.W", "f": "o.F", "t": "o.T", "e": "o.E", "c": "o.E"}
HEADER = [
    f"// Written by tools/cldr.py from the Unicode CLDR {VERSION} ({{0}}, under the Unicode License v3; see",
    "// THIRD_PARTY_NOTICES.md). Run it again rather than edit this.",
]


def fetch(name):
    return fetched.text(BASE + name, os.path.join(fetched.ROOT, "artifacts", "cldr", VERSION, name))


def tag(locale):
    """CLDR's locale id as .NET names the culture: pt_PT is pt-PT."""
    return locale.replace("_", "-")


def relation(text):
    """One relation (operand, optionally modulo, = or != a list of values and ranges) as a C# condition."""
    match = re.fullmatch(r"\s*([nivwftce])\s*(?:%\s*(\d+))?\s*(!=|=)\s*([\d.,\s]+?)\s*", text)
    if not match:
        raise ValueError(f"unread relation: {text!r}")
    operand, modulo, op, values = match.groups()
    expression = OPERANDS[operand]
    decimal = operand == "n"
    if modulo:
        expression = f"({expression} % {modulo}{'m' if decimal else ''})"
    tests = []
    for item in values.split(","):
        item = item.strip()
        if ".." in item:
            low, high = item.split("..")
            tests.append(f"In({expression}, {low}, {high})" if decimal else f"{expression} is >= {low} and <= {high}")
        else:
            tests.append(f"{expression} == {item}")
    joined = " || ".join(tests)
    if op == "!=":
        return f"!({joined})"
    return f"({joined})" if len(tests) > 1 else joined


def condition(text):
    """A rule's condition (before its samples) as a C# condition: or of ands of relations."""
    ors = [" && ".join(relation(part) for part in alternative.split(" and ")) for alternative in text.split(" or ")]
    return " || ".join(f"({part})" if " && " in part and len(ors) > 1 else part for part in ors)


def plural_rules():
    root = ElementTree.fromstring(fetch("plurals.xml"))
    sets = []
    for rules in root.iter("pluralRules"):
        locales = [tag(locale) for locale in rules.get("locales").split()]
        cases = []
        for rule in rules.iter("pluralRule"):
            count = rule.get("count")
            text = (rule.text or "").split("@", 1)[0].strip()
            if count != "other" and text:
                cases.append((count, condition(text)))
        sets.append((locales, cases))
    lines = [line.format("supplemental/plurals.xml") for line in HEADER] + [
        "namespace Passenger.Core;",
        "",
        "public static partial class PluralRules",
        "{",
        "    /// <summary>The category a count takes in a language CLDR gives rules of, by its tag; null for one it gives none.</summary>",
        "    private static string? Category(string language, in PluralOperands o) => language switch",
        "    {",
    ]
    for index, (locales, _) in enumerate(sets):
        names = " or ".join(f'"{locale}"' for locale in locales)
        lines.append(f"        {names} => Set{index}(o),")
    lines += ["        _ => null,", "    };", ""]
    for index, (_, cases) in enumerate(sets):
        lines.append(f"    private static string Set{index}(in PluralOperands o)")
        lines.append("    {")
        for count, test in cases:
            lines.append(f"        if ({test})")
            lines.append("        {")
            lines.append(f'            return "{count}";')
            lines.append("        }")
        lines.append('        return "other";')
        lines.append("    }")
        lines.append("")
    lines[-1] = "}"
    return lines


def likely_scripts():
    root = ElementTree.fromstring(fetch("likelySubtags.xml"))
    pairs = []
    for subtag in root.iter("likelySubtag"):
        source, target = subtag.get("from"), subtag.get("to")
        if re.fullmatch(r"[a-z]{2,3}", source):
            parts = target.split("_")
            if len(parts) > 1 and re.fullmatch(r"[A-Z][a-z]{3}", parts[1]):
                pairs.append(f"{source} {parts[1]}")
    rows = []
    row = ""
    for pair in pairs:
        if len(row) + len(pair) > 100:
            rows.append(row)
            row = ""
        row += pair + " "
    rows.append(row)
    lines = [line.format("supplemental/likelySubtags.xml") for line in HEADER] + [
        "namespace Passenger.Core;",
        "",
        "public static partial class LikelyScripts",
        "{",
        "    /// <summary>Each language CLDR knows by its tag alone, and the script it is likely written in, in pairs apart by spaces.</summary>",
        "    private const string Pairs =",
    ]
    lines += [f'        "{text}"' + (" +" if index < len(rows) - 1 else ";") for index, text in enumerate(rows)]
    lines.append("}")
    return lines


def write(name, lines):
    target = os.path.join(fetched.ROOT, "src", "Passenger.Core", name)
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines) + "\n")
    print(f"wrote {os.path.relpath(target, fetched.ROOT)}")


def main():
    write("PluralRules.Cldr.cs", plural_rules())
    write("LikelyScripts.Cldr.cs", likely_scripts())


if __name__ == "__main__":
    main()
