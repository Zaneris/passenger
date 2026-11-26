#!/usr/bin/env python3
"""Which of a system's CI shards runs each test class, and the checks that every test runs in exactly one of them.

A lane is a part of the suite: the gate's [Gate] tests and the unit tests, the report lane's every other test (off
ONE_SYSTEM, none marked [SameOnEverySystem]; on MAC_OS, only those marked [AboutMacOS]), or all. `list` writes a lane as
`dotnet test --list-tests` prints it, again while a project names no test or says discovery failed, failing above the
suite's most tests. `split` deals its classes by their seconds in the lane on that system in TestTimes.json (else the
others'), the longest first, each where its shard ends soonest by its kinds of class (one in another lane's job from
that one's end, one in a job building the check first from that build's end), and to one of a job's test processes, the
same in every shard; `check` fails unless every test is in exactly one. `test` runs a hand; `ended` names the classes it
did not run whole; `cover` fails unless every test has one result; `failures` tables misses; `update` writes the times."""
import argparse
import ctypes
import glob
import importlib.util
import json
import os
import platform
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from collections import Counter
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TIMES = os.path.join(ROOT, "tests", "Passenger.Tests", "TestTimes.json")
PROJECT = os.path.join(ROOT, "tests", "Passenger.Tests")
# Pure logic's tests, every one in the gate; a checkout may not have them yet.
UNIT_PROJECT = os.path.join(ROOT, "tests", "Passenger.UnitTests")
UNIT_PREFIX = "Passenger.UnitTests."
# A test class's seconds while TestTimes.json has none for it, to deal it by; a unit test class's tests take
# milliseconds.
CLASS_SECONDS = 60
UNIT_CLASS_SECONDS = 1
SYSTEMS = ("macOS", "Windows", "Linux")
LANES = ("gate", "report", "all")
# The trait [Gate] gives a test (tests/Passenger.Tests/Gate.cs).
GATE = "Gate"
# The trait [SameOnEverySystem] gives a report class whose subject behaves the same on every system
# (tests/Passenger.Tests/SameOnEverySystem.cs), and the one system whose report lane runs it: the others leave it out.
SAME = "SameOnEverySystem"
ONE_SYSTEM = "Linux"
# The trait [AboutMacOS] gives a report class that reaches macOS's own behaviour (tests/Passenger.Tests/AboutMacOS.cs),
# and the system whose report lane runs only those: the others stand in for it in every other report class.
ABOUT_MAC_OS = "AboutMacOS"
MAC_OS = "macOS"
# How long a class's tests take on each system in each of a job's test processes, by how many there are, for each of
# their seconds in a process alone (TestTimes.json's): the deal's model of a job of several, and the measure by which
# `update` keeps the seconds a lone process's: the median `cover` reported for each system's report lane, for two in CI
# 44824859727, for three on Windows in CI 44889105427.
PACED = {"macOS": {2: 1.21}, "Windows": {2: 1.07, 3: 1.41}, "Linux": {2: 1.06}}
# How many test processes a job may run at once.
PROCESSES = (1, 2, 3)
# How much later, in seconds, the job of a lane's first shard begins its tests than another job where it builds the
# solution's check first (test.yml's Build: every analyzer and code-style rule) and lists each shard's hand (--check): the
# median over CI 44836100225, 44848029756, 44855772233, 44872855052 and 44879987936 of that job's time before its tests
# against the others' median.
CHECKED_FIRST = {"macOS": 270, "Windows": 145, "Linux": 65}
# How much later, in seconds, the job of a shard --apart-shards names begins its tests than one setting up nothing (the
# mobile workloads, and on Linux an emulator): the median over CI 44995132904, 45000646248, 45009557197, 45016390450
# and 45022036262 of the report lane's first shard's time before its tests against its third's.
APART_FIRST = {"macOS": 220, "Windows": 0, "Linux": 80}
# How many times their seconds the classes --apart names are dealt at: their devices ran them from 0.8 to 1.5 times
# their seconds in a shard from run to run (CI 45022036262 to 45055574992), and as the shard runs them last, a slow
# run of them leaves the shard room before the job's TEST_MINUTES rather than leaving them unreached.
APART_MARGIN = 1.25
# How many times its seconds every other class dealt to a shard --apart-shards names takes, its job having set up for
# the classes apart: those shards' window, Beside and Alone classes' minutes against their deal over the other shards'
# on the same runners, each the median over CI 45055574992, 45062369510 and 45069301166 (macOS 1.35 against 1.01 on
# the image with Xcode 27 and the mobile workloads, Linux 1.21 against 0.96 beside the emulator it boots).
SET_UP_PACE = {"macOS": 1.33, "Windows": 1.0, "Linux": 1.26}
# What xunit's adapter writes into the listing when it could not discover an assembly's tests.
DISCOVERY_FAILED = "Exception discovering tests"
# How much sooner the slowest shard must end, dealt from a run's own times, before the summary asks for a new deal.
WORTH_DEALING_AGAIN = 120
# How many times a listing that names no test or says discovery failed (xunit #3576) is asked for.
TRIES = 3
# The most tests the suite may list, unit and window tests, gate and report lane together, every case of a theory
# counted (#2095): a new check joins a flow's step or a table's row where one fits (tests/Shared).
MOST_TESTS = 2000
# How many more it may list for now, while #2095's batches bring it down, lowered as each lands until it is 0.
ALLOWANCE = 0
# A hung test is stopped and named after this, rather than holding its run or its shard's job to the job's own limit;
# in minutes, as the tests read it for their long waits to end within (Limits.Hang).
HANG = "10m"
# On macOS blame dumps every .NET process of a hung run whole, whatever type is asked ("none" too), about 7 GB each.
MAC_OS_DUMPS_WHOLE = platform.system() == "Darwin"
# What every dotnet test of the tests is given, so a test hung for HANG is stopped and named (`hang` prints it), a mini
# dump of the test host and of each program under it taken first (blame's), but on macOS, where the hung test host ends
# itself once its report is written, a minute before blame would dump it (HangReports in the tests).
HANG_ARGUMENTS = ["--blame-hang-timeout", HANG, "--blame-hang-dump-type", "none" if MAC_OS_DUMPS_WHOLE else "mini"]
# The folder within a run's results that keeps what a hang leaves: blame's dumps, each once (VSTEST_DUMP_PATH, where
# blame would write them in a temporary folder and then copy them twice into the results), and the report a test still
# running near HANG writes (PASSENGER_TEST_HANGS): the tests running, what the IDE stood on and its log's end, and each
# .NET program under the tests with its threads' stacks. A whole dump macOS's blame took anyway goes where nothing keeps it.
HANGS = "hangs"
DUMPS_VARIABLE = "VSTEST_DUMP_PATH"
REPORTS_VARIABLE = "PASSENGER_TEST_HANGS"
# What a shard's dotnet test of a project wrote to its console, kept beside its results as it came (`test`), so a shard
# stopped partway leaves what it ran.
CONSOLE_LOG = "console-{}.log"
# The classes a shard did not run whole, one a line: the class, its tests that ran and its tests listed (`ended`).
UNREACHED = "unreached.txt"
# Which shard a results folder holds and how its job ended (`ended`), for the summary jobs.
SHARD_STATE = "shard.json"
# The second each shard of a deal begins its tests at, counted from its job's first tests (`split`).
STARTS = "starts.json"
# Each run of dotnet test a shard's job makes, in steps, the runs of a step at once, by name (`split`, read by `test`).
RUNS = "shard-{}.runs.json"
# How far the machine's free memory may fall while a job's tests run before the job says so (`test`).
MEMORY_MARGIN = 2 << 30
# How long the command a shard runs before its classes apart (`test --before-apart`) is given, in seconds, beyond the
# limits it holds itself to.
BEFORE_APART_LIMIT = 15 * 60
# A test's result as the console logger writes it, with how long it took: "  Passed NAME [1 m 3 s]".
RESULT_LINE = re.compile(r"^  (?P<outcome>Passed|Failed|Skipped) (?P<name>Passenger\.(?:Unit)?Tests\.\S.*?)"
                         r"(?: \[(?P<took>[^\]]*)\])?\s*$")

# Classes that share something of the machine beyond the run's own folder, which `just test` runs in one process together
# beside the Alone classes' phase: the container engine (its virtual machine's processors, image builds, Compose), by what
# a class's source calls.
TOGETHER = {
    "the container engine": re.compile(r"\bDocker\.(?:WhyNot|Run|Ask|Said|ComposeUp|Container|State|VolumeExists|Stopped"
                                       r"|TakenDown|NoneLeft|Images?)\b|\bContainerPrograms\b|\bStandInEngine\b"
                                       r"|\bContainerEngine\.Shared\b"),
}
# What only some shards' jobs give the machine (test.yml), by what a class's source names, so every class needing it is
# dealt to those: .NET's mobile workloads, the debug relays, an Android emulator or Xcode's simulators and MAUI's packages
# for a mobile platform, the shards --apart-shards names, which run its classes last, apart from the rest dealt to them
# (--apart), as their devices' times are their own; Aspire's dashboard and orchestrator, with the run's own aspire CLI,
# which the build downloads (PassengerAspireRuntime), the second shard's, beside the classes dealt there too (--second).
SET_UPS = {
    "aspire": re.compile(r"\bAspire(?:Functions)?App\.(?:IsUnavailable|Unavailable|IsCliUnavailable|CliUnavailable"
                         r"|AppHostSkipped|CliSkipped)\b|\bAspirePackageApp\.RunSkipped\b"),
    "mobile": re.compile(r"\bAndroidDevices\.\w|\bAppleSimulators\.\w|\bSimulatorApp\b|\bMacCatalystApp\b"
                         r"|does not restore here from the package cache alone|lacks a workload of the app's heads"),
}
# The classes a deal ran apart from the rest, last in their shard in one test process, each Alone class in a dotnet test
# of its own (runs_of), the shards it gave them to, and those it gave the second shard, by name, beside its settings
# (split, read by cover).
APART = "apart.json"
# The collections that run beside the window tests' own in a process (Collections.cs).
BESIDE = ("Collection(DebugSession.Collection)", "Collection(Beside.Name)")
DECLARATION = re.compile(r"^(?P<attributes>(?:\[[^\n]*\][ \t]*\r?\n)*)(?:(?:public|internal|sealed|static|partial|abstract"
                         r"|file)\s+)*class\s+(?P<name>\w+)(?P<rest>[^\n{]*)", re.M)
NAMESPACE = re.compile(r"^namespace\s+(?P<name>[\w.]+)\s*;", re.M)

# test-times.py's .trx reading, loaded by its file since -I keeps this folder off the path, leaving no __pycache__.
sys.dont_write_bytecode = True
_spec = importlib.util.spec_from_file_location("test_times", os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                                          "test-times.py"))
trx = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(trx)
PREFIX = trx.PREFIX


def write(line):
    """A line into the log in UTF-8, as GitHub reads it, whatever Python's own encoding (cp1252 on Windows) can hold."""
    sys.stdout.flush()
    sys.stdout.buffer.write((line + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()


def command_text(text, property=False):
    """Text for a workflow command, in its own escapes: '%' and line breaks, and in a property ':' and ',' too."""
    text = text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    return text.replace(":", "%3A").replace(",", "%2C") if property else text


def say(kind, title, message):
    write(f"::{kind} title={command_text(title, True)}::{command_text(message)}"
          if os.environ.get("GITHUB_ACTIONS") == "true" else f"{kind}: {message}")


def summarize(lines):
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as file:
            file.write("\n".join(lines) + "\n\n")


def some(names):
    return ", ".join(names[:20]) + (" …" if len(names) > 20 else "")


# dotnet test's listing cuts a display name longer than this to its first so many characters and "···"; a .trx keeps it.
LISTED_LENGTH = 444


def key(name):
    """A test's display name as both dotnet test's listing and a .trx carry it: a long one by as much as the listing
    keeps, and, as a Windows console writes what it cannot encode its own way, every character past ASCII as one '?'."""
    return "".join(c if c.isascii() else "?" for c in name[:LISTED_LENGTH])


def unit_tests():
    """Whether the checkout has the unit tests' project."""
    return os.path.isfile(os.path.join(UNIT_PROJECT, "Passenger.UnitTests.csproj"))


def projects(lane):
    """The test projects holding a lane's tests: the unit tests' only in the gate (and in all)."""
    return [PROJECT] + ([UNIT_PROJECT] if lane != "report" and unit_tests() else [])


def lane_filter(lane, system=None):
    """dotnet test's filter that selects a lane's tests from its projects, empty for all; on a system but ONE_SYSTEM, the
    report lane's leaves out the classes marked [SameOnEverySystem], which ONE_SYSTEM's runs, and on MAC_OS it takes only
    the classes marked [AboutMacOS]."""
    chosen = {"gate": f"{GATE}=true|FullyQualifiedName~{UNIT_PREFIX}",
              "report": f"{GATE}!=true&FullyQualifiedName!~{UNIT_PREFIX}", "all": ""}[lane]
    return (chosen + (f"&{SAME}!=true" if lane == "report" and system not in (None, ONE_SYSTEM) else "")
            + (f"&{ABOUT_MAC_OS}=true" if lane == "report" and system == MAC_OS else ""))


def both(*filters):
    """dotnet test's filter selecting what every one of the given filters (an empty one selecting all) selects."""
    given = [f for f in filters if f]
    return given[0] if len(given) == 1 else "&".join(f"({f})" for f in given)


def full_name(name):
    """A class's full name from its name as owner gives it."""
    return name if name.startswith(UNIT_PREFIX) else PREFIX + name


def project_of(name):
    return UNIT_PROJECT if name.startswith(UNIT_PREFIX) else PROJECT


def shown(project):
    """A project's path from the checkout, as a shard's projects file and a command line give it."""
    return os.path.relpath(project, ROOT).replace(os.sep, "/")


def tests_in(text):
    """The tests a `dotnet test --list-tests` printout names, by display name, or None when it never says which are there."""
    lines = text.splitlines()
    starts = [i for i, line in enumerate(lines) if line.strip() == "The following Tests are available:"]
    if not starts:
        return None
    return [line.strip() for line in lines[starts[0] + 1:] if line.startswith("    ") and line.strip()]


def unlisted(text):
    """Why a `dotnet test --list-tests` printout is no listing of the suite, or None when it is one."""
    tests = tests_in(text)
    if tests is None:
        return "it never says which tests are there"
    # Discovery that failed may still have listed some of the tests, so this shard's deal would not be the others'.
    if DISCOVERY_FAILED in text:
        return f"it says \"{DISCOVERY_FAILED}\", so some or all of the tests are missing from it"
    if not tests:
        return "it names no test"
    return None


def read(path):
    with open(path, encoding="utf-8", errors="replace") as file:
        return file.read()


def listing(path):
    """The tests `dotnet test --list-tests` printed, by display name."""
    text = read(path)
    if (why := unlisted(text)) is not None:
        sys.exit(f"test-shards: {path} is not a listing of the suite: {why}. dotnet test printed:\n{text}")
    return tests_in(text)


def list_tests(arguments):
    """A lane's tests as `dotnet test --list-tests` lists each of its projects' (with a shard's settings, that shard's
    hand of the projects its .projects file names), written to a file as it printed them, one project after another."""
    if arguments.settings:
        named = os.path.splitext(arguments.settings)[0] + ".projects"
        chosen = ([os.path.join(ROOT, line.strip()) for line in read(named).splitlines() if line.strip()]
                  if os.path.exists(named) else projects(arguments.lane))
        options = ["--settings", arguments.settings]
    else:
        chosen = projects(arguments.lane)
        given = lane_filter(arguments.lane, arguments.system)
        options = ["--filter", given] if given else []
    printed = b""
    for project in chosen:
        listed, whole = list_project(project, options, arguments.tries)
        printed += listed
        if not whole:
            with open(arguments.out, "wb") as file:
                file.write(printed)
            return 1
    with open(arguments.out, "wb") as file:
        file.write(printed)
    listed = len(tests_in(printed.decode('utf-8', errors='replace')))
    print(f"{listed} {arguments.lane} tests listed from "
          f"{', '.join(shown(p) for p in chosen)}{f' with {arguments.settings}' if arguments.settings else ''}")
    return 0 if arguments.settings else held_to_most(listed + sum(len(listing(path)) for path in arguments.beside))


def held_to_most(count):
    """Whether the suite's count of tests is within MOST_TESTS and its ALLOWANCE, said when it is not."""
    if count <= MOST_TESTS + ALLOWANCE:
        return 0
    allowed = f" with the {ALLOWANCE:,} more allowed while its batches cut it down" if ALLOWANCE > 0 else ""
    say("error", "Too many tests", f"The suite lists {count:,} tests, more than the {MOST_TESTS:,} it may (#2095){allowed}: "
        f"join each new check to a flow's step or a table's row where one fits (tests/Shared), or cut what no longer "
        f"guards anything another test does not")
    return 1


def list_project(project, options, tries):
    """What `dotnet test --list-tests` printed for one project, and whether it is a listing: one that names no test, or
    says discovery failed, is asked for again; still so after the last try, it is said with what dotnet printed."""
    command = ["dotnet", "test", project, "--no-build", "--list-tests"] + options
    for attempt in range(1, tries + 1):
        # Its standard error goes to the log; its output, which the listing is, as bytes, so the file is what it printed.
        done = subprocess.run(command, stdout=subprocess.PIPE, check=False)
        why = unlisted(done.stdout.decode("utf-8", errors="replace"))
        if why is None:
            return done.stdout, True
        if attempt < tries:
            say("warning", "Listing the tests", f"dotnet test --list-tests of {shown(project)} (exit code "
                f"{done.returncode}) is no listing: {why}. Listing again, try {attempt + 1} of {tries}")
            echo(done.stdout)
    say("error", "Listing the tests", f"dotnet test --list-tests of {shown(project)} is no listing in {tries} tries: "
        f"{why}. Its last exit code was {done.returncode}, and what it printed follows")
    echo(done.stdout)
    return done.stdout, False


def echo(printed):
    """A program's output into the log as it printed it: Python's own encoding on Windows (cp1252) cannot hold every name."""
    sys.stdout.flush()
    sys.stdout.buffer.write(printed)
    sys.stdout.buffer.flush()


def owner(name):
    """A test's class without PREFIX, from its display name: xunit's is the class's full name, the method's and any
    arguments in brackets. A test given a display name of its own still runs with its class, whose filter goes by full
    name, but its name would be dealt too, as a class of its own."""
    whole = name.split("(", 1)[0].rsplit(".", 1)[0]
    return whole[len(PREFIX):] if whole.startswith(PREFIX) else whole


def bases_of(listed):
    """The simple names of the types a class's base list names, never a generic argument's (a class fixture's type)."""
    while (plain := re.sub(r"<[^<>]*>", "", listed)) != listed:
        listed = plain
    return [part.strip().rsplit(".", 1)[-1] for part in listed.split(",") if re.match(r"\s*[A-Z]", part)]


def sources():
    """Each top-level class the test projects declare, by its name as a listing's tests give it (owner): whether its
    declaration says it is Alone or runs beside the window tests (a unit test always does), its project, its bases'
    simple names, and the TOGETHER groups its source names."""
    declared = {}
    unit = os.path.join(UNIT_PROJECT, "")
    for path in glob.glob(os.path.join(PROJECT, "**", "*.cs"), recursive=True) + \
            glob.glob(os.path.join(UNIT_PROJECT, "**", "*.cs"), recursive=True):
        if os.sep + "obj" + os.sep in path or os.sep + "bin" + os.sep in path:
            continue
        with open(path, encoding="utf-8-sig") as file:
            text = file.read()
        namespace = NAMESPACE.search(text)
        within = namespace["name"] + "." if namespace else ""
        found = list(DECLARATION.finditer(text))
        for index, declaration in enumerate(found):
            body = text[declaration.start():found[index + 1].start() if index + 1 < len(found) else len(text)]
            whole = within + declaration["name"]
            name = whole[len(PREFIX):] if whole.startswith(PREFIX) else whole
            rest = declaration["rest"]
            bases = bases_of(rest.split(":", 1)[1]) if ":" in rest else []
            declared[name] = {
                "alone": "Collection(Alone.Name)" in declaration["attributes"],
                "beside": path.startswith(unit) or any(collection in declaration["attributes"] for collection in BESIDE),
                "project": UNIT_PROJECT if path.startswith(unit) else PROJECT,
                "bases": [base for base in bases if base != declaration["name"]],
                "together": [group for group, pattern in TOGETHER.items() if pattern.search(body)],
                "set_ups": [need for need, pattern in SET_UPS.items() if pattern.search(body)],
            }
    return declared


def declares(name, declared, key, unknown, holds=bool):
    """Whether a class's declaration, or a base class's in its own project, says what key names (holds what it says, where
    given); unknown for one the sources do not show."""
    entry = declared.get(name)
    if entry is None:
        return unknown
    by_simple = {full.rsplit(".", 1)[-1]: value for full, value in declared.items() if value["project"] == entry["project"]}
    seen = set()
    while entry is not None:
        if holds(entry[key]):
            return True
        base = next((b for b in entry["bases"] if b in by_simple and b not in seen), None)
        if base is None:
            return False
        seen.add(base)
        entry = by_simple[base]
    return False


def alone(name, declared):
    """Whether a class runs in the Alone collection: its own declaration says so, or a base class's does. One the sources
    do not show (a test class nested in another) runs alone, which is never wrong, only slower."""
    return declares(name, declared, "alone", True)


def beside(name, declared):
    """Whether a class runs beside the window tests in its process (DebugSession.Collection, Beside)."""
    return declares(name, declared, "beside", False)


def needs(name, declared, set_ups):
    """Whether a class's source, or a base class's in its own project, names one of these SET_UPS."""
    return declares(name, declared, "set_ups", False, lambda named: bool(set(named) & set(set_ups)))


def load(path, default):
    if not os.path.exists(path):
        return default
    with open(path, encoding="utf-8") as file:
        return json.load(file)


def guess(name):
    """A class's seconds for one TestTimes.json has none for."""
    return UNIT_CLASS_SECONDS if name.startswith(UNIT_PREFIX) else CLASS_SECONDS


def shares(chosen, whole):
    """Each class's share of its tests among those chosen, by the counts of each in the two listings."""
    return {name: min(1.0, count / whole[name]) if whole.get(name) else 1.0 for name, count in chosen.items()}


def lanes_of(entry):
    """A system's seconds in TestTimes.json, each lane's by class; one kept before there were lanes, its classes' whole
    seconds as all's."""
    return entry if all(isinstance(value, dict) for value in entry.values()) else {"all": entry}


def lane_seconds(times, system, name, lane):
    """A class's seconds on a system for its tests in a lane, None where TestTimes.json has none."""
    return lanes_of(times.get(system, {})).get(lane, {}).get(name)


def class_seconds(times, system, name):
    """A class's seconds on a system for all its tests, every lane's together, None where TestTimes.json has none."""
    found = [seconds[name] for seconds in lanes_of(times.get(system, {})).values() if name in seconds]
    return sum(found) if found else None


def pace(times, system, other):
    """How long a class takes on this system for each of its seconds on the other: the median of the two systems' ratios
    over the classes both have, of a second or more on each."""
    mine, theirs = ({name: class_seconds(times, s, name) for lane in lanes_of(times.get(s, {})).values() for name in lane}
                    for s in (system, other))
    ratios = sorted(mine[c] / theirs[c] for c in mine.keys() & theirs.keys() if mine[c] >= 1 and theirs[c] >= 1)
    return ratios[len(ratios) // 2] if ratios else 1.0


def weights(classes, system, times, share, lane):
    """Each class's seconds on this system for its tests in the lane, and the classes borrowed and guessed. One whose
    tests in the lane TestTimes.json has no seconds for (they came from the other lane since) weighs its whole class's
    seconds times its share of its tests; one it lacks here (it only ever ran in shards that ran out of time) weighs its
    seconds on the other systems the same way, each at this system's pace, else a guess."""
    paces = {other: pace(times, system, other) for other in SYSTEMS if other != system}

    def on(system, name):
        own = lane_seconds(times, system, name, lane)
        whole = class_seconds(times, system, name)
        return own if own is not None else whole * share.get(name, 1.0) if whole is not None else None

    seconds, borrowed, guessed = {}, [], []
    for name in classes:
        elsewhere = [paces[other] * spent for other in paces if (spent := on(other, name)) is not None]
        if (here := on(system, name)) is not None:
            seconds[name] = here
        elif elsewhere:
            seconds[name] = sum(elsewhere) / len(elsewhere)
            borrowed.append(name)
        else:
            seconds[name] = guess(name) * share.get(name, 1.0)
            guessed.append(name)
    return seconds, borrowed, guessed


def deal(weighed, count):
    """The classes in count hands, the heaviest first, each to the lightest hand so far (the first of equals)."""
    hands = [[] for _ in range(count)]
    totals = [0.0] * count
    for name in sorted(weighed, key=lambda c: (-weighed[c], c)):
        lightest = min(range(count), key=lambda i: (totals[i], i))
        hands[lightest].append(name)
        totals[lightest] += weighed[name]
    return hands, totals


# How a class runs in a shard's dotnet test (Collections.cs): one after another with the other window classes, at once
# with them (DebugSession.Collection, Beside), alone once they have all ended, or in the unit tests' own dotnet test after.
# In a job of several test processes, its window and Beside classes are split between them, and its Alone classes run
# after all of them, in one process with nothing beside it.
KINDS = ("window", "beside", "alone", "unit")
# The kinds of class a job's test processes share out, each in one of them.
SHARED_OUT = ("window", "beside")


def kinds(classes):
    """Each class's kind, by its declaration in the checkout."""
    declared = sources()
    return {name: "unit" if name.startswith(UNIT_PREFIX) else "alone" if alone(name, declared)
            else "beside" if beside(name, declared) else "window" for name in classes}


def paced(system, processes):
    """How long a class takes on a system in a job of so many test processes for each second it takes in one alone."""
    if processes != 1 and processes not in PACED[system]:
        sys.exit(f"test-shards: PACED has no pace for {processes} test processes on {system}")
    return 1.0 if processes == 1 else PACED[system][processes]


def empty_hand(processes, pace=1.0, set_up=1.0):
    """A hand's seconds by kind before any class is dealt to it: its window and Beside classes' by process, which run at
    the pace given (paced), and those of the classes it runs apart after them (deal_apart), beside which the others run
    at its job's set-up's pace (set_up, SET_UP_PACE)."""
    return {"window": [0.0] * processes, "beside": [0.0] * processes, "alone": 0.0, "unit": 0.0, "pace": pace,
            "apart": 0.0, "set_up": set_up}


def with_class(part, kind, seconds, process):
    """A hand's seconds by kind with a class of that kind added, to its process where it has one."""
    added = {name: list(value) if isinstance(value, list) else value for name, value in part.items()}
    if kind in SHARED_OUT:
        added[kind][process] += seconds
    else:
        added[kind] += seconds
    return added


def total(part, kind):
    """A hand's seconds of a kind, every process's together."""
    return sum(part[kind]) if kind in SHARED_OUT else part[kind]


def hand_time(part):
    """A shard's seconds from its kinds' seconds: in each of its processes, its window classes', or its Beside ones' run
    at once with them where longer, at its processes' pace, the slowest process's; then its Alone classes' and its unit
    tests', all of these at its job's set-up's pace; and its classes apart."""
    first = max(max(window, beside) for window, beside in zip(part["window"], part["beside"]))
    return part["apart"] + (first * part["pace"] + part["alone"] + part["unit"]) * part["set_up"]


def deal_shards(weighed, kind, count, starts=None, processes=1, pace=1.0, together=None, held=(), held_to=0, set_ups=None):
    """The classes in count hands so the slowest ends soonest (its start's seconds and hand_time), with each hand's seconds
    by kind and each window or Beside class's process: those that run one after another the heaviest first, each where its
    hand then ends soonest (a window class to the process with the fewest seconds); then the Beside ones, each where it
    lengthens a hand least, to the process with the fewest Beside seconds. A TOGETHER group's (together) go as one; one
    holding a class held goes first, to the hand held_to. Each hand's set-up's pace is set_ups' (none: 1)."""
    starts = starts or [0.0] * count
    hands = [[] for _ in range(count)]
    parts = [empty_hand(processes, pace, set_up) for set_up in (set_ups or [1.0] * count)]
    places = {}
    units = {}
    for name in weighed:
        group = (together or {}).get(name) if kind[name] in SHARED_OUT else None
        units.setdefault(f"[{group}]" if group else name, []).append(name)
    unit_weight = {unit: sum(weighed[name] for name in members) for unit, members in units.items()}
    unit_kind = {unit: "window" if any(kind[name] == "window" for name in members) else kind[members[0]]
                 for unit, members in units.items()}
    pinned = {unit for unit, members in units.items() if set(members) & set(held)}
    ordered = sorted(units, key=lambda u: (u not in pinned, -unit_weight[u], u))

    def with_unit(part, unit, process):
        for name in units[unit]:
            part = with_class(part, kind[name], weighed[name], process)
        return part

    def ends(i, unit, process):
        # To the millisecond, so a sum's rounding never decides between equals.
        return round(starts[i] + hand_time(with_unit(parts[i], unit, process)), 3)

    def lengthens(i, unit, process):
        return round(hand_time(with_unit(parts[i], unit, process)) - hand_time(parts[i]), 3)

    def put(unit, i, process):
        hands[i].extend(units[unit])
        parts[i] = with_unit(parts[i], unit, process)
        places.update({name: process for name in units[unit] if kind[name] in SHARED_OUT})

    def hands_for(unit):
        return [held_to] if unit in pinned else range(count)

    for unit in (u for u in ordered if unit_kind[u] != "beside"):
        where = [(i, p) for i in hands_for(unit) for p in (range(processes) if unit_kind[unit] == "window" else [0])]
        i, p = min(where, key=lambda at: (ends(at[0], unit, at[1]), parts[at[0]]["window"][at[1]], at))
        put(unit, i, p)
    for unit in (u for u in ordered if unit_kind[u] == "beside"):
        where = [(i, p) for i in hands_for(unit) for p in range(processes)]
        i, p = min(where, key=lambda at: (lengthens(at[0], unit, at[1]), parts[at[0]]["beside"][at[1]], at))
        put(unit, i, p)
    return hands, parts, places


def deal_apart(weighed, kind, count, starts=None, processes=1, pace=1.0, together=None, apart=(), second=(), apart_hands=(0,),
               set_up=1.0):
    """deal_shards with the classes apart (those needing what only the jobs of apart_hands set up) dealt among those
    hands, last in each, in one test process with nothing beside them (runs_of), and every other class dealt as though
    each such hand began its share once its own end, at set_up's pace; those needing what the second shard's job sets up
    held to the second; as deal_shards where none is apart or held there, or there is one hand."""
    first = [name for name in weighed if name in apart]
    set_ups = [set_up if i in apart_hands and first else 1.0 for i in range(count)]
    if count < 2 or not first and not set(second) & set(weighed):
        return deal_shards(weighed, kind, count, starts, processes, pace, together)
    starts = list(starts or [0.0] * count)
    held, before, places = deal_shards({name: weighed[name] for name in first}, kind, len(apart_hands),
                                       [starts[i] for i in apart_hands], 1, 1.0, together)
    for slot, i in enumerate(apart_hands):
        starts[i] += hand_time(before[slot])
    hands, parts, others = deal_shards({name: spent for name, spent in weighed.items() if name not in apart}, kind, count,
                                       starts, processes, pace, together, second, 1, set_ups)
    for slot, i in enumerate(apart_hands):
        hands[i] = held[slot] + hands[i]
        parts[i]["apart"] = hand_time(before[slot])
    return hands, parts, {**places, **others}


def parts_of(seconds, kind, places, processes, pace=1.0, apart=(), set_up=1.0):
    """A hand's seconds by kind, from its classes' seconds and the process each window or Beside class ran in, the rest
    beside those of its classes apart (deal_apart) at set_up's pace, the classes apart as one process's after them."""
    part = empty_hand(processes, pace, set_up if set(apart) & set(seconds) else 1.0)
    first = empty_hand(1)
    for name, spent in seconds.items():
        if name in apart:
            first = with_class(first, kind[name], spent, 0)
        else:
            part = with_class(part, kind[name], spent, places.get(name, 0))
    part["apart"] = hand_time(first)
    return part


def said_hand(part):
    """A hand's seconds by kind as the deal says them: in a job of several processes, each one's first."""
    last = f"; then {part['apart'] / 60:.1f} apart" if part["apart"] else ""
    rest = any(total(part, k) for k in ("window", "beside", "alone", "unit"))
    slowed = f"; these at {part['set_up']:g} times as long beside the job's set-up" if part["set_up"] != 1.0 and rest else ""
    if len(part["window"]) == 1:
        return ", ".join(f"{total(part, k) / 60:.1f} {k}" for k in KINDS if total(part, k)) + slowed + last
    each = "; ".join(f"process {p + 1}: {part['window'][p] / 60:.1f} window, {part['beside'][p] / 60:.1f} beside"
                     for p in range(len(part["window"])))
    after = ", ".join(f"{part[k] / 60:.1f} {k}" for k in ("alone", "unit") if part[k])
    return (f"{each}, each at {part['pace']:g} times a lone process's pace" + (f"; then {after}" if after else "") + slowed
            + last)


def run_settings(classes, *filters):
    """dotnet test's settings (--settings) that run these classes' tests the filters select and no others: each test's
    FullyQualifiedName is its class's full name then its method's. A file rather than --filter, so no shell or command
    line holds it."""
    names = "|".join(f"FullyQualifiedName~{full_name(name)}." for name in sorted(classes))
    return ("<RunSettings>\n  <RunConfiguration>\n    <TestCaseFilter>" + escape(both(*filters, names))
            + "</TestCaseFilter>\n  </RunConfiguration>\n</RunSettings>\n")


def hand_projects(classes):
    """The projects holding these classes, in the order they run."""
    return [project for project in (PROJECT, UNIT_PROJECT) if any(project_of(name) == project for name in classes)]


def dealt(tests, whole, system, times, count, starts=None, processes=1, set_ups=(), seconds_ups=(), lane="all",
          apart_shards=(1,)):
    """A lane's tests, as a listing names them, dealt to count shards of so many test processes each (deal_apart: the
    classes needing set_ups apart last in apart_shards, those needing seconds_ups in the second), each weighed by its
    seconds in the lane (weights, by whole: the listing of all by class, none for the lane's own): the tests by class,
    each class's seconds, the classes borrowed and guessed, each class's kind, the hands with their seconds by kind, each
    window or Beside class's process, and the classes held to the first and second shards."""
    classes = sorted(set(owner(test) for test in tests))
    if count < 1 or len(classes) < count:
        sys.exit(f"test-shards: {len(classes)} classes cannot be dealt to {count} shards")
    counts = Counter(owner(test) for test in tests)
    seconds, borrowed, guessed = weights(classes, system, times, shares(counts, whole or counts), lane)
    kind = kinds(classes)
    declared = sources()
    together = {name: declared[name]["together"][0] for name in classes if declared.get(name, {}).get("together")}
    apart = {name for name in classes if set_ups and needs(name, declared, set_ups)} if count > 1 else set()
    second = {name for name in classes if seconds_ups and needs(name, declared, seconds_ups)} - apart if count > 1 else set()
    seconds = {name: spent * (APART_MARGIN if name in apart else 1.0) for name, spent in seconds.items()}
    hands, parts, places = deal_apart(seconds, kind, count, starts, processes, paced(system, processes), together, apart,
                                      second, [number - 1 for number in apart_shards], SET_UP_PACE[system])
    held = {"apart": sorted(apart), "apart_shards": list(apart_shards), "second": sorted(second)}
    return counts, seconds, borrowed, guessed, kind, hands, parts, places, held


def runs_of(number, hand, kind, places, processes, apart=()):
    """A shard's runs of dotnet test, in steps, each step's runs at once: one of each project (its Alone classes last),
    or with several processes each one's share of the window and Beside classes at once, then the Alone classes in one;
    then its classes apart (deal_apart), their window and Beside classes in one run and each Alone one in a run of its
    own, so one that hangs takes no other and what their devices leave slows none; then the unit tests. Each run is named
    (its .trx and console log), with its project, its settings' file, how many ran at once and its classes."""
    def run(name, project, classes, together, process=0):
        return {"name": name, "project": shown(project), "settings": f"shard-{number}.{name}.runsettings",
                "together": together, "process": process, "classes": sorted(classes)}

    held = [name for name in hand if name in apart]
    tests = [name for name in hand if project_of(name) == PROJECT and name not in apart]
    units = [name for name in hand if project_of(name) == UNIT_PROJECT and name not in apart]
    leaf = os.path.basename(PROJECT)
    steps = []
    if processes == 1:
        steps += [[run(leaf, PROJECT, tests, 1)]] if tests else []
    else:
        shared = [[name for name in tests if kind[name] in SHARED_OUT and places[name] == p] for p in range(processes)]
        shared = [(p, classes) for p, classes in enumerate(shared) if classes]
        alone_ones = [name for name in tests if kind[name] not in SHARED_OUT]
        steps += ([[run(f"{leaf}-{p + 1}", PROJECT, classes, len(shared), p) for p, classes in shared]] if shared else []) \
            + ([[run(f"{leaf}-alone", PROJECT, alone_ones, 1)]] if alone_ones else [])
    shared = [name for name in held if kind[name] in SHARED_OUT]
    steps += ([[run(f"{leaf}-apart", PROJECT, shared, 1)]] if shared else []) \
        + [[run(f"{leaf}-{name}", PROJECT, [name], 1)] for name in sorted(held) if kind[name] not in SHARED_OUT]
    return steps + ([[run(os.path.basename(UNIT_PROJECT), UNIT_PROJECT, units, 1)]] if units else [])


def split(arguments):
    if arguments.check and not arguments.out:
        sys.exit("test-shards: --check lists what each shard's settings select, which --out writes")
    own = arguments.of - (arguments.after_of if arguments.after else 0)
    if arguments.apart and len(set(arguments.apart_shards)) != len(arguments.apart_shards) \
            or arguments.apart and any(not 0 < number <= own or arguments.second and number == 2 for number in arguments.apart_shards):
        sys.exit(f"test-shards: --apart-shards {' '.join(map(str, arguments.apart_shards))} must each be a shard of the "
                 f"lane's own jobs, 1 to {own}, once, and not the second's where --second gives it a set-up")
    tests = listing(arguments.suite)
    whole = Counter(owner(test) for test in listing(arguments.whole)) if arguments.whole else None
    times = load(arguments.times, {})
    processes = arguments.processes
    # The last shards' jobs run another lane's shards first, one each, as dealt in those jobs: each starts as that ends.
    checked = [CHECKED_FIRST[arguments.system] if arguments.checks_first else 0.0]
    starts = [0.0] * arguments.of
    if arguments.after:
        if not 0 < arguments.after_of <= arguments.of:
            sys.exit(f"test-shards: the last {arguments.after_of} of {arguments.of} shards cannot each follow a shard "
                     "of the lane run first")
        begun = checked + [0.0] * (arguments.after_of - 1)
        first = dealt(listing(arguments.after), whole, arguments.system, times, arguments.after_of, begun, processes,
                      lane=arguments.after_lane)[6]
        starts[arguments.of - arguments.after_of:] = [start + hand_time(part) for start, part in zip(begun, first)]
    else:
        starts[0] = checked[0]
    for number in arguments.apart_shards if arguments.apart else ():
        starts[number - 1] += APART_FIRST[arguments.system]
    counts, seconds, borrowed, guessed, kind, hands, parts, places, held = dealt(
        tests, whole, arguments.system, times, arguments.of, starts, processes, arguments.apart or (), arguments.second or (),
        arguments.lane, arguments.apart_shards)
    whole = whole or counts
    lane = "" if arguments.lane == "all" else f" of the {arguments.lane} lane"
    shared = "" if arguments.lane == "all" else "; a class with tests outside the lane by its seconds in it"
    print(f"{len(tests)} tests in {len(seconds)} classes{lane}, dealt to {arguments.of} shards by their seconds on "
          f"{arguments.system} (TestTimes.json; a class it lacks by the other systems', else a guess{shared}), a shard "
          + ("taking its window classes' time or its Beside classes' beside them" if processes == 1 else
             f"running its window and Beside classes in {processes} test processes at once, each taking its window "
             f"classes' time or its Beside classes' beside them, at {paced(arguments.system, processes):g} times a lone "
             "process's pace, the container engine's classes in one")
          + ", then its Alone classes'"
          + (f"; the last {arguments.after_of} each in the job of a shard of the lane {arguments.after} lists, once "
             "that shard's tests end" if arguments.after else "")
          + (f"; the classes marked [{SAME}] only on {ONE_SYSTEM}, and on {MAC_OS} only those marked [{ABOUT_MAC_OS}]"
             if arguments.lane == "report" else "")
          + (f"; the classes needing {' or '.join(arguments.apart)}, which only the jobs of shards "
             f"{', '.join(map(str, arguments.apart_shards))} set up, last in them, apart from the rest, at "
             f"{APART_MARGIN:g} times their seconds, the rest there at {SET_UP_PACE[arguments.system]:g} times theirs"
             if arguments.apart else "")
          + (f"; those needing {' or '.join(arguments.second)}, which the second shard's job alone sets up, in it"
             if arguments.second else ""))
    for number, (hand, part, start) in enumerate(zip(hands, parts, starts), 1):
        first = number - (arguments.of - arguments.after_of) if arguments.after else 0
        print(f"\nShard {number}"
              + (f", after that lane's shard {first} in its job (about {start / 60:.1f} minutes)" if first > 0 else
                 f", its job setting up for {' and '.join(arguments.apart)} first (about {start / 60:.1f} minutes)"
                 if arguments.apart and number in arguments.apart_shards else
                 f", its job building the check first (about {start / 60:.1f} minutes)" if start > 0 else "")
              + f": {len(hand)} classes, {sum(counts[c] for c in hand)} tests, about {hand_time(part) / 60:.1f} minutes ("
              + said_hand(part) + ")"
              + (f", the classes needing {' or '.join(arguments.apart)} apart last"
                 if number in held["apart_shards"] and held["apart"] else "")
              + (f", the classes needing {' or '.join(arguments.second)} in it" if number == 2 and held["second"] else "")
              + (f", ending about {(start + hand_time(part)) / 60:.1f} minutes into the jobs' tests" if start > 0 else ""))
        for name in hand:
            print(f"  {name:<44} {seconds[name]:>7.1f}" + (f"  ({kind[name]})" if kind[name] in ("beside", "alone") else "")
                  + (f"  (process {places[name] + 1})" if processes > 1 and name in places else "")
                  + (f"  ({counts[name]} of its {whole[name]} tests)" if counts[name] < whole[name] else "")
                  + ("  (the other systems' time: TestTimes.json has none here)" if name in borrowed else "")
                  + ("  (a guess: TestTimes.json has none)" if name in guessed else ""))
    print(f"\nThe slowest shard ends about {max(s + hand_time(p) for s, p in zip(starts, parts)) / 60:.1f} minutes "
          "into the jobs' tests")
    if borrowed or guessed:
        say("notice", "Test times", f"TestTimes.json has no {arguments.system} seconds for "
            f"{', '.join(sorted(borrowed + guessed))}, so each is dealt by the other systems' or a guess: just test-times "
            "RUN brings them in from a run")
    if arguments.out:
        os.makedirs(arguments.out, exist_ok=True)
        chosen = lane_filter(arguments.lane, arguments.system)
        for number, hand in enumerate(hands, 1):
            with open(os.path.join(arguments.out, f"shard-{number}.runsettings"), "w", encoding="utf-8") as file:
                file.write(run_settings(hand, chosen))
            with open(os.path.join(arguments.out, f"shard-{number}.projects"), "w", encoding="utf-8",
                      newline="\n") as file:
                file.write("".join(shown(project) + "\n" for project in hand_projects(hand)))
            with open(os.path.join(arguments.out, f"shard-{number}.classes"), "w", encoding="utf-8",
                      newline="\n") as file:
                file.write("".join(name + "\n" for name in sorted(hand)))
            steps = runs_of(number, hand, kind, places, processes, apart=held["apart"])
            for each in (run for step in steps for run in step):
                with open(os.path.join(arguments.out, each["settings"]), "w", encoding="utf-8") as file:
                    file.write(run_settings(each["classes"], chosen))
            with open(os.path.join(arguments.out, RUNS.format(number)), "w", encoding="utf-8", newline="\n") as file:
                file.write(json.dumps(steps, indent=1) + "\n")
        with open(os.path.join(arguments.out, STARTS), "w", encoding="utf-8", newline="\n") as file:
            file.write(json.dumps([round(start, 1) for start in starts]) + "\n")
        with open(os.path.join(arguments.out, APART), "w", encoding="utf-8", newline="\n") as file:
            file.write(json.dumps(held, indent=1) + "\n")
    return check_shards(arguments) if arguments.check else 0


def check_shards(arguments):
    """Before any test runs: what each shard's settings select, listed (each a VSTest start) beside them as
    listed-N.txt, every test of the lane in exactly one of them (check)."""
    listed = []
    for number in range(1, arguments.of + 1):
        out = os.path.join(arguments.out, f"listed-{number}.txt")
        settings = os.path.join(arguments.out, f"shard-{number}.runsettings")
        if list_tests(argparse.Namespace(out=out, lane=arguments.lane, system=arguments.system, settings=settings,
                                         tries=TRIES)) != 0:
            return 1
        listed.append(out)
    return check(argparse.Namespace(suite=arguments.suite, listed=listed))


def check(arguments):
    """Every test of the suite in exactly one of the shards' listings."""
    suite = Counter(key(test) for test in listing(arguments.suite))
    listed = [Counter(key(test) for test in listing(path)) for path in arguments.listed]
    together = sum(listed, Counter())
    never = sorted((suite - together).elements())
    twice = sorted(name for name, count in together.items() if count > suite[name])
    problems = []
    if never:
        problems.append(f"{len(never)} of the suite's tests are in no shard's filter, so would never run: {some(never)}")
    if twice:
        problems.append(f"{len(twice)} tests are in more than one shard's filter (or are not the suite's), so would run "
                        f"twice: {some(twice)}")
    for problem in problems:
        say("error", "Test shards", problem)
    if not problems:
        print(f"Each of the suite's {sum(suite.values())} tests is in exactly one of the {len(listed)} shards' filters "
              f"({', '.join(str(sum(hand.values())) for hand in listed)})")
    return 1 if problems else 0


def run_shard(arguments):
    """A shard's tests (run_tests), then each class's time in the log and the job's summary (test-times.py), then, when a
    dotnet test failed, its failed tests (tell_failed). The last failing exit code, else test-times.py's. The machine's
    memory is watched meanwhile, and how low it fell said after (Memory)."""
    memory = Memory()
    code = run_tests(arguments)
    label = arguments.label or f"shard {arguments.shard}"
    memory.tell(label)
    timed = subprocess.run([sys.executable, "-I", os.path.join(ROOT, "tools", "test-times.py"),
                            os.path.abspath(arguments.results), "--shard", str(arguments.shard), "--label", label],
                           check=False).returncode
    if code == 0:
        return timed
    tell_failed(arguments.results, arguments.lane, label)
    return code


def tell_failed(results, lane, label):
    """Each test of a shard's results that ran and did not pass, in the log, as an annotation (a warning in the report
    lane, which fails nothing, else an error) and in the job's summary with the first line of what it said; or, with
    none, that dotnet test failed all the same."""
    failed = [entry for file in trx.trx_files([results]) for entry in trx.failed(file)]
    heading = (f"Tests failed on {label}: {len(failed)}" if failed else f"dotnet test failed on {label} with no failed "
               "test in its results: the log above says what crashed or hung")
    write(f"\n{heading}")
    lines = [f"### {heading}", ""]
    for name, outcome, message in failed:
        write(f"  {name} ({outcome})")
        say("warning" if lane == "report" else "error", name, message)
        first = message.split("\n", 1)[0].strip()
        lines.append(f"- `{name}` ({outcome}): {first}")
    summarize(lines)


def hang_environment(results):
    """This process's environment with the hang variables naming the HANGS folder of <results>, made; on macOS blame's
    dumps go to a folder of the system's temporary one, emptied as each dotnet test ends (drop_whole_dumps)."""
    folder = os.path.join(os.path.abspath(results), HANGS)
    dumps = whole_dumps() if MAC_OS_DUMPS_WHOLE else folder
    for made in (folder, dumps):
        os.makedirs(made, exist_ok=True)
    return dict(os.environ, **{REPORTS_VARIABLE: folder, DUMPS_VARIABLE: dumps})


def whole_dumps():
    return os.path.join(tempfile.gettempdir(), "passenger-whole-dumps")


def drop_whole_dumps():
    """Removes what macOS's blame dumped whole, which no artifact could carry and which would fill the disk."""
    if MAC_OS_DUMPS_WHOLE:
        shutil.rmtree(whole_dumps(), ignore_errors=True)


def run_tests(arguments):
    """A shard's tests: each step of its runs (RUNS) one after another, the runs of a step at once, each a dotnet test of
    its project with its settings; what each writes to its console goes to the log and, line by line as it comes, to its
    CONSOLE_LOG beside the results, which a shard stopped before its .trx is written leaves; the command --before-apart
    gives runs before the first step of its classes apart (run_before). The last failing exit code, else 0."""
    results = os.path.abspath(arguments.results)
    folder = os.path.join(results, "shards")
    code = 0
    apart = set(load(os.path.join(folder, APART), {}).get("apart", ()))
    before = arguments.before_apart
    for step in steps_of(folder, arguments.shard):
        if before and all(run.get("classes") and set(run["classes"]) <= apart for run in step):
            run_before(before)
            before = None
        if len(step) > 1:
            write(f"Running {', '.join(run['name'] for run in step)} at once")
        printing = threading.Lock()
        going = []
        for run in step:
            command = ["dotnet", "test", run["project"], "--no-build", "--settings", os.path.join(folder, run["settings"]),
                       "--results-directory", results, "--logger", f"trx;LogFileName={run['name']}.trx", "--logger",
                       "console;verbosity=normal"] + HANG_ARGUMENTS
            sys.stdout.flush()
            log = open(os.path.join(results, CONSOLE_LOG.format(run["name"])), "wb")
            process = subprocess.Popen(command, cwd=ROOT, env=hang_environment(results), stdout=subprocess.PIPE)
            copying = threading.Thread(target=copy_lines, args=(process.stdout, log, printing))
            copying.start()
            going.append((process, copying, log))
        for process, copying, log in going:
            copying.join()
            log.close()
            if process.wait() != 0:
                code = process.returncode
        drop_whole_dumps()
    return code


def run_before(command):
    """The command --before-apart gives, run to its end or BEFORE_APART_LIMIT, printing into the log; what it comes to
    fails nothing, its own words saying what it left undone."""
    write(f"Before the classes apart: {command}")
    began = time.monotonic()
    try:
        said = f"ended with {subprocess.run(shlex.split(command), cwd=ROOT, timeout=BEFORE_APART_LIMIT).returncode}"
    except subprocess.TimeoutExpired:
        said = f"was ended at its limit of {BEFORE_APART_LIMIT // 60} minutes"
    except OSError as error:
        said = f"could not start: {error}"
    write(f"{command} {said}, {(time.monotonic() - began) / 60:.1f} minutes after it began")


def copy_lines(stream, log, printing):
    """A run's console, line by line as it comes, to the log (one whole line at a time among runs at once) and its file."""
    for printed in iter(stream.readline, b""):
        with printing:
            sys.stdout.buffer.write(printed)
            sys.stdout.buffer.flush()
        log.write(printed)
        log.flush()


def steps_of(folder, shard):
    """A shard's runs in steps, as split wrote them beside its settings; from a deal before there were steps, one run of
    each project its .projects file names."""
    path = os.path.join(folder, RUNS.format(shard))
    if os.path.exists(path):
        return load(path, [])
    named = os.path.join(folder, f"shard-{shard}.projects")
    return [[{"name": os.path.basename(project.strip()), "project": project.strip(), "settings": f"shard-{shard}.runsettings",
              "together": 1}] for project in read(named).splitlines() if project.strip()] if os.path.exists(named) else []


class Memory:
    """The machine's memory watched while a shard's tests run, every few seconds on a thread of its own: the least that
    was free (free_memory); nothing where the system says none."""

    def __init__(self):
        self.whole, self.least = None, None
        self.stopped = threading.Event()
        self.watching = threading.Thread(target=self.watch, daemon=True)
        self.watching.start()

    def watch(self):
        while True:
            if (now := free_memory()) is not None:
                self.whole, free = now
                self.least = free if self.least is None else min(self.least, free)
            if self.stopped.wait(5):
                return

    def tell(self, label):
        """How low the free memory fell, in the log and the job's summary; a warning where it fell below MEMORY_MARGIN."""
        self.stopped.set()
        self.watching.join()
        if self.least is None:
            return
        said = (f"Memory on {label}: at least {self.least / (1 << 30):.1f} GB of the machine's {self.whole / (1 << 30):.1f} GB "
                f"stayed free while the tests ran, {(self.whole - self.least) / (1 << 30):.1f} GB at most in use")
        write(said)
        summarize([said])
        if self.least < MEMORY_MARGIN:
            say("warning", "Memory", f"{said}: under {MEMORY_MARGIN >> 30} GB free, a test process may be ended for want of "
                "memory, so this job's test processes at once may be too many for its runner")


def free_memory():
    """The machine's memory and how much of it is free, in bytes, or None where the system says neither: on macOS its free,
    inactive and speculative pages (vm_stat), which it gives a program before swapping."""
    if sys.platform == "darwin":
        try:
            whole = int(subprocess.run(["sysctl", "-n", "hw.memsize"], capture_output=True, text=True, timeout=10,
                                       check=True).stdout)
            stats = subprocess.run(["vm_stat"], capture_output=True, text=True, timeout=10, check=True).stdout
            size = int(re.search(r"page size of (\d+) bytes", stats)[1])
            pages = {found[1]: int(found[2]) for found in re.finditer(r"^Pages ([\w ]+?):\s+(\d+)\.", stats, re.M)}
            return whole, size * (pages["free"] + pages["inactive"] + pages["speculative"])
        except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError):
            return None
    if sys.platform.startswith("linux"):
        try:
            with open("/proc/meminfo", encoding="ascii") as file:
                values = {line.split(":")[0]: int(line.split()[1]) * 1024 for line in file if line.split()[1:2]}
            return values["MemTotal"], values["MemAvailable"]
        except (OSError, KeyError, ValueError):
            return None
    if sys.platform == "win32":
        class Status(ctypes.Structure):
            _fields_ = [("length", ctypes.c_uint32), ("load", ctypes.c_uint32), ("total", ctypes.c_uint64),
                        ("available", ctypes.c_uint64), ("page_total", ctypes.c_uint64), ("page_available", ctypes.c_uint64),
                        ("virtual_total", ctypes.c_uint64), ("virtual_available", ctypes.c_uint64),
                        ("extended", ctypes.c_uint64)]
        status = Status(length=ctypes.sizeof(Status))
        return (status.total, status.available) if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)) \
            else None
    return None


def took(text):
    """Seconds from the console logger's time for a test: "1 m 3 s", "250 ms", "< 1 ms"."""
    units = {"h": 3600, "m": 60, "s": 1, "ms": 0.001}
    return sum(int(number) * units[unit] for number, unit in re.findall(r"(\d+) (ms|h|m|s)\b", text or ""))


def console_results(path):
    """The results a dotnet test's console log holds, as trx.results gives a .trx's: (class, display name, seconds,
    outcome), a skipped test's NotExecuted."""
    found = []
    for line in read(path).splitlines():
        if result := RESULT_LINE.match(line):
            found.append((owner(result["name"]), result["name"], took(result["took"]),
                          "NotExecuted" if result["outcome"] == "Skipped" else result["outcome"]))
    return found


def shard_results(folder, shard):
    """A shard's results, each as trx.results gives a .trx's with how many runs ran at once with its own and its job's
    set-up's pace (set_up_of): each run's .trx where its dotnet test ended, else what its console log holds."""
    found = []
    for run in (run for step in steps_of(os.path.join(folder, "shards"), shard) for run in step):
        name = run["name"]
        if os.path.exists(os.path.join(folder, name + ".trx")):
            held = trx.results(os.path.join(folder, name + ".trx"))[1]
        elif os.path.exists(os.path.join(folder, CONSOLE_LOG.format(name))):
            held = console_results(os.path.join(folder, CONSOLE_LOG.format(name)))
        else:
            held = []
        found += [(*result, run.get("together", 1), set_up_of(folder, shard, run)) for result in held]
    return found


def set_up_of(folder, shard, run):
    """How many times their seconds a shard's run's classes took for its job's set-up (SET_UP_PACE): those beside the
    classes apart in a shard the deal set up for them (its APART beside the results); 1 for any other run."""
    held = load(os.path.join(folder, "shards", APART), {})
    apart = set(held.get("apart", ()))
    system = load(os.path.join(folder, SHARD_STATE), {}).get("system")
    beside = apart and shard in held.get("apart_shards", ()) and not set(run.get("classes", ())) <= apart
    return SET_UP_PACE.get(system, 1.0) if beside else 1.0


def run_of(path):
    """The results folder, shard and run (runs_of's) whose .trx or console log this is, by its shard's runs beside it;
    an empty run where they do not say."""
    folder = os.path.dirname(os.path.abspath(path))
    state = load(os.path.join(folder, SHARD_STATE), {})
    name = os.path.basename(path)
    before, after = CONSOLE_LOG.split("{}")
    name = name[:-len(".trx")] if name.endswith(".trx") else name[len(before):-len(after)]
    runs = [run for step in steps_of(os.path.join(folder, "shards"), state.get("shard")) for run in step] \
        if state.get("shard") is not None else []
    return folder, state.get("shard"), next((run for run in runs if run["name"] == name), {})


def lone_pace(system, path):
    """How many times a lone test process's seconds the run whose .trx or console log this is took: its processes'
    pace (paced, by how many ran at once with it) times its job's set-up's (set_up_of)."""
    folder, shard, run = run_of(path)
    return paced(system, run.get("together", 1)) * set_up_of(folder, shard, run)


def not_reached(folder):
    """The classes a shard's results folder says it did not run whole: (class, tests that ran, tests listed)."""
    path = os.path.join(folder, UNREACHED)
    if not os.path.exists(path):
        return []
    return [(name, int(ran), int(listed)) for name, ran, listed in
            (line.split("\t") for line in read(path).splitlines() if line.strip())]


def said_short(entry):
    name, ran, listed = entry
    return name if ran == 0 else f"{name} ({ran} of its {listed} tests)"


def sized(size):
    return f"{size / 2 ** 20:.1f} MB" if size >= 2 ** 20 else f"{size / 2 ** 10:.1f} KB"


def ended(arguments):
    """After a shard's tests, however they ended: SHARD_STATE beside its results, saying which shard it was and how it
    ended, for the summary jobs; then what a hang left (hung) and the classes it did not reach (unreached)."""
    os.makedirs(arguments.results, exist_ok=True)
    with open(os.path.join(arguments.results, SHARD_STATE), "w", encoding="utf-8", newline="\n") as file:
        file.write(json.dumps({"system": arguments.system, "lane": arguments.lane, "shard": arguments.shard,
                               "shards": arguments.of, "status": arguments.status}) + "\n")
    hung(arguments)
    return unreached(arguments)


def hung(arguments):
    """What a hang left in the results' HANGS folder: each report printed whole into the log, and the tests it says were
    running, with the reports and dumps named and their sizes, in the job's summary and an annotation, which say the
    artifact carries them; nothing when no test hung."""
    folder = os.path.join(arguments.results, HANGS)
    kept = sorted(os.listdir(folder)) if os.path.isdir(folder) else []
    if not kept:
        return
    running = []
    for name in kept:
        if name.endswith(".txt"):
            lines = read(os.path.join(folder, name)).splitlines()
            write(f"{name}:")
            for line in lines:
                write(f"  {line}")
            after = lines[lines.index("Running:") + 1:] if "Running:" in lines else []
            running += [line.strip() for line in after[:next((i for i, line in enumerate(after) if not line.startswith("  ")), len(after))]]
    said = [f"{name} ({sized(os.path.getsize(os.path.join(folder, name)))})" for name in kept]
    heading = (f"{arguments.label or f'Shard {arguments.shard}'} hung, and kept {len(kept)} files of it in "
               f"{HANGS}/ of its results' artifact")
    say("warning" if arguments.lane == "report" else "error", "A test hung",
        f"{heading}: {', '.join(said)}" + (f"; running: {'; '.join(running)}" if running else ""))
    summarize([f"### {heading}", ""] + [f"- Running: {test}" for test in running] + [
        "", "The reports are printed whole in the log of the step that says how the shard ended; a dump opens in "
        "`dotnet-dump analyze` (on the system that took it), whose `clrstack -all` gives each thread's managed stack.", ""]
        + [f"- `{entry}`" for entry in said])


def unreached(arguments):
    """Each class of a shard's hand with fewer results than the lane's listing has tests, a line each in the log
    ("  Unreached CLASS"), in the job's summary and an annotation, and in UNREACHED for the summary job; none when every
    class ran whole."""
    results = arguments.results
    base = os.path.join(results, "shards", f"shard-{arguments.shard}")
    suite = os.path.join(results, "suite.txt")
    if not os.path.exists(base + ".classes") or not os.path.exists(suite) or unlisted(read(suite)) is not None:
        print("test-shards: the shard left no deal or no listing of the lane, so nothing says which classes it should "
              "have run")
        return 0
    hand = [line.strip() for line in read(base + ".classes").splitlines() if line.strip()]
    listed = Counter(owner(test) for test in tests_in(read(suite)))
    ran = Counter(result[0] for result in shard_results(results, arguments.shard))
    missed = [(name, ran[name], listed[name]) for name in hand if ran[name] < listed[name]]
    with open(os.path.join(results, UNREACHED), "w", encoding="utf-8", newline="\n") as file:
        file.write("".join(f"{name}\t{count}\t{whole}\n" for name, count, whole in missed))
    if not missed:
        print(f"Each of the shard's {len(hand)} classes ran each of its tests")
        return 0
    heading = (f"{arguments.label or f'Shard {arguments.shard}'} did not reach {len(missed)} of its {len(hand)} classes, "
               "so they ran in part or not at all")
    print(heading)
    for name, count, whole in missed:
        print(f"  Unreached {full_name(name)} ({count} of its {whole} tests ran)")
    say("warning" if arguments.lane == "report" else "error", "Classes not reached",
        f"{heading}: {', '.join(said_short(entry) for entry in missed)}")
    summarize([f"### {heading}", ""] + [f"- `{name}`: {count} of its {whole} tests ran" for name, count, whole in missed])
    return 0


def shards(results, system, lane):
    """One system's shards of a lane among downloaded results, by number: the folders whose shard.json says they are
    that shard (one that names no lane, from before there were two, is all's)."""
    found = {}
    for folder, _, files in sorted(os.walk(results)):
        if SHARD_STATE in files:
            state = load(os.path.join(folder, SHARD_STATE), {})
            if state.get("system") == system and state.get("lane", "all") == lane:
                found.setdefault(state.get("shard"), []).append((folder, state))
    return found


def cover(arguments):
    """After the shards: each ran and succeeded, and the lane's every test has exactly one result among them; in the
    report lane, which holds nothing back, a problem is a warning."""
    system = arguments.system
    found = shards(arguments.results, system, arguments.lane)
    where = system if arguments.lane == "all" else f"{system} in the {arguments.lane} lane"
    problems = [f"{len(places)} folders say they are shard {number} on {where}: {', '.join(f for f, _ in places)}"
                for number, places in found.items() if len(places) > 1]
    folders = {number: places[0][0] for number, places in found.items()}
    states = {number: places[0][1] for number, places in found.items()}
    counts = sorted(set(state.get("shards") for state in states.values()))
    if len(counts) > 1:
        problems.append(f"The shards on {where} disagree on how many there are: {', '.join(map(str, counts))}")
    if not found:
        problems.append(f"No shard on {where} left any results: each one's job failed or was stopped before "
                        "uploading them")
    for number in range(1, max(counts + list(folders) or [0]) + 1):
        if number not in folders:
            problems.append(f"Shard {number} on {where} left no results: its job failed or was stopped before "
                            "uploading them")
        elif states[number].get("status") != "success":
            problems.append(f"Shard {number} on {where} ended in {states[number].get('status')}: its job's log says why")
    for number, folder in sorted(folders.items()):
        if missed := not_reached(folder):
            problems.append(f"Shard {number} on {where} did not reach {len(missed)} of its classes, so they ran in part or "
                            f"not at all: {', '.join(said_short(entry) for entry in missed)}")

    suite = None
    for number, folder in sorted(folders.items()):
        if os.path.exists(os.path.join(folder, "suite.txt")):
            text = read(os.path.join(folder, "suite.txt"))
            if (why := unlisted(text)) is not None:
                problems.append(f"Shard {number} on {where} left no listing of the suite: {why}")
                continue
            this = Counter(key(test) for test in tests_in(text))
            if suite is None:
                suite = this
            elif this != suite:
                problems.append(f"Shard {number} on {where} listed a different suite from the others")
    ran = Counter()
    hands = {}
    places = {}
    ratios = []
    times = load(TIMES, {})
    in_lane = Counter(owner(test) for test in (suite or Counter()).elements())
    processes = 1
    for number, folder in folders.items():
        hand = hands.setdefault(number, {})
        found = (shard_results(folder, number) if steps_of(os.path.join(folder, "shards"), number)
                 else [(*result, 1, 1.0) for file in trx.trx_files([folder]) for result in trx.results(file)[1]])
        for test_class, name, spent, _, together, set_up in found:
            ran[key(name)] += 1
            # A lone process's seconds, as TestTimes.json keeps them, for a deal from this run's times.
            hand[test_class] = hand.get(test_class, 0.0) + spent / (paced(system, together) * set_up)
            processes = max(processes, together)
        for run in (run for step in steps_of(os.path.join(folder, "shards"), number) for run in step):
            places.update(dict.fromkeys(run.get("classes", []), run.get("process", 0)))
            if run.get("together", 1) > 1:
                spent, tests = Counter(), Counter()
                for test_class, _, seconds, _, _, set_up in (result for result in found if result[0] in run["classes"]):
                    spent[test_class] += seconds / set_up
                    tests[test_class] += 1
                # Only a class whose every test of the lane ran here, as TestTimes.json's seconds are all of them.
                kept = {name: lane_seconds(times, system, name, arguments.lane) if arguments.lane != "all"
                        else class_seconds(times, system, name) for name in spent}
                ratios += [spent[name] / kept[name] for name in spent
                          if (kept[name] or 0) >= 10 and tests[name] >= in_lane[name]]
    if suite is None:
        problems.append(f"No shard on {where} left its listing of the suite, so nothing says which tests should have "
                        "run")
    else:
        never = sorted((suite - ran).elements())
        twice = sorted(name for name, times in ran.items() if times > suite[name])
        if never:
            problems.append(f"{len(never)} of the suite's tests have no result from any shard on {where}: {some(never)}")
        if twice:
            problems.append(f"{len(twice)} tests have a result from more than one shard on {where}, or are not in "
                            f"the suite: {some(twice)}")
    for problem in problems:
        say("warning" if arguments.lane == "report" else "error", "Test shards", problem)

    lines = [f"### Shards on {where}", ""]
    if problems:
        lines += [f"- {problem}" for problem in problems]
    else:
        lines.append(f"Each of the suite's {sum(suite.values())} tests ran in exactly one of {len(folders)} shards.")
        print(lines[-1])
        seconds = {name: spent for hand in hands.values() for name, spent in hand.items()}
        kind = kinds(seconds)
        # Where the deal had shards begin after another lane's in their jobs, a deal from this run's times does too.
        starts = next((load(path, None) for path in (os.path.join(folder, "shards", STARTS) for folder in folders.values())
                       if os.path.exists(path)), None)
        starts = starts if starts and len(starts) == len(hands) else [0.0] * len(hands)
        pace = paced(system, processes)
        # The classes the deal held to the first and second shards, which a deal from this run's times holds there too.
        held = next((load(path, {}) for path in (os.path.join(folder, "shards", APART) for folder in folders.values())
                     if os.path.exists(path)), {})
        slowest = max(start + hand_time(parts_of(hands[number], kind, places, processes, pace, held.get("apart", ()),
                                                 SET_UP_PACE[system] if number in held.get("apart_shards", ()) else 1.0))
                      for start, number in zip(starts, sorted(hands)))
        declared = sources()
        together = {name: declared[name]["together"][0] for name in seconds if declared.get(name, {}).get("together")}
        _, parts, _ = deal_apart(seconds, kind, len(hands), starts, processes, pace, together, held.get("apart", ()),
                                 held.get("second", ()), [number - 1 for number in held.get("apart_shards", [1])],
                                 SET_UP_PACE[system])
        best = max(start + hand_time(part) for start, part in zip(starts, parts))
        if slowest - best >= WORTH_DEALING_AGAIN:
            message = (f"Dealt from this run's own times, the slowest shard on {where} would end its tests "
                       f"{best / 60:.1f} minutes into its job's rather than {slowest / 60:.1f}: `just test-times "
                       f"{os.environ.get('GITHUB_RUN_ID', 'RUN')}` writes them to TestTimes.json, to commit")
            say("warning", "Test shards out of balance", message)
            lines += ["", message]
    if ratios:
        ratios.sort()
        message = (f"In {processes} test processes at once on {where}, a class of 10 s or more took a median "
                   f"{ratios[len(ratios) // 2]:.2f} times its seconds in TestTimes.json ({len(ratios)} classes; the deal "
                   f"takes {paced(system, processes):g}, test-shards.py's PACED)")
        print(message)
        lines += ["", message]
    summarize(lines)
    return 1 if problems and arguments.lane != "report" else 0


def markdown(text):
    """Text for a cell of a Markdown table."""
    return text.replace("\\", "\\\\").replace("|", "\\|").replace("`", "\\`")


def failures(arguments):
    """Every test of the results that ran and did not pass, tabled in the job's summary with the system it failed on and
    the first line of what it said: a lane's failures, listed for triage."""
    rows = []
    for file in trx.trx_files([arguments.results]) if os.path.isdir(arguments.results) else []:
        system, _ = trx.shard_of(file)
        for name, outcome, message in trx.failed(file):
            first = next((line.strip() for line in message.splitlines() if line.strip()), "")
            rows.append((name, system or "?", outcome, first[:300] + ("…" if len(first) > 300 else "")))
    rows.sort()
    heading = arguments.title + (f": {len(rows)} failed" if rows else ": none failed")
    print(heading)
    for name, system, outcome, first in rows:
        print(f"  {name} on {system} ({outcome}): {first}")
    lines = [f"### {heading}", ""]
    if rows:
        lines += ["| Test | System | Message |", "|---|---|---|"]
        lines += [f"| {markdown(name)} | {system}{'' if outcome == 'Failed' else f' ({outcome})'} | {markdown(first)} |"
                  for name, system, outcome, first in rows]
    missed = []
    for folder, _, files in sorted(os.walk(arguments.results)) if os.path.isdir(arguments.results) else []:
        if SHARD_STATE in files:
            state = load(os.path.join(folder, SHARD_STATE), {})
            missed += [(name, state.get("system", "?"), state.get("shard"), count, whole)
                       for name, count, whole in not_reached(folder)]
    if missed:
        said = f"Classes the shards did not reach, so their tests ran in part or not at all: {len(missed)}"
        print(said)
        for name, system, shard, count, whole in sorted(missed):
            print(f"  Unreached {full_name(name)} on {system}, shard {shard} ({count} of its {whole} tests ran)")
        lines += ["", f"### {said}", "", "| Class | System | Tests that ran |", "|---|---|---:|"]
        lines += [f"| {markdown(name)} | {system}, shard {shard} | {count} of {whole} |"
                  for name, system, shard, count, whole in sorted(missed)]
    summarize(lines)
    return 0


def download(run):
    """A CI run's test results, downloaded by gh into artifacts/test-times/RUN."""
    folder = os.path.join(ROOT, "artifacts", "test-times", run)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    subprocess.run(["gh", "run", "download", run, "--pattern", "test-results-*", "--dir", folder], check=True)
    return folder


def listings(folder):
    """Per system, the lanes a run's shards ran and what they listed: each lane's tests by class, and all's."""
    found = {}
    for path, _, files in os.walk(folder):
        if not {SHARD_STATE, "suite.txt", "whole.txt"} & set(files):
            continue
        system, _ = trx.shard_of(os.path.join(path, SHARD_STATE))
        lane = load(os.path.join(path, SHARD_STATE), {}).get("lane", "all")
        entry = found.setdefault(system, {"lanes": {lane}, "listed": {}})
        entry["lanes"].add(lane)
        for name, listed in (("suite.txt", lane), ("whole.txt", "all")):
            # A shard whose listing failed ran nothing, and says nothing of the suite.
            if name in files and unlisted(text := read(os.path.join(path, name))) is None:
                entry["listed"].setdefault(listed, Counter(owner(test) for test in tests_in(text)))
    return found


def lane_of(file):
    """The lane a result ran in: its shard's, by the SHARD_STATE beside it, else by the folder its artifact was downloaded
    to (test-results-macOS-gate-1); all where neither says, as from before there were lanes."""
    folder = os.path.dirname(os.path.abspath(file))
    if os.path.exists(os.path.join(folder, SHARD_STATE)):
        return load(os.path.join(folder, SHARD_STATE), {}).get("lane", "all")
    named = re.search(r"-(gate|report)(?:-\d+)?$", os.path.basename(folder))
    return named[1] if named else "all"


def logged_results(folder):
    """The results of each shard under a folder whose dotnet test of a project left its console log and no .trx (it was
    stopped partway), as (its log, results), only those of the classes that ran every test the shard's listing has."""
    found = []
    for path, _, files in sorted(os.walk(folder)):
        if SHARD_STATE not in files or "suite.txt" not in files or unlisted(read(os.path.join(path, "suite.txt"))):
            continue
        listed = Counter(owner(test) for test in tests_in(read(os.path.join(path, "suite.txt"))))
        before, after = CONSOLE_LOG.split("{}")
        for name in sorted(files):
            if not (name.startswith(before) and name.endswith(after)) or name[len(before):-len(after)] + ".trx" in files:
                continue
            results = console_results(os.path.join(path, name))
            ran = Counter(test_class for test_class, _, _, _ in results)
            found.append((os.path.join(path, name), [result for result in results if ran[result[0]] >= listed[result[0]]]))
    return found


def hang(arguments):
    """HANG_ARGUMENTS on one line with no line break after it, which a shell's $(...) splits into dotnet test's arguments
    on every system (Windows' Python would end a line with a carriage return)."""
    sys.stdout.write(" ".join(HANG_ARGUMENTS))
    return 0


def update(arguments):
    """TestTimes.json from runs' results, the newest first: each class's seconds on each system for its tests in each lane
    as the newest run listing the lane lists them, the mean of the runs that ran every one of them and listed no other,
    each as a lone test process takes it (lone_pace: a run's beside others at once or its job's set-up); one a shard did not
    reach, its seconds from before, or what the runs measured of it in that lane on another system at this one's pace
    where more. A lane the runs did not list keeps its seconds from before."""
    old = load(arguments.times, {})
    spent = {}
    suites = {}
    partly = {}
    changed = {}
    for source in arguments.runs:
        folder = source if os.path.isdir(source) else download(source) if source.isdigit() else None
        if folder is None:
            sys.exit(f"test-shards: {source} is neither a folder of results nor a run's number")
        listed = listings(folder)
        found = [(file, trx.results(file)[1]) for file in trx.trx_files([folder])] + logged_results(folder)
        if not found:
            sys.exit(f"test-shards: {folder} holds no .trx results and no shard's console log")
        this = {}
        ran = {}
        for file, results in found:
            system, _ = trx.shard_of(file)
            if system not in SYSTEMS:
                sys.exit(f"test-shards: nothing says which system {file} ran on: no shard.json beside it, and no "
                         "test-results-SYSTEM folder over it")
            if (lane := lane_of(file)) not in LANES[:2]:
                sys.exit(f"test-shards: nothing says which lane {file} ran in, as in a run from before there were lanes, "
                         "and TestTimes.json keeps each lane's seconds")
            # A lone process's seconds, where the run ran beside others at once or beside its job's set-up.
            pace_here = lone_pace(system, file)
            for test_class, _, seconds, _ in results:
                this.setdefault(system, {}).setdefault(lane, {}).setdefault(test_class, 0.0)
                this[system][lane][test_class] += seconds / pace_here
                ran.setdefault(system, {}).setdefault(lane, Counter())[test_class] += 1
        for system, lanes in this.items():
            for lane, classes in lanes.items():
                # A class whose every test of the lane has a result, by the lane's listing where the run left one.
                every = listed.get(system, {}).get("listed", {}).get(lane)
                for test_class in [name for name in classes if every and ran[system][lane][name] < every.get(name, 0)]:
                    partly.setdefault(system, {}).setdefault(lane, set()).add(test_class)
                    del classes[test_class]
        for system, entry in listed.items():
            for lane, counts in entry["listed"].items():
                suites.setdefault(system, {}).setdefault(lane, counts)
        for system, lanes in this.items():
            for lane, classes in lanes.items():
                newest = suites.get(system, {}).get(lane)
                here = listed.get(system, {}).get("listed", {}).get(lane)
                for test_class, seconds in classes.items():
                    # A run listing other tests of the class in the lane than the newest is no measure of them.
                    if newest is not None and here is not None and here.get(test_class, 0) != newest.get(test_class, 0):
                        changed.setdefault(system, {}).setdefault(lane, set()).add(test_class)
                        continue
                    spent.setdefault(system, {}).setdefault(lane, {}).setdefault(test_class, []).append(seconds)
    every = {system: {lane: {name: round(sum(values) / len(values), 1) for name, values in classes.items()}
                      for lane, classes in lanes.items()} for system, lanes in spent.items()}
    times = {}
    for system in SYSTEMS:
        if system not in spent:
            if system in old:
                times[system] = old[system]
            continue
        before = lanes_of(old.get(system, {}))
        listed = suites.get(system, {})
        times[system] = {}
        said = []
        for lane in LANES[:2]:
            measured = every[system].get(lane, {})
            if lane not in listed:
                times[system][lane] = dict(before.get(lane, {}))
                said.append(f"the {lane} lane's {len(times[system][lane])} classes kept from before, as the runs listed "
                            "none of it")
                continue

            def earlier(name):
                # From before there were lanes, a class's whole seconds by its share of its tests in this lane.
                if name in before.get(lane, {}):
                    return before[lane][name]
                whole = before.get("all", {}).get(name)
                return whole * shares({name: listed[lane][name]}, listed.get("all", {}))[name] if whole is not None else None

            # Unreported (its shard stopped early), a class keeps its seconds from before, or, where more, another system's.
            kept = {name: round(max([seconds] + [pace(every, system, other) * every[other][lane][name] for other in every
                                                 if other != system and name in every[other].get(lane, {})]), 1)
                    for name in listed[lane] if name not in measured and (seconds := earlier(name)) is not None}
            times[system][lane] = dict(sorted({**kept, **measured}.items()))
            unmeasured = partly.get(system, {}).get(lane, set()) - set(measured)
            since = changed.get(system, {}).get(lane, set())
            said.append(f"{len(measured)} classes measured in the {lane} lane" + (f", {len(kept)} kept from before"
                                                                                  if kept else "")
                        + (f" ({len(unmeasured)} ran only in part)" if unmeasured else "")
                        + (f" ({len(since)} measured only where their tests in it were as in the newest run)"
                           if since else ""))
        print(f"{system}: {'; '.join(said)}")
    with open(arguments.times, "w", encoding="utf-8", newline="\n") as file:
        file.write(json.dumps(times, indent=2) + "\n")
    print(f"Wrote {os.path.relpath(arguments.times, ROOT)}: commit it, and every shard deals by it")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)

    command = commands.add_parser("list", help="write what dotnet test --list-tests prints, listing again while it names no test")
    command.add_argument("out", help="the file the listing goes to")
    command.add_argument("--lane", choices=LANES, default="all", help="the part of the suite to list")
    command.add_argument("--system", choices=SYSTEMS, help="the system whose part of the lane to list: off ONE_SYSTEM "
                         "the report lane has no class marked [SameOnEverySystem], and on MAC_OS only those marked "
                         "[AboutMacOS]; every system's where none is given")
    command.add_argument("--settings", help="a shard's dotnet test settings, to list only its hand")
    command.add_argument("--tries", type=int, default=TRIES)
    command.add_argument("--beside", nargs="*", default=[], help="what dotnet test --list-tests printed for the suite's "
                         "other lanes, whose tests count with these against MOST_TESTS: the listing fails above it")
    command.set_defaults(run=list_tests)

    command = commands.add_parser("split", help="deal a system's test classes to its shards, their settings to --out")
    command.add_argument("suite", help="what dotnet test --list-tests printed for the lane")
    command.add_argument("--lane", choices=LANES, default="all", help="the lane listed, which the shards' settings select")
    command.add_argument("--whole", help="what it printed for all, by which a class with tests outside the lane that "
                         "TestTimes.json has no seconds in the lane for weighs its share of its seconds")
    command.add_argument("--system", required=True, choices=SYSTEMS)
    command.add_argument("--of", type=int, required=True, help="how many shards")
    command.add_argument("--processes", type=int, default=1, choices=PROCESSES, help="how many test processes each "
                         "shard's job runs its window and Beside classes in at once, its Alone classes after them in one")
    command.add_argument("--after", help="what dotnet test --list-tests printed for a lane run first in the jobs of the "
                         "last shards, one of its shards in each, so each of them begins once that one would end")
    command.add_argument("--after-of", type=int, default=0, help="how many shards that lane is dealt to: the last so many "
                         "of these follow them, in order")
    command.add_argument("--after-lane", choices=LANES, default="gate", help="the lane --after lists, whose seconds weigh "
                         "its classes")
    command.add_argument("--checks-first", action="store_true", help="the job of the first shard of the lane run first "
                         "(--after's, else this one's) builds the solution's check before its tests, so begins them "
                         "CHECKED_FIRST later")
    command.add_argument("--out", help="a folder for each shard's dotnet test settings, shard-N.runsettings, and its "
                         "projects, shard-N.projects, and when each begins, starts.json")
    command.add_argument("--second", action="append", choices=sorted(SET_UPS), help="a set-up only the second shard's "
                         "job makes (SET_UPS): every class needing it is dealt to that shard, beside others")
    command.add_argument("--apart", action="append", choices=sorted(SET_UPS), help="a set-up only the jobs of "
                         "--apart-shards make (SET_UPS): every class needing it is dealt among those shards, run last "
                         "and apart from the others dealt there")
    command.add_argument("--apart-shards", type=int, nargs="+", default=[1], help="the shards whose jobs make --apart's "
                         "set-up, each a shard of the lane's own jobs and none the second's (default: 1)")
    command.add_argument("--times", default=TIMES)
    command.add_argument("--check", action="store_true", help="then list what each shard's settings select and fail "
                         "unless every test of the lane is in exactly one, as check does: one shard of a commit's does")
    command.set_defaults(run=split)

    command = commands.add_parser("check", help="fail unless every test of the lane is in exactly one shard's listing")
    command.add_argument("suite", help="what dotnet test --list-tests printed for the lane")
    command.add_argument("listed", nargs="+", help="what it printed with each shard's settings")
    command.set_defaults(run=check)

    command = commands.add_parser("test", help="run a shard's tests, keeping what each dotnet test writes beside them")
    command.add_argument("results", help="the shard's results folder, with the deal's shards/ in it")
    command.add_argument("--shard", type=int, required=True)
    command.add_argument("--lane", choices=LANES, default="all", help="the lane, whose failed tests are warnings in the "
                         "report lane and errors otherwise")
    command.add_argument("--label", help="which shard, for the summary: Report, macOS (arm64), shard 3 of 5")
    command.add_argument("--before-apart", help="a command run once before the shard's classes apart (split's --apart), "
                         "however it ends: test.yml's first boot of the iOS simulator in a macOS mobile shard")
    command.set_defaults(run=run_shard)

    command = commands.add_parser("ended", help="say which shard ran and how it ended, and name the classes of its hand "
                                  "it did not run whole")
    command.add_argument("results", help="the shard's results folder, with the deal's shards/ and the lane's suite.txt")
    command.add_argument("--system", required=True, choices=SYSTEMS)
    command.add_argument("--lane", choices=LANES, default="all", help="the lane, whose shards' misses are warnings in "
                         "the report lane and errors otherwise")
    command.add_argument("--shard", type=int, required=True)
    command.add_argument("--of", type=int, required=True, help="how many shards the lane has on the system")
    command.add_argument("--status", required=True, help="how its job ended, or its steps where it ran after another "
                         "lane's in its job: success, failure, cancelled, skipped")
    command.add_argument("--label", help="which shard, for the summary: Report, macOS (arm64), shard 3 of 5")
    command.set_defaults(run=ended)

    command = commands.add_parser("cover", help="fail unless every shard succeeded and every test has exactly one result")
    command.add_argument("results", help="the downloaded artifacts of the system's shards, each with its shard.json")
    command.add_argument("--system", required=True, choices=SYSTEMS)
    command.add_argument("--lane", choices=LANES, default="all", help="the lane whose shards to hold to it")
    command.set_defaults(run=cover)

    command = commands.add_parser("failures", help="table the failed tests of results in the job's summary")
    command.add_argument("results", help="downloaded artifacts of shards, or a folder of .trx results")
    command.add_argument("--title", default="Failed tests", help="the table's heading")
    command.set_defaults(run=failures)

    command = commands.add_parser("hang", help="print the arguments by which dotnet test stops and names a test hung for "
                                  "HANG, on one line, for a workflow's own dotnet test of the tests")
    command.set_defaults(run=hang)

    command = commands.add_parser("update", help="rewrite TestTimes.json from CI runs' results")
    command.add_argument("runs", nargs="+", help="CI runs' numbers (downloaded with gh) or folders of their artifacts, the "
                         "newest first")
    command.add_argument("--times", default=TIMES)
    command.set_defaults(run=update)

    arguments = parser.parse_args()
    return arguments.run(arguments)


if __name__ == "__main__":
    sys.exit(main())
