#!/usr/bin/env python3
"""Exercise the packaging mount CLI against changing OS attachment state, and real hdiutil on macOS."""

import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest

TOOL = Path(__file__).with_name("dmg-mount.py")
FAKE = r'''#!/usr/bin/env python3
import json, os, pathlib, plistlib, sys
file = pathlib.Path(os.environ["FAKE_DISKS"])
state = json.loads(file.read_text())
args = sys.argv[1:]
state["calls"].append(args)
mode = os.environ.get("FAKE_MODE")
if args[0] == "attach":
    entities = [{"dev-entry": "/dev/disk7"}, {"dev-entry": "/dev/disk7s1", "mount-point": args[args.index("-mountpoint") + 1]}]
    state["images"] = [{"image-path": args[1], "system-entities": entities}]
    output = {"system-entities": entities}
elif args[0] == "detach":
    if args[1] != "/dev/disk7":
        sys.exit("detach was addressed by mount path instead of its owned device")
    if mode != "lying-detach":
        state["images"] = []
    output = None
elif args[0] == "info":
    output = {} if mode == "bad-info" else {"images": state["images"]}
else:
    sys.exit("unexpected fake OS request")
file.write_text(json.dumps(state))
if args[0] == "attach" and mode == "partial-attach":
    sys.exit("attachment failed after allocating its device")
if output is not None:
    plistlib.dump(output, sys.stdout.buffer)
'''


@unittest.skipIf(os.name == "nt", "The fake OS command uses a Unix executable, as hdiutil does")
class MountCli(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="passenger-dmg-cli-")
        self.root = Path(self.temp.name)
        self.state = self.root / "disks.json"
        self.state.write_text(json.dumps({"images": [], "calls": []}))
        fake = self.root / "hdiutil"
        fake.write_text(FAKE)
        fake.chmod(0o755)
        self.env = dict(os.environ, PATH=str(self.root) + os.pathsep + os.environ["PATH"], FAKE_DISKS=str(self.state))
        self.record = self.root / "mount.json"
        self.image = self.root / "owned image.dmg"
        self.image.touch()
        self.mount = self.root / "mount"
        self.cli("attach", self.record, self.image, self.mount, "-readwrite")

    def tearDown(self):
        self.temp.cleanup()

    def cli(self, *args, success=True):
        result = subprocess.run([sys.executable, "-I", str(TOOL), *map(str, args)], env=self.env, capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
        return result

    def mutate(self, change):
        value = json.loads(self.state.read_text())
        change(value)
        self.state.write_text(json.dumps(value))

    def test_unmounted_volume_is_detached_by_owned_device(self):
        self.mutate(lambda value: value["images"][0]["system-entities"][1].pop("mount-point"))
        self.mount.rmdir()
        self.cli("detach", self.record)
        self.assertFalse(self.record.exists())
        calls = json.loads(self.state.read_text())["calls"]
        self.assertIn(["detach", "/dev/disk7"], calls)
        self.assertEqual(calls[-1], ["info", "-plist"])

    def test_already_detached_image_requires_positive_info_without_detaching(self):
        self.mutate(lambda value: value.update(images=[]))
        self.cli("detach", self.record)
        self.assertFalse(self.record.exists())
        self.assertFalse(any(call[0] == "detach" for call in json.loads(self.state.read_text())["calls"]))

    def test_reused_device_cannot_detach_another_image(self):
        self.mutate(lambda value: value["images"][0].update({"image-path": str(self.root / "another image.dmg")}))
        result = self.cli("detach", self.record, success=False)
        self.assertIn("belongs to another image", result.stderr)
        self.assertTrue(self.record.exists())
        self.assertFalse(any(call[0] == "detach" for call in json.loads(self.state.read_text())["calls"]))

    def test_unknown_attachment_state_cannot_report_cleanup_success(self):
        self.env["FAKE_MODE"] = "bad-info"
        result = self.cli("detach", self.record, success=False)
        self.assertIn("no images array", result.stderr)
        self.assertTrue(self.record.exists())

    def test_successful_detach_exit_does_not_hide_a_remaining_attachment(self):
        self.env["FAKE_MODE"] = "lying-detach"
        result = self.cli("detach", self.record, success=False)
        self.assertIn("remains attached", result.stderr)
        self.assertTrue(self.record.exists())
        calls = [call for call in json.loads(self.state.read_text())["calls"] if call[0] == "detach"]
        self.assertEqual(len(calls), 6)
        self.assertEqual(calls[-1], ["detach", "/dev/disk7", "-force"])

    def test_work_cleanup_refuses_an_unmounted_attached_backing_image(self):
        self.mutate(lambda value: value["images"][0]["system-entities"][1].pop("mount-point"))
        self.cli("clear", self.root, success=False)
        self.assertTrue(self.image.exists())

    def test_failed_attach_keeps_an_owned_record_for_cleanup(self):
        self.cli("detach", self.record)
        self.env["FAKE_MODE"] = "partial-attach"
        self.cli("attach", self.record, self.image, self.mount, "-readwrite", success=False)
        self.assertTrue(self.record.exists())
        self.env.pop("FAKE_MODE")
        self.cli("detach", self.record)
        self.assertFalse(self.record.exists())

    def test_unknown_backing_file_cannot_authorize_work_cleanup(self):
        def forget_ownership(value):
            value["images"][0].pop("image-path")
            value["images"][0]["system-entities"][1].pop("mount-point")

        self.mutate(forget_ownership)
        recorded = self.record.read_text()
        result = self.cli("clear", self.root, success=False)
        self.assertIn("no backing-image path", result.stderr)
        self.assertEqual(self.record.read_text(), recorded)
        self.assertTrue(self.image.exists())
        self.assertTrue(self.mount.is_dir())
        self.assertFalse(any(call[0] == "detach" for call in json.loads(self.state.read_text())["calls"]))


@unittest.skipUnless(sys.platform == "darwin", "Real disk-image acceptance needs macOS")
class NativeMountCli(unittest.TestCase):
    def test_owned_device_cleanup_after_unmount_and_readonly_check(self):
        root = Path(tempfile.mkdtemp(prefix="passenger-dmg-native-"))
        image, mount, record = root / "image.dmg", root / "mount", root / "mount.json"

        def run(*args, success=True):
            result = subprocess.run(list(map(str, args)), capture_output=True, text=True, timeout=120)
            print(result.stdout + result.stderr, flush=True)
            self.assertEqual(result.returncode == 0, success)
            return result

        try:
            run("hdiutil", "create", "-size", "20m", "-fs", "HFS+", "-volname", "Passenger mount check", image)
            run(sys.executable, "-I", TOOL, "attach", record, image, mount, "-readwrite")
            (mount / "checked.txt").write_text("owned image")
            run("diskutil", "unmount", mount)
            info = plistlib.loads(run("hdiutil", "info", "-plist").stdout.encode())
            attached = [item for item in info["images"] if Path(item["image-path"]).resolve() == image.resolve()]
            self.assertEqual(len(attached), 1, "Unmounting must retain the owned disk-image device for this control")
            self.assertFalse(any(entity.get("mount-point") for entity in attached[0]["system-entities"]))
            run(sys.executable, "-I", TOOL, "detach", record)
            self.assertFalse(record.exists())
            run(sys.executable, "-I", TOOL, "attach", record, image, mount, "-readonly")
            self.assertEqual((mount / "checked.txt").read_text(), "owned image")
            run(sys.executable, "-I", TOOL, "detach", record)
            run(sys.executable, "-I", TOOL, "clear", root)
        finally:
            try:
                if record.exists():
                    run(sys.executable, "-I", TOOL, "detach", record)
            finally:
                evidence = Path("artifacts/package/mount-check")
                evidence.mkdir(parents=True, exist_ok=True)
                for name in ("mount-attach.plist", "mount-info.plist", "mount.json"):
                    if (root / name).exists():
                        shutil.copy2(root / name, evidence / name)
                if not record.exists():
                    shutil.rmtree(root)


if __name__ == "__main__":
    unittest.main()
