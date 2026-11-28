#!/usr/bin/env python3
"""Keeps every translation of Passenger's words in step with its English.

Each neutral .resx under src/ (and the unit tests' own) is the English; each xlf/Name.<culture>.xlf beside it is one
language's translation, XLIFF 1.2 as Arcade's XliffTasks writes it. This writes each XLIFF file again from its English:
a key the English gained comes in untranslated (state "new"), a key whose English changed keeps its translation marked
"needs-review-translation" (shown until a person reviews it, while its arguments still match), a key the English lost
goes, and each unit's note is the English's comment. It then says, for each language, how many units are untranslated
and how many translations are older than their English.

    just words              # every file brought in step
    just words --add de     # and a file begun for a new language beside every .resx
"""
import os
import re
import sys
import xml.etree.ElementTree as ElementTree

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FOLDERS = [os.path.join(ROOT, "src"), os.path.join(ROOT, "tests", "Passenger.UnitTests")]
XLIFF = "urn:oasis:names:tc:xliff:document:1.2"
X = "{" + XLIFF + "}"
REVIEW = "needs-review-translation"


def neutral_files():
    for folder in FOLDERS:
        for path, folders, names in os.walk(folder):
            folders[:] = [name for name in folders if name not in ("bin", "obj", "node_modules")]
            for name in sorted(names):
                if name.endswith(".resx") and name.count(".") == 1:
                    yield os.path.join(path, name)


def read_resx(path):
    """The English's keys in order, each with its value and comment."""
    entries = []
    for data in ElementTree.parse(path).getroot().iter("data"):
        if data.get("type") is None and data.get("mimetype") is None:
            entries.append((data.get("name"), data.findtext("value") or "", data.findtext("comment") or ""))
    return entries


def read_xliff(path):
    """A translation's units by key: its source, target, the target's state and qualifier."""
    units = {}
    for unit in ElementTree.parse(path).getroot().iter(X + "trans-unit"):
        target = unit.find(X + "target")
        units[unit.get("id")] = (unit.findtext(X + "source") or "", None if target is None else (target.text or ""),
                                 None if target is None else target.get("state"), None if target is None else target.get("state-qualifier"))
    return units


def escaped(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def written(culture, original, entries, units):
    """The XLIFF file's text for the English's entries, its units carried over where they stand."""
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        f'<xliff xmlns="{XLIFF}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="1.2" '
        f'xsi:schemaLocation="{XLIFF} xliff-core-1.2-transitional.xsd">',
        f'  <file datatype="xml" source-language="en" target-language="{culture}" original="{original}">',
        "    <body>",
    ]
    counts = {"new": 0, REVIEW: 0}
    for key, english, comment in entries:
        source, target, state, qualifier = units.get(key, (english, None, None, None))
        if target is None or state == "new":
            target, state, qualifier = english, "new", None
        elif source != english:
            state = REVIEW
        if state in counts:
            counts[state] += 1
        qualified = f' state-qualifier="{qualifier}"' if qualifier else ""
        lines += [
            f'      <trans-unit id="{key}">',
            f"        <source>{escaped(english)}</source>",
            f'        <target state="{state}"{qualified}>{escaped(target)}</target>',
            f"        <note>{escaped(comment)}</note>" if comment else "        <note />",
            "      </trans-unit>",
        ]
    lines += ["    </body>", "  </file>", "</xliff>"]
    return "\n".join(lines) + "\n", counts


def main():
    added = sys.argv[2:3] if sys.argv[1:2] == ["--add"] else []
    if added and not re.fullmatch(r"[a-z]{2,3}(-[A-Za-z0-9]{2,8})*", added[0]):
        sys.exit(f"{added[0]} is no language tag")
    totals = {}
    for resx in neutral_files():
        folder, name = os.path.split(resx)
        stem = name[:-len(".resx")]
        xlf = os.path.join(folder, "xlf")
        cultures = sorted({f[len(stem) + 1:-len(".xlf")] for f in (os.listdir(xlf) if os.path.isdir(xlf) else [])
                           if f.startswith(stem + ".") and f.endswith(".xlf")} | set(added))
        entries = read_resx(resx)
        for culture in cultures:
            path = os.path.join(xlf, f"{stem}.{culture}.xlf")
            units = read_xliff(path) if os.path.exists(path) else {}
            text, counts = written(culture, f"../{name}", entries, units)
            os.makedirs(xlf, exist_ok=True)
            before = open(path, encoding="utf-8").read() if os.path.exists(path) else None
            if text != before:
                with open(path, "w", encoding="utf-8", newline="\n") as out:
                    out.write(text)
                print(f"wrote {os.path.relpath(path, ROOT)}")
            total = totals.setdefault(culture, [0, 0, 0])
            total[0] += len(entries)
            total[1] += counts["new"]
            total[2] += counts[REVIEW]
    for culture, (keys, new, review) in sorted(totals.items()):
        print(f"{culture}: {keys - new - review} of {keys} translated, {new} untranslated, {review} older than their English")


if __name__ == "__main__":
    main()
