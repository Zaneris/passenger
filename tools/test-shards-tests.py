#!/usr/bin/env python3
"""Exercise the shard CLI with real discovered tests and temporary collection/timing conventions."""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if len(sys.argv) != 2:
    sys.exit("usage: test-shards-tests.py SUITE (dotnet test --list-tests output)")
with open(sys.argv.pop(), encoding="utf-8", errors="replace") as file:
    DISCOVERED = file.read().splitlines()


class ShardCliTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = self.temporary.name
        self.tools = os.path.join(self.root, "tools")
        self.source = os.path.join(self.root, "tests", "Passenger.Tests")
        os.makedirs(self.tools)
        os.makedirs(os.path.join(self.source, "Debugging", "Helpers"))
        for name in ("test-shards.py", "test-times.py"):
            shutil.copyfile(os.path.join(ROOT, "tools", name), os.path.join(self.tools, name))
        for name in ("Collections.cs", os.path.join("Debugging", "Helpers", "DebugSession.cs")):
            shutil.copyfile(os.path.join(ROOT, "tests", "Passenger.Tests", name), os.path.join(self.source, name))
        self.groups = {"PackageSourcesTests": ("", 100), "NewSolutionTests": ("", 100),
                       "Debugging.StepTests": ("DebugSession.Collection", 90),
                       "Debugging.LaunchTests": ("DebugSession.Collection", 90),
                       "CodeAssistTests": ("Alone.Name", 10), "McpTests": ("Alone.Name", 10)}
        self.tests = []
        for name, (collection, _) in self.groups.items():
            tests = [line for line in DISCOVERED if line.startswith("    Passenger.Tests." + name + ".")]
            self.assertTrue(tests, f"The real suite no longer discovers {name}; update the CLI fixture")
            self.tests.extend(tests)
            namespace, _, short = ("Passenger.Tests." + name).rpartition(".")
            attribute = f"[Collection({collection})]\n" if collection else ""
            self.write(name + ".cs", f"namespace {namespace};\n{attribute}public sealed partial class {short} {{}}\n", self.source)
        self.write("Mcp-part.cs", "namespace Passenger.Tests;\npublic sealed partial class McpTests {}\n", self.source)
        self.suite = self.write("suite.txt", "The following Tests are available:\n" + "\n".join(self.tests) + "\n")
        self.times = self.write("times.json", json.dumps({"macOS": {name: seconds for name, (_, seconds) in self.groups.items()}}))

    def write(self, name, text, folder=None):
        path = os.path.join(folder or self.root, name)
        with open(path, "w", encoding="utf-8") as file:
            file.write(text)
        return path

    def cli(self, *arguments):
        return subprocess.run([sys.executable, "-X", "utf8", "-I", os.path.join(self.tools, "test-shards.py"), *arguments],
                              text=True, encoding="utf-8", capture_output=True, timeout=30)

    def split(self, out):
        return self.cli("split", self.suite, "--system", "macOS", "--of", "2", "--times", self.times, "--out", out)

    def test_filters_use_parallel_collections_deterministically_and_check_exact_coverage(self):
        first = os.path.join(self.root, "first")
        result = self.split(first)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, result.stdout.count("about 1.8 minutes"))
        listed = []
        selected = []
        for number in (1, 2):
            with open(os.path.join(first, f"shard-{number}.runsettings"), encoding="utf-8") as file:
                settings = file.read()
            classes = re.findall(r"FullyQualifiedName~Passenger\.Tests\.([\w.]+)\.", settings)
            selected.extend(classes)
            tests = [line for line in self.tests if any(line.startswith("    Passenger.Tests." + name + ".") for name in classes)]
            listed.append(self.write(f"listed-{number}.txt", "The following Tests are available:\n" + "\n".join(tests) + "\n"))
        self.assertEqual(sorted(self.groups), sorted(selected))
        checked = self.cli("check", self.suite, *listed)
        self.assertEqual(0, checked.returncode, checked.stdout + checked.stderr)
        self.assertIn(f"Each of the suite's {len(self.tests)} tests is in exactly one", checked.stdout)
        self.write("times.json", json.dumps({"macOS": {name: seconds for name, (_, seconds) in reversed(list(self.groups.items()))}}))
        second = os.path.join(self.root, "second")
        repeated = self.split(second)
        self.assertEqual(result.stdout, repeated.stdout)
        for number in (1, 2):
            with open(os.path.join(first, f"shard-{number}.runsettings"), encoding="utf-8") as before:
                with open(os.path.join(second, f"shard-{number}.runsettings"), encoding="utf-8") as after:
                    self.assertEqual(before.read(), after.read())
        duplicate = self.cli("check", self.suite, *listed, listed[0])
        self.assertNotEqual(0, duplicate.returncode)
        self.assertIn("more than one shard", duplicate.stdout)

    def test_unknown_collections_and_changed_protocol_conventions_are_refused(self):
        for attribute in ("[Collection(Another.Name)]", "[Collection(\nAlone.Name)]"):
            with self.subTest(attribute=attribute):
                self.write("CodeAssistTests.cs", "namespace Passenger.Tests;\n" + attribute + "\npublic sealed class CodeAssistTests {}\n", self.source)
                result = self.split(os.path.join(self.root, "unsupported"))
                self.assertNotEqual(0, result.returncode)
                self.assertRegex(result.stderr, "unsupported|convention")
        self.write("CodeAssistTests.cs", "namespace Passenger.Tests;\n[Collection(Alone.Name)]\npublic sealed class CodeAssistTests {}\n", self.source)
        self.write("DebugSession.cs", 'public sealed class DebugSession { public const string Collection = "Alone"; }',
                   os.path.join(self.source, "Debugging", "Helpers"))
        result = self.split(os.path.join(self.root, "changed-protocol"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("DebugSession.Collection changed", result.stderr)

    def test_decimal_finish_ties_use_the_same_filters_and_exact_coverage(self):
        cases = [
            ({"PackageSourcesTests": 0.1, "NewSolutionTests": 0.1, "Debugging.StepTests": 0.1,
              "Debugging.LaunchTests": 0.2, "CodeAssistTests": 0.4, "McpTests": 0.4},
             [{"CodeAssistTests", "Debugging.LaunchTests", "PackageSourcesTests"},
              {"McpTests", "Debugging.StepTests", "NewSolutionTests"}]),
            ({"PackageSourcesTests": 0.1, "NewSolutionTests": 0.1, "Debugging.StepTests": 0.1,
              "Debugging.LaunchTests": 0.3, "CodeAssistTests": 0.1, "McpTests": 0.1},
             [{"Debugging.LaunchTests", "NewSolutionTests"},
              {"CodeAssistTests", "Debugging.StepTests", "McpTests", "PackageSourcesTests"}]),
        ]
        for number, (seconds, expected) in enumerate(cases):
            with self.subTest(seconds=seconds):
                self.write("times.json", json.dumps({"macOS": seconds}))
                out = os.path.join(self.root, f"decimal-{number}")
                result = self.split(out)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(2, result.stdout.count("about 0.0 minutes"))
                listed = []
                for shard, hand in enumerate(expected, 1):
                    with open(os.path.join(out, f"shard-{shard}.runsettings"), encoding="utf-8") as file:
                        selected = set(re.findall(r"FullyQualifiedName~Passenger\.Tests\.([\w.]+)\.", file.read()))
                    self.assertEqual(hand, selected)
                    tests = [line for line in self.tests if any(line.startswith("    Passenger.Tests." + name + ".") for name in selected)]
                    listed.append(self.write(f"decimal-{number}-{shard}.txt", "The following Tests are available:\n" + "\n".join(tests) + "\n"))
                checked = self.cli("check", self.suite, *listed)
                self.assertEqual(0, checked.returncode, checked.stdout + checked.stderr)
                self.write("times.json", json.dumps({"macOS": dict(reversed(list(seconds.items())))}))
                self.assertEqual(result.stdout, self.split(out).stdout)

    def test_indented_collection_definition_is_not_silently_ignored(self):
        self.write("Extra.cs", 'namespace Passenger.Tests;\n    [CollectionDefinition("Other")]\npublic sealed class Extra {}\n', self.source)
        result = self.split(os.path.join(self.root, "extra-definition"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("collection definition was added", result.stderr)


if __name__ == "__main__":
    unittest.main()
