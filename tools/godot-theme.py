#!/usr/bin/env python3
"""Writes src/Passenger.Workspace/GodotDefaultTheme.json, the table of Godot's default theme Passenger carries, from Godot's
own sources at one commit (MIT, named in THIRD_PARTY_NOTICES.md):

- scene/theme/default_theme.cpp: each theme type's items by kind (colors, constants, fonts, font_sizes, icons, styles)
  and the type variations it makes (HeaderSmall of Label);
- scene/scene_string_names.h and core/core_string_names.h: the names its StringName constants stand for;
- scene/gui's and scene/main's headers: each Control's or Window's class's base (GDCLASS), along which it looks items up;
- doc/classes/<class>.xml: the class reference's description of each theme item a class documents, its BBCode made words.
Fetched once into artifacts/godot-theme/<commit>/; COMMIT is Godot 4.8 beta 2's, as tools/godot-shader-types.py's.
"""
import json
import os
import re
import urllib.error
import urllib.request
import xml.etree.ElementTree as ElementTree

COMMIT = "cafc17a192acf67903910a68aeb3c076c0907a4b"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Passenger.Workspace", "GodotDefaultTheme.json")
CACHE = os.path.join(ROOT, "artifacts", "godot-theme", COMMIT)

KINDS = {"color": "colors", "constant": "constants", "font": "fonts", "font_size": "font_sizes", "icon": "icons", "stylebox": "styles"}
NAME = r'(?:"(\w+)"|(?:Scene|Core)StringName\((\w+)\))'
SET = re.compile(r'theme->set_(color|constant|font_size|font|icon|stylebox)\(\s*' + NAME + r'\s*,\s*' + NAME + r'\s*,')
VARIATION = re.compile(r'theme->set_type_variation\(\s*' + NAME + r'\s*,\s*' + NAME + r'\s*\)')
CONSTANT = re.compile(r'const StringName (\w+) = "([^"]*)";')
CLASS = re.compile(r'\bGDCLASS\(\s*(\w+)\s*,\s*(\w+)\s*\)')
DOCUMENTED = {"color": "colors", "constant": "constants", "font": "fonts", "font_size": "font_sizes", "icon": "icons", "style": "styles"}
TAG = re.compile(r"\[(?:/?(?:code|b|i|kbd|codeblock|codeblocks|gdscript|csharp|url[^\]]*|center|br))\]")
REFERENCE = re.compile(r"\[(?:(?:member|method|signal|constant|enum|theme_item|param|annotation|constructor|operator) )?([\w.@]+)\]")


def words(text):
    """A class reference's BBCode as plain words: tags dropped, a reference ([Color], [member text]) its name, one line."""
    said = REFERENCE.sub(lambda m: m[1], TAG.sub("", text or ""))
    return " ".join(said.split())


def documented(name):
    """The theme items a class's reference describes, by kind and name; none where the class has no page."""
    try:
        root = ElementTree.fromstring(fetch(f"doc/classes/{name}.xml"))
    except urllib.error.HTTPError as missing:
        if missing.code == 404:
            return {}
        raise
    items = {}
    for item in root.iter("theme_item"):
        kind = DOCUMENTED.get(item.get("data_type", ""))
        if kind and item.get("name") and words(item.text):
            items.setdefault(kind, {})[item.get("name")] = words(item.text)
    return items


def fetch(path):
    os.makedirs(CACHE, exist_ok=True)
    local = os.path.join(CACHE, path.replace("/", "_"))
    if not os.path.exists(local):
        with urllib.request.urlopen(f"https://raw.githubusercontent.com/godotengine/godot/{COMMIT}/{path}") as response:
            data = response.read()
        with open(local, "wb") as out:
            out.write(data)
    with open(local, encoding="utf-8") as text:
        return text.read()


def headers():
    """The headers of scene/gui and scene/main at the commit, listed once by GitHub's tree of it."""
    local = os.path.join(CACHE, "headers.json")
    if not os.path.exists(local):
        os.makedirs(CACHE, exist_ok=True)
        url = f"https://api.github.com/repos/godotengine/godot/git/trees/{COMMIT}?recursive=1"
        with urllib.request.urlopen(url) as response:
            tree = json.load(response)
        listed = [e["path"] for e in tree["tree"] if re.match(r"scene/(gui|main)/[^/]+\.h$", e["path"])]
        with open(local, "w", encoding="utf-8") as out:
            json.dump(sorted(listed), out)
    with open(local, encoding="utf-8") as text:
        return json.load(text)


def main():
    names = {}
    for path in ["scene/scene_string_names.h", "core/core_string_names.h"]:
        names.update(CONSTANT.findall(fetch(path)))

    def said(quoted, constant):
        return quoted if quoted else names[constant]

    source = fetch("scene/theme/default_theme.cpp")
    types = {}
    for m in SET.finditer(source):
        item, kind = said(m[2], m[3]), KINDS[m[1]]
        entry = types.setdefault(said(m[4], m[5]), {})
        if item not in entry.setdefault(kind, []):
            entry[kind].append(item)
    variations = {said(m[1], m[2]): said(m[3], m[4]) for m in VARIATION.finditer(source)}

    bases = {}
    for path in headers():
        for derived, base in CLASS.findall(fetch(path)):
            bases[derived] = base

    def themed(name):
        while name in bases:
            if name in ("Control", "Window"):
                return True
            name = bases[name]
        return False

    kept = {}
    for name in sorted(bases):
        if themed(name):
            at = name
            while at in bases and at not in kept:
                kept[at] = bases[at]
                at = bases[at]
    docs = {}
    for name in sorted(set(kept) | {"Control", "Window"}):
        if items := documented(name):
            docs[name] = {k: dict(sorted(v.items())) for k, v in sorted(items.items())}
    table = {
        "source": f"https://github.com/godotengine/godot/tree/{COMMIT}",
        "bases": dict(sorted(kept.items())),
        "variations": dict(sorted(variations.items())),
        "types": {t: {k: sorted(v) for k, v in sorted(kinds.items())} for t, kinds in sorted(types.items())},
        "docs": docs,
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as out:
        out.write("{\n")
        out.write(f' "source": {json.dumps(table["source"])},\n')
        out.write(' "bases": {\n' + ",\n".join(f"  {json.dumps(k)}: {json.dumps(v)}" for k, v in table["bases"].items()) + "\n },\n")
        out.write(' "variations": {\n' + ",\n".join(f"  {json.dumps(k)}: {json.dumps(v)}" for k, v in table["variations"].items()) + "\n },\n")
        out.write(' "types": {\n' + ",\n".join(f"  {json.dumps(t)}: {json.dumps(kinds)}" for t, kinds in table["types"].items()) + "\n },\n")
        out.write(' "docs": {\n' + ",\n".join(f"  {json.dumps(t)}: {json.dumps(kinds, ensure_ascii=False)}" for t, kinds in table["docs"].items()) + "\n }\n")
        out.write("}\n")
    count = sum(len(v) for kinds in table["types"].values() for v in kinds.values())
    described = sum(len(v) for kinds in table["docs"].values() for v in kinds.values())
    print(f"Wrote {os.path.relpath(OUT, ROOT)}: {count} items of {len(table['types'])} theme types, {len(table['bases'])} classes, {described} described")


if __name__ == "__main__":
    main()
