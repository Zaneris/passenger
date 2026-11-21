"""What tools/unicode-widths.py and tools/unicode-identifiers.py share of reading the Unicode Character Database: its
files fetched once into artifacts/unicode/<version>/ (tools/fetched.py), their rows, a row's code points, and runs of
code points."""
import importlib.util
import os

_spec = importlib.util.spec_from_file_location("fetched", os.path.join(os.path.dirname(os.path.abspath(__file__)), "fetched.py"))
fetched = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fetched)

ROOT = fetched.ROOT


def cache(version):
    """The folder a version's files are kept in."""
    return os.path.join(ROOT, "artifacts", "unicode", version)


def fetch(files, name, folder):
    """A file's text, fetched from its address in files the first time and kept in folder; a byte order mark is dropped."""
    return fetched.text(files[name], os.path.join(folder, name), encoding="utf-8-sig")


def rows(text):
    """Each data line's fields, its comment and blank lines left out."""
    for line in text.splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            yield [field.strip() for field in line.split(";")]


def points(field):
    """The code points of a field: one, or a range written first..last."""
    if ".." in field:
        first, last = field.split("..")
        return range(int(first, 16), int(last, 16) + 1)
    return [int(field, 16)]


def ranges(values):
    """Runs of consecutive code points with one value, from a mapping of code point to value: [first, last, value]."""
    out = []
    for point in sorted(values):
        value = values[point]
        if out and out[-1][1] == point - 1 and out[-1][2] == value:
            out[-1][1] = point
        else:
            out.append([point, point, value])
    return out
