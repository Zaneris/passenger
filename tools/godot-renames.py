#!/usr/bin/env python3
"""Writes src/Passenger.Workspace/GDScript/GodotRenames.json, the names Godot 4 renamed from Godot 3's that its GDScript
analyzer hints at ("Did you mean to use ...?"), from Godot's own editor/project_upgrade/renames_map_3_to_4.cpp at one
commit (MIT, named in THIRD_PARTY_NOTICES.md): the maps check_for_renamed_identifier reads, in the order it reads them,
each the old name and the new, the first of a name kept as get_rename_from_map takes it. Commented-out entries are left
out, as the compiler leaves them. The source is Godot's at tools/fetched.py's GODOT_COMMIT, the analyzer's port's, fetched
once into artifacts/godot-source/<commit>/.
"""
import importlib.util
import json
import os
import re

_spec = importlib.util.spec_from_file_location("fetched", os.path.join(os.path.dirname(os.path.abspath(__file__)), "fetched.py"))
fetched = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fetched)

SOURCE = "editor/project_upgrade/renames_map_3_to_4.cpp"
ROOT = fetched.ROOT
OUT = os.path.join(ROOT, "src", "Passenger.Workspace", "GDScript", "GodotRenames.json")

# What an identifier's hint reads, then what a call's does (gdscript_analyzer.cpp's check_for_renamed_identifier).
IDENTIFIER = ["gdscript_properties_renames", "enum_renames", "color_renames", "class_renames", "builtin_types_renames"]
CALL = ["gdscript_function_renames", "builtin_types_renames"]
ENTRY = re.compile(r'^\s*\{\s*"([^"]*)",\s*"([^"]*)"\s*\},')


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
    read = maps(fetched.godot(SOURCE))
    table = {"commit": fetched.GODOT_COMMIT, "identifiers": first_of_each(IDENTIFIER, read), "calls": first_of_each(CALL, read)}
    with open(OUT, "w", encoding="utf-8", newline="\n") as out:
        json.dump(table, out, indent=0, sort_keys=False, ensure_ascii=False)
        out.write("\n")
    print(f"{OUT}: {len(table['identifiers'])} identifiers, {len(table['calls'])} calls")


if __name__ == "__main__":
    main()
