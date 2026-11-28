#!/usr/bin/env python3
"""Writes tests/Passenger.Tests/RiderKeymaps.json from an installed Rider: the keys, double taps and mouse shortcuts each
action RiderKeymaps.cs names has in the seven keymaps Passenger's presets follow ($default, Mac OS X 10.5+, Visual Studio,
Visual Studio OSX, VSCode, VSCode OSX, Emacs), resolved as IntelliJ resolves them: the keymap's own list, else the list of the
action it takes its shortcuts from (use-shortcut-of), else its parent's, Mac OS X 10.5+ taking $default's with Ctrl and
Cmd swapped; plugins' descriptors adding to a keymap's list or putting theirs in its place, the platform's jars first.

Usage: python3 -I tools/rider-keymaps.py [RIDER.app] (~/Applications/Rider.app by default).
"""
import json
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

KEYMAPS = ["$default", "Mac OS X 10.5+", "Visual Studio", "Visual Studio OSX", "VSCode", "VSCode OSX", "Emacs"]
MODIFIERS = {"control": "control", "ctrl": "control", "meta": "meta", "alt": "alt", "shift": "shift"}
ORDER = ["meta", "control", "alt", "shift"]
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TABLE = os.path.join(ROOT, "tests", "Passenger.Tests", "RiderKeymaps.cs")
OUT = os.path.join(ROOT, "tests", "Passenger.Tests", "RiderKeymaps.json")


def ordered(modifiers, rest):
    return " ".join([m for m in ORDER if m in modifiers] + rest)


def stroke(text):
    """A keystroke in Passenger's notation: its modifiers in order, the key's name in capitals."""
    words = text.split()
    return ordered({MODIFIERS[w.lower()] for w in words[:-1]}, [words[-1].upper()])


def shortcut(element):
    """An element's shortcut as Passenger's keymaps write it, or None for one Passenger has no notation for."""
    if element.tag == "keyboard-shortcut":
        first = stroke(element.get("first-keystroke"))
        second = element.get("second-keystroke")
        return f"{first} , {stroke(second)}" if second else first
    if element.tag == "keyboard-gesture-shortcut" and element.get("modifier") == "dblClick":
        modifier = element.get("keystroke").split()[0].lower()
        return f"{MODIFIERS[modifier]} , {MODIFIERS[modifier]}"
    if element.tag == "mouse-shortcut" and "button" in element.get("keystroke"):
        words = element.get("keystroke").split()
        return ordered({MODIFIERS[w.lower()] for w in words if w.lower() in MODIFIERS}, [w for w in words if w.lower() not in MODIFIERS])
    return None


def swapped(text):
    """A $default shortcut as Mac OS X 10.5+ takes it, Ctrl and Cmd swapped; a modifier's double tap stays."""
    parts = text.split(" , ")
    if len(parts) == 2 and parts[0] == parts[1] and len(parts[0].split()) == 1:
        return text
    swap = {"control": "meta", "meta": "control"}

    def one(part):
        words = [swap.get(w, w) for w in part.split()]
        return ordered({w for w in words if w in ORDER}, [w for w in words if w not in ORDER])
    return " , ".join(one(p) for p in parts)


def jars(rider):
    found = []
    for folder, _, files in os.walk(os.path.join(rider, "Contents")):
        found += [os.path.join(folder, f) for f in files if f.endswith(".jar")]
    # The platform's jars before the plugins', as IntelliJ loads their descriptors.
    return sorted(found, key=lambda p: (0 if os.path.relpath(p, rider).startswith(os.path.join("Contents", "lib")) else 1, p))


def read(rider):
    own, parent, bound, edits = {}, {}, {}, []
    for jar in jars(rider):
        try:
            archive = zipfile.ZipFile(jar)
        except zipfile.BadZipFile:
            continue
        for name in archive.namelist():
            if not name.endswith(".xml"):
                continue
            data = archive.read(name)
            if name.startswith("keymaps/") and os.path.basename(name)[:-4] in KEYMAPS:
                keymap = ET.fromstring(data)
                parent[keymap.get("name")] = keymap.get("parent")
                own[keymap.get("name")] = {a.get("id"): [s for s in map(shortcut, a) if s] for a in keymap.findall("action")}
            elif b"use-shortcut-of" in data or b"-shortcut" in data:
                try:
                    tree = ET.fromstring(data)
                except ET.ParseError:
                    continue
                for action in tree.iter():
                    if action.tag not in ("action", "group") or action.get("id") is None:
                        continue
                    if action.get("use-shortcut-of"):
                        bound[action.get("id")] = action.get("use-shortcut-of")
                    edits += [(action.get("id"), c) for c in action if c.tag.endswith("-shortcut") and c.get("keymap") in KEYMAPS]
    return own, parent, bound, edits


def main():
    rider = sys.argv[1] if len(sys.argv) > 1 and sys.argv[1] else os.path.expanduser("~/Applications/Rider.app")
    own, parent, bound, edits = read(rider)
    missing = [k for k in KEYMAPS if k not in own]
    if missing:
        sys.exit(f"{rider} holds no keymap {', '.join(missing)}")

    def resolve(keymap, action):
        if keymap is None:
            return []
        if action in own[keymap]:
            return list(own[keymap][action])
        if bound.get(action) in own[keymap]:
            return list(own[keymap][bound[action]])
        inherited = resolve(parent[keymap], action)
        return [swapped(s) for s in inherited] if keymap == "Mac OS X 10.5+" else inherited

    for action, element in edits:
        keymap, written = element.get("keymap"), shortcut(element)
        listed = own[keymap].setdefault(action, resolve(keymap, action))
        if element.get("remove") == "true":
            listed[:] = [s for s in listed if s != written]
        else:
            if element.get("replace-all") == "true":
                listed.clear()
            if written and written not in listed:
                listed.append(written)

    named = sorted(set(re.findall(r'"([A-Za-z$][\w.$-]*)"', open(TABLE, encoding="utf-8").read())))
    actions = {}
    for action in named:
        keys = {k: resolve(k, action) for k in KEYMAPS}
        if any(keys.values()):
            actions[action] = {k: v for k, v in keys.items() if v}
    with open(os.path.join(rider, "Contents", "Resources", "product-info.json"), encoding="utf-8") as info:
        version = json.load(info)["version"]
    lines = [f"  {json.dumps(a)}: {json.dumps(k, separators=(', ', ': '))}" for a, k in actions.items()]
    with open(OUT, "w", encoding="utf-8", newline="\n") as out:
        out.write('{\n  "rider": ' + json.dumps(version) + ',\n  "actions": {\n  ' + ",\n  ".join(lines) + "\n  }\n}\n")
    print(f"{OUT}: {len(actions)} actions from Rider {version}")


main()
