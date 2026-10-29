#!/bin/sh
# Passenger's own icons from src/Passenger.App/Assets/passenger.svg (`just app-icon`, on macOS, since sips draws them):
# Passenger.icns at each size macOS asks of an app icon, passenger.png at 256 pixels for the IDE's mark and Linux's
# desktop entry, and Passenger.ico at the sizes Windows draws Passenger.exe's and every window's icon, each a PNG inside.
# Every picture the three files hold is read back, failing the script unless its corner is fully transparent.
set -eu
fail() {
    echo "app-icon: $*" >&2
    exit 1
}
root=$(cd "$(dirname "$0")/.." && pwd)
assets="$root/src/Passenger.App/Assets"
svg="$assets/passenger.svg"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
set_dir="$work/Passenger.iconset"
mkdir -p "$set_dir"
# Each size drawn from the SVG itself, since one scaled down from a larger picture keeps a fringe round its edge. sips
# says nothing in its exit status of a file it skipped, so the picture's absence says it.
draw() {
    rm -f "$2"
    sips -s format png -z "$1" "$1" "$svg" --out "$2" >/dev/null || true
    [ -s "$2" ] || fail "sips drew no $1-pixel picture of $svg (does this macOS's sips read SVG?)"
}
for size in 16 32 128 256 512; do
    draw "$size" "$set_dir/icon_${size}x${size}.png"
    draw $((size * 2)) "$set_dir/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$set_dir" -o "$assets/Passenger.icns"
draw 256 "$assets/passenger.png"
ico_sizes="16 20 24 32 40 48 64 128 256"
for size in $ico_sizes; do
    draw "$size" "$work/ico-$size.png"
done
# An ICONDIR, an entry of 16 bytes per image (a width or height of 256 is written 0), then the PNGs themselves.
python3 -I - "$assets/Passenger.ico" $(for size in $ico_sizes; do echo "$work/ico-$size.png"; done) <<'PY'
import struct, sys
images = [open(path, "rb").read() for path in sys.argv[2:]]
offset = 6 + 16 * len(images)
head = struct.pack("<HHH", 0, 1, len(images))
for png in images:
    width, height = struct.unpack(">II", png[16:24])
    head += struct.pack("<BBBBHHII", width % 256, height % 256, 0, 0, 1, 32, len(png), offset)
    offset += len(png)
with open(sys.argv[1], "wb") as ico:
    ico.write(head + b"".join(images))
PY

iconutil -c iconset "$assets/Passenger.icns" -o "$work/shipped.iconset"
python3 -I - "$assets/Passenger.icns" "$work/shipped.iconset" "$assets/Passenger.ico" "$assets/passenger.png" <<'PY'
import os, struct, sys, zlib


def corner(png, name):
    """The top-left pixel's RGBA: an 8-bit RGBA PNG's first four bytes once inflated, past the row's filter byte, which
    no filter changes, since that pixel has none to its left or above."""
    if png[:8] != b"\x89PNG\r\n\x1a\n":
        sys.exit(f"app-icon: {name} is not a PNG")
    width, _, depth, kind = struct.unpack(">IIBB", png[16:26])
    if (depth, kind) != (8, 6):
        sys.exit(f"app-icon: {name} is not 8-bit RGBA (depth {depth}, colour type {kind}), so it holds no transparency to check")
    data, at = b"", 8
    while at < len(png):
        length, chunk = struct.unpack(">I4s", png[at:at + 8])
        if chunk == b"IDAT":
            data += png[at + 8:at + 8 + length]
        at += 12 + length
    return width, tuple(zlib.decompressobj().decompress(data, 5)[1:5])


def check(png, name):
    width, pixel = corner(png, name)
    if pixel[3] != 0:
        sys.exit(f"app-icon: {name}'s corner is {pixel} (RGBA), not transparent: the icon would be a square of it on the Dock or the taskbar")
    return width


icns, iconset, ico, window = sys.argv[1:]
packed = sorted(os.listdir(iconset), key=lambda n: (int(n.split("_")[1].split("x")[0]), "@2x" in n))
for name in packed:
    check(open(os.path.join(iconset, name), "rb").read(), f"{os.path.basename(icns)} {name}")
data = open(ico, "rb").read()
count = struct.unpack("<H", data[4:6])[0]
sizes = []
for entry in range(count):
    at = 6 + 16 * entry
    length, offset = struct.unpack("<II", data[at + 8:at + 16])
    sizes.append(check(data[offset:offset + length], f"{os.path.basename(ico)}'s {data[at] or 256}-pixel picture"))
check(open(window, "rb").read(), os.path.basename(window))
names = ", ".join(n.removeprefix("icon_").removesuffix(".png") for n in packed)
print(f"app-icon: transparent corners in {os.path.basename(icns)} ({names}), {os.path.basename(ico)} ({', '.join(map(str, sizes))}) and {os.path.basename(window)}")
PY
