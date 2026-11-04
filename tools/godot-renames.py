#!/usr/bin/env python3
"""Writes src/Passenger.Workspace/GDScript/GodotRenames.json, the names Godot 4 renamed from Godot 3's that its GDScript
analyzer hints at ("Did you mean to use ...?"), from Godot's own editor/project_upgrade/renames_map_3_to_4.cpp at one
commit (MIT, named in THIRD_PARTY_NOTICES.md): the maps check_for_renamed_identifier reads, in the order it reads them,
each the old name and the new, the first of a name kept as get_rename_from_map takes it. Commented-out entries are left
out, as the compiler leaves them. The source is fetched once into artifacts/godot-renames/<commit>/; COMMIT is Godot 4.8
beta 2's, the analyzer's port's.
"""
import json
import os
import re
import urllib.request

COMMIT = "cafc17a192acf67903910a68aeb3c076c0907a4b"
SOURCE = "editor/project_upgrade/renames_map_3_to_4.cpp"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Passenger.Workspace", "GDScript", "GodotRenames.json")

# What an identifier's hint reads, then what a call's does (gdscript_analyzer.cpp's check_for_renamed_identifier).
IDENTIFIER = ["gdscript_properties_renames", "enum_renames", "color_renames", "class_renames", "builtin_types_renames"]
CALL = ["gdscript_function_renames", "builtin_types_renames"]
ENTRY = re.compile(r'^\s*\{\s*"([^"]*)",\s*"([^"]*)"\s*\},')


def fetch():
    cache = os.path.join(ROOT, "artifacts", "godot-renames", COMMIT)
    os.makedirs(cache, exist_ok=True)
    local = os.path.join(cache, os.path.basename(SOURCE))
    if not os.path.exists(local):
        with urllib.request.urlopen(f"https://raw.githubusercontent.com/godotengine/godot/{COMMIT}/{SOURCE}") as response:
            data = response.read()
        with open(local, "wb") as out:
            out.write(data)
    with open(local, encoding="utf-8") as text:
        return text.read()


def maps(source):
    found = {}
    current = None
    for line in source.splitlines():
        if m := re.match(r'^const char \*RenamesMap3To4::(\w+)\[\]\[2\] = \{', line):
            current = found.setdefault(m[1], [])
        elif current is not None and line.startswith("};"):
            current = None
        elif current is not None and (m := ENTRY.match(line)) and not line.lstrip().startswith("//"):
            current.append([m[1], m[2]])
    return found


def first_of_each(names, read):
    kept = {}
    for name in names:
        for old, new in read[name]:
            kept.setdefault(old, new)
    return kept


def main():
    read = maps(fetch())
    table = {"commit": COMMIT, "identifiers": first_of_each(IDENTIFIER, read), "calls": first_of_each(CALL, read)}
    with open(OUT, "w", encoding="utf-8", newline="\n") as out:
        json.dump(table, out, indent=0, sort_keys=False, ensure_ascii=False)
        out.write("\n")
    print(f"{OUT}: {len(table['identifiers'])} identifiers, {len(table['calls'])} calls")


if __name__ == "__main__":
    main()
