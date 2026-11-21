"""What the recipes reading another project's files share: Godot's commit, which the GDScript port and its tables are read
from, and a file fetched once into artifacts/ and kept, written whole or not at all, so a fetch cut short leaves nothing
a later run trusts. A recipe runs `python3 -I`, which leaves its own folder off the path, so each loads this file by its
path, as tools/unicode-widths.py loads tools/unicode.py."""
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Godot 4.8 beta 2's commit (godotengine/godot-builds names it), the one GDScript's analyzer and Godot's shader checker
# are ported from: its renames, shader types and default theme are read there too.
GODOT_COMMIT = "cafc17a192acf67903910a68aeb3c076c0907a4b"


def cached(url, path):
    """path, the file at url fetched into it the first time and kept: written beside it, then moved into its place."""
    if not os.path.exists(path):
        with urllib.request.urlopen(url) as response:
            data = response.read()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        part = path + ".part"
        with open(part, "wb") as out:
            out.write(data)
        os.replace(part, path)
    return path


def text(url, path, encoding="utf-8"):
    """The text of the file at url, kept at path."""
    with open(cached(url, path), encoding=encoding) as read:
        return read.read()


def godot(path, at=GODOT_COMMIT):
    """The text of a file of Godot's sources at a commit or tag, kept in artifacts/godot-source/<at>/ under its own path."""
    url = f"https://raw.githubusercontent.com/godotengine/godot/{at}/{path}"
    return text(url, os.path.join(ROOT, "artifacts", "godot-source", at, *path.split("/")))

