#!/usr/bin/env python3
"""Own a packaging disk image by its backing file and attached device, even after its volume is unmounted."""

import json
from pathlib import Path
import plistlib
import re
import subprocess
import sys
import time


def run(*args, timeout=30):
    result = subprocess.run(args, capture_output=True, timeout=timeout)
    if result.stderr:
        sys.stderr.buffer.write(result.stderr)
    return result


def read_info(record):
    result = run("hdiutil", "info", "-plist")
    if record is not None:
        record.with_name("mount-info.plist").write_bytes(result.stdout)
    if result.returncode:
        raise RuntimeError("hdiutil info failed; attachment ownership cannot be verified")
    value = plistlib.loads(result.stdout)
    if not isinstance(value, dict) or not isinstance(value.get("images"), list):
        raise RuntimeError("hdiutil info returned no images array")
    for image in value["images"]:
        if not isinstance(image, dict) or not isinstance(image.get("system-entities"), list):
            raise RuntimeError("hdiutil info returned an image without system-entities")
    return value["images"]


def devices(image):
    return {entity.get("dev-entry") for entity in image["system-entities"]}


def disk(image):
    roots = [device for device in devices(image) if isinstance(device, str) and re.fullmatch(r"/dev/disk\d+", device)]
    if len(roots) != 1:
        raise RuntimeError(f"the owned image has no unique whole device: {roots}")
    return roots[0]


def matching(images, path):
    return [image for image in images if isinstance(image.get("image-path"), str) and str(Path(image["image-path"]).resolve()) == path]


def clear(mount):
    for image in read_info(None):
        if isinstance(image.get("image-path"), str) and Path(image["image-path"]).resolve().is_relative_to(mount):
            raise RuntimeError(f"refusing to remove an attached backing image: {image}")
        for entity in image["system-entities"]:
            if isinstance(entity.get("mount-point"), str) and Path(entity["mount-point"]).resolve().is_relative_to(mount):
                raise RuntimeError(f"refusing to remove a work folder containing an attached volume: {image}")


def attach(record, image, mount, options):
    if record.exists():
        raise RuntimeError(f"an attachment is already recorded in {record}")
    mount.mkdir(parents=True, exist_ok=True)
    clear(mount)
    path = str(image.resolve())
    if matching(read_info(record), path):
        raise RuntimeError(f"the image is already attached: {path}")
    owned = {"image": path, "mount": str(mount.resolve()), "device": None}
    record.write_text(json.dumps(owned), encoding="utf-8")
    result = run("hdiutil", "attach", path, *options, "-nobrowse", "-noautoopen", "-mountpoint", owned["mount"], "-plist", timeout=120)
    record.with_name("mount-attach.plist").write_bytes(result.stdout)
    if result.returncode:
        raise RuntimeError(f"hdiutil attach failed for {path}; the recorded image will be checked during cleanup")
    attached = plistlib.loads(result.stdout)
    owned["device"] = disk(attached)
    record.write_text(json.dumps(owned), encoding="utf-8")
    images = matching(read_info(record), path)
    if len(images) != 1 or disk(images[0]) != owned["device"]:
        raise RuntimeError(f"the attached device does not uniquely belong to {path}: {images}")
    if not any(entity.get("mount-point") and Path(entity["mount-point"]).resolve() == mount.resolve() for entity in images[0]["system-entities"]):
        raise RuntimeError(f"the owned image is not mounted at {mount}: {images}")
    print(f"package-mac: attached {path} as {owned['device']} at {owned['mount']}", flush=True)


def detach(record):
    owned = json.loads(record.read_text(encoding="utf-8"))
    path = owned["image"]
    device = owned["device"]
    for attempt in range(6):
        images = read_info(record)
        found = matching(images, path)
        if device is not None and any(device in devices(image) and image not in found for image in images):
            raise RuntimeError(f"refusing to detach {device}: it belongs to another image; expected {path}")
        if not found:
            record.unlink()
            print(f"package-mac: confirmed detached {path} ({device or 'no device was attached'})", flush=True)
            return
        if len(found) != 1:
            raise RuntimeError(f"refusing to detach multiple attachments of {path}: {found}")
        current = disk(found[0])
        if device is not None and current != device:
            raise RuntimeError(f"refusing to detach {current}: {path} was attached as {device}")
        device = current
        owned["device"] = device
        record.write_text(json.dumps(owned), encoding="utf-8")
        print(f"package-mac: detach attempt {attempt + 1}: {path} owns {device}; entities={found[0]['system-entities']}", flush=True)
        run("hdiutil", "detach", device, *(["-force"] if attempt >= 3 else []))
        time.sleep(attempt + 1)
    images = read_info(record)
    if matching(images, path) or any(device in devices(image) for image in images):
        for args in [("diskutil", "info", device), ("lsof", "+D", owned["mount"])]:
            try:
                result = run(*args, timeout=10)
                sys.stderr.buffer.write(result.stdout)
            except (OSError, subprocess.TimeoutExpired) as error:
                print(f"package-mac: cleanup diagnostic unavailable: {error}", file=sys.stderr)
        raise RuntimeError(f"the owned image {path} ({device}) remains attached; see {record.with_name('mount-info.plist')}")
    record.unlink()
    print(f"package-mac: confirmed detached {path} ({device})", flush=True)


def main():
    command, *args = sys.argv[1:]
    if command == "attach" and len(args) >= 3:
        attach(Path(args[0]), Path(args[1]), Path(args[2]), args[3:])
    elif command == "detach" and len(args) == 1:
        detach(Path(args[0]))
    elif command == "clear" and args:
        for path in args:
            clear(Path(path).resolve())
    else:
        raise RuntimeError("usage: dmg-mount.py attach RECORD IMAGE MOUNT [OPTIONS] | detach RECORD | clear WORK [IMAGE]")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.TimeoutExpired) as error:
        sys.exit(f"package-mac: {error}")
