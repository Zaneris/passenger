#!/usr/bin/env python3
"""Writes src/Passenger.Core/PluralRules.Cldr.cs, LikelyScripts.Cldr.cs and LanguagePatterns.Cldr.cs from the Unicode CLDR.

PluralRules.Cldr.cs holds each language's plural rules (supplemental/plurals.xml): the category a count takes, each rule
turned into C# over the operands CLDR defines (n, i, v, w, f, t, e). LikelyScripts.Cldr.cs holds the script each language
is written in where its tag names none (supplemental/likelySubtags.xml), which the words' fallback reads so it never
crosses into another script. LanguagePatterns.Cldr.cs holds how each language Passenger carries (LANGUAGES, and each
src/**/xlf/*.<tag>.xlf's) joins a list by and and by or, and writes the units of the whiles and sizes Passenger says
(main/<language>.xml, resolved through CLDR's inheritance to root). The files are fetched once into
artifacts/cldr/<version>/. THIRD_PARTY_NOTICES.md carries the Unicode licence.
"""
import glob
import importlib.util
import os
import re
import xml.etree.ElementTree as ElementTree

_spec = importlib.util.spec_from_file_location("fetched", os.path.join(os.path.dirname(os.path.abspath(__file__)), "fetched.py"))
fetched = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fetched)

VERSION = "49"
BASE = f"https://raw.githubusercontent.com/unicode-org/cldr/release-{VERSION}/common/supplemental/"
MAIN = f"https://raw.githubusercontent.com/unicode-org/cldr/release-{VERSION}/common/main/"
# The languages Passenger's first translations are made in, by CLDR's locale ids; a language a translation file carries is
# taken too.
LANGUAGES = ["de", "es", "fr", "hi", "it", "pa", "pt", "zh"]
# Each unit Passenger says, by its name in LanguagePatterns, CLDR's unit and the width it is said in.
UNITS = [
    ("millisecond", "duration-millisecond", "short"),
    ("second", "duration-second", "short"),
    ("minute", "duration-minute", "long"),
    ("hour", "duration-hour", "long"),
    ("byte", "digital-byte", "short"),
    ("kilobyte", "digital-kilobyte", "short"),
    ("megabyte", "digital-megabyte", "short"),
    ("gigabyte", "digital-gigabyte", "short"),
]
INHERITED = "\u2191\u2191\u2191"
OPERANDS = {"n": "o.N", "i": "o.I", "v": "o.V", "w": "o.W", "f": "o.F", "t": "o.T", "e": "o.E", "c": "o.E"}
HEADER = [
    f"// Written by tools/cldr.py from the Unicode CLDR {VERSION} ({{0}}, under the Unicode License v3; see",
    "// THIRD_PARTY_NOTICES.md). Run it again rather than edit this.",
]


def fetch(name):
    return fetched.text(BASE + name, os.path.join(fetched.ROOT, "artifacts", "cldr", VERSION, name))


def fetch_main(locale):
    return fetched.text(MAIN + f"{locale}.xml", os.path.join(fetched.ROOT, "artifacts", "cldr", VERSION, "main", f"{locale}.xml"))


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


def carried():
    """LANGUAGES and each language a translation file under src/ carries, as CLDR's locale ids (zh_Hans, not zh-Hans)."""
    tags = {os.path.basename(path).split(".")[-2] for path in glob.glob(os.path.join(fetched.ROOT, "src", "**", "xlf", "*.xlf"), recursive=True)}
    return sorted(set(LANGUAGES) | {t.replace("-", "_") for t in tags})


def patterns(locale):
    """A locale's own list patterns ({kind: {part: text}}) and unit patterns ({(width, unit): {count: text}}), what it
    inherits left out."""
    root = ElementTree.fromstring(fetch_main(locale))
    lists = {}
    for pattern in root.iter("listPattern"):
        kind = pattern.get("type") or "and"
        if kind in ("and", "or"):
            lists[kind] = {part.get("type"): part.text for part in pattern.findall("listPatternPart") if part.text != INHERITED}
    units = {}
    wanted = {unit for _, unit, _ in UNITS}
    for length in root.iter("unitLength"):
        for unit in length.findall("unit"):
            if unit.get("type") in wanted:
                units[(length.get("type"), unit.get("type"))] = {
                    p.get("count"): p.text for p in unit.findall("unitPattern") if p.get("case") is None and p.text != INHERITED}
    return lists, units


def chain(locale):
    """The locale and its parents to root, by truncation as CLDR's main files inherit (zh_Hant's parent is root)."""
    parents = {"zh_Hant": "root", "pa_Arab": "root"}
    steps = [locale]
    while steps[-1] != "root":
        at = steps[-1]
        steps.append(parents.get(at) or ("_".join(at.split("_")[:-1]) if "_" in at else "root"))
    return steps


def resolved(locale):
    """A locale's list patterns (and, then or: start, middle, end, 2) and each unit's patterns by plural count, each part
    taken from the nearest locale of its chain that gives it; a unit's long width, where none gives it, its short's."""
    read = [patterns(step) for step in chain(locale)]
    lists = []
    for kind in ("and", "or"):
        for part in ("start", "middle", "end", "2"):
            lists.append(next(own[kind][part] for own, _ in read if part in own.get(kind, {})))
    units = []
    for name, unit, width in UNITS:
        widths = [width, "short"] if width != "short" else ["short"]
        counts = {}
        for want in widths:
            for _, own in read:
                for count, text in own.get((want, unit), {}).items():
                    counts.setdefault(count, text)
            if "other" in counts:
                break
        units.append((name, sorted(counts.items(), key=lambda c: (c[0] == "other", c[0]))))
    return lists, units


def cs(text):
    """A C# string literal of the text, its non-ASCII spaces escaped so they show."""
    return '"' + "".join(f"\\u{ord(c):04X}" if c in "\u00a0\u202f\u2009" else ("\\" + c if c in '"\\' else c) for c in text) + '"'


def language_patterns():
    rows = [(tag(locale), resolved(locale)) for locale in carried()]
    lines = [line.format("main/<language>.xml") for line in HEADER] + [
        "namespace Passenger.Core;",
        "",
        "public static partial class LanguagePatterns",
        "{",
        "    /// <summary>A language's list patterns by its tag, joining by and then by or: each a start, a middle, an end and a pair's;",
        "    /// null for a language CLDR's patterns are not carried of.</summary>",
        "    private static string[]? Lists(string language) => language switch",
        "    {",
    ]
    for language, (lists, _) in rows:
        lines.append(f"        \"{language}\" => [{', '.join(cs(p) for p in lists)}],")
    lines += ["        _ => null,", "    };", "",
              "    /// <summary>A language's patterns of a unit by its tag, each plural category before its pattern; null for a language",
              "    /// CLDR's patterns are not carried of.</summary>",
              "    private static string[]? Units(string language, string unit) => (language, unit) switch",
              "    {"]
    for language, (_, units) in rows:
        for name, counts in units:
            flat = ", ".join(f"\"{count}\", {cs(text)}" for count, text in counts)
            lines.append(f"        (\"{language}\", \"{name}\") => [{flat}],")
    lines += ["        _ => null,", "    };", "}"]
    return lines


def write(name, lines):
    target = os.path.join(fetched.ROOT, "src", "Passenger.Core", name)
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines) + "\n")
    print(f"wrote {os.path.relpath(target, fetched.ROOT)}")


def main():
    write("PluralRules.Cldr.cs", plural_rules())
    write("LikelyScripts.Cldr.cs", likely_scripts())
    write("LanguagePatterns.Cldr.cs", language_patterns())


if __name__ == "__main__":
    main()
