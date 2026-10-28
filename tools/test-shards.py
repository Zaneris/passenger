#!/usr/bin/env python3
"""Which of a system's CI shards runs each test class, and the checks that every test runs in exactly one of them.

A lane is a part of the suite: the gate's [Gate] tests and the unit tests, the report lane's every other test, or all.
`list` writes a lane as `dotnet test --list-tests` prints it, listing again while a project names no test or says
discovery failed. `split` deals its classes by their seconds on that system in TestTimes.json (else their budget), the
longest first, each to the shard with the fewest so far, so every shard of a commit deals the same. `check` fails unless
every test is in exactly one shard's settings; `cover`, after the shards, unless each succeeded and every test has
exactly one result. `failures` tables a lane's failed tests. `update` (`just test-times RUN`) rewrites TestTimes.json.
"""
import argparse
import importlib.util
import json
import os
import shutil
import subprocess
import sys
from collections import Counter
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TIMES = os.path.join(ROOT, "tests", "Passenger.Tests", "TestTimes.json")
BUDGET = os.path.join(ROOT, "tests", "Passenger.Tests", "TestBudget.json")
PROJECT = os.path.join(ROOT, "tests", "Passenger.Tests")
# Pure logic's tests, every one in the gate; a checkout may not have them yet.
UNIT_PROJECT = os.path.join(ROOT, "tests", "Passenger.UnitTests")
UNIT_PREFIX = "Passenger.UnitTests."
# A unit test class's seconds while TestTimes.json has none for it: its tests take milliseconds.
UNIT_CLASS_SECONDS = 1
SYSTEMS = ("macOS", "Windows", "Linux")
LANES = ("gate", "report", "all")
# The trait [Gate] gives a test (tests/Passenger.Tests/Gate.cs).
GATE = "Gate"
# What xunit's adapter writes into the listing when it could not discover an assembly's tests.
DISCOVERY_FAILED = "Exception discovering tests"
# How much sooner the slowest shard must end, dealt from a run's own times, before the summary asks for a new deal.
WORTH_DEALING_AGAIN = 120

# test-times.py's .trx reading, loaded by its file since -I keeps this folder off the path, leaving no __pycache__.
sys.dont_write_bytecode = True
_spec = importlib.util.spec_from_file_location("test_times", os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                                          "test-times.py"))
trx = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(trx)
PREFIX = trx.PREFIX


def say(kind, title, message):
    print(f"::{kind} title={title}::{message}" if os.environ.get("GITHUB_ACTIONS") == "true" else f"{kind}: {message}")


def summarize(lines):
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as file:
            file.write("\n".join(lines) + "\n\n")


def some(names):
    return ", ".join(names[:20]) + (" …" if len(names) > 20 else "")


def key(name):
    """A test's display name as both dotnet test's listing and a .trx carry it: a Windows console writes what it cannot
    encode its own way, so every character past ASCII counts as one '?'."""
    return "".join(c if c.isascii() else "?" for c in name)


def unit_tests():
    """Whether the checkout has the unit tests' project."""
    return os.path.isfile(os.path.join(UNIT_PROJECT, "Passenger.UnitTests.csproj"))


def projects(lane):
    """The test projects holding a lane's tests: the unit tests' only in the gate (and in all)."""
    return [PROJECT] + ([UNIT_PROJECT] if lane != "report" and unit_tests() else [])


def lane_filter(lane):
    """dotnet test's filter that selects a lane's tests from its projects, empty for all."""
    return {"gate": f"{GATE}=true|FullyQualifiedName~{UNIT_PREFIX}",
            "report": f"{GATE}!=true&FullyQualifiedName!~{UNIT_PREFIX}", "all": ""}[lane]


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
        options = ["--filter", lane_filter(arguments.lane)] if lane_filter(arguments.lane) else []
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
    print(f"{len(tests_in(printed.decode('utf-8', errors='replace')))} {arguments.lane} tests listed from "
          f"{', '.join(shown(p) for p in chosen)}{f' with {arguments.settings}' if arguments.settings else ''}")
    return 0


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
    name, but its name would be dealt too, as a class weighing a budget."""
    whole = name.split("(", 1)[0].rsplit(".", 1)[0]
    return whole[len(PREFIX):] if whole.startswith(PREFIX) else whole


def load(path, default):
    if not os.path.exists(path):
        return default
    with open(path, encoding="utf-8") as file:
        return json.load(file)


def budgeted(name, budget):
    """A class's seconds by its budget, for one TestTimes.json has none for."""
    default = UNIT_CLASS_SECONDS if name.startswith(UNIT_PREFIX) else budget.get("classSeconds", 60)
    return budget.get("classes", {}).get(name, default)


def shares(chosen, whole):
    """Each class's share of its tests among those chosen, by the counts of each in the two listings."""
    return {name: min(1.0, count / whole[name]) if whole.get(name) else 1.0 for name, count in chosen.items()}


def weights(classes, system, times, budget, share):
    """Each class's seconds on this system (else its budget) times its share of its tests, and the classes so weighed."""
    known = times.get(system, {})
    guessed = sorted(c for c in classes if c not in known)
    return {c: (known[c] if c in known else budgeted(c, budget)) * share.get(c, 1.0) for c in classes}, guessed


def deal(weights, count):
    """The classes in count hands, the heaviest first, each to the lightest hand so far (the first of equals)."""
    hands = [[] for _ in range(count)]
    totals = [0.0] * count
    for name in sorted(weights, key=lambda c: (-weights[c], c)):
        lightest = min(range(count), key=lambda i: (totals[i], i))
        hands[lightest].append(name)
        totals[lightest] += weights[name]
    return hands, totals


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


def split(arguments):
    tests = listing(arguments.suite)
    classes = sorted(set(owner(test) for test in tests))
    if arguments.of < 1 or len(classes) < arguments.of:
        sys.exit(f"test-shards: {len(classes)} classes cannot be dealt to {arguments.of} shards")
    counts = Counter(owner(test) for test in tests)
    whole = Counter(owner(test) for test in listing(arguments.whole)) if arguments.whole else counts
    seconds, guessed = weights(classes, arguments.system, load(arguments.times, {}), load(arguments.budget, {}),
                               shares(counts, whole))
    hands, totals = deal(seconds, arguments.of)
    lane = "" if arguments.lane == "all" else f" of the {arguments.lane} lane"
    shared = "" if arguments.lane == "all" else "; a class with tests outside the lane by its share of them"
    print(f"{len(tests)} tests in {len(classes)} classes{lane}, dealt to {arguments.of} shards by their seconds on "
          f"{arguments.system} (TestTimes.json; a class it lacks by its budget{shared})")
    for number, (hand, total) in enumerate(zip(hands, totals), 1):
        print(f"\nShard {number}: {len(hand)} classes, {sum(counts[c] for c in hand)} tests, "
              f"about {total / 60:.1f} minutes")
        for name in hand:
            print(f"  {name:<44} {seconds[name]:>7.1f}"
                  + (f"  ({counts[name]} of its {whole[name]} tests)" if counts[name] < whole[name] else "")
                  + ("  (its budget: TestTimes.json has none)" if name in guessed else ""))
    if guessed:
        say("notice", "Test times", f"TestTimes.json has no {arguments.system} seconds for {', '.join(guessed)}, so "
            "each is dealt by its budget: just test-times RUN brings them in from a run")
    if arguments.out:
        os.makedirs(arguments.out, exist_ok=True)
        for number, hand in enumerate(hands, 1):
            with open(os.path.join(arguments.out, f"shard-{number}.runsettings"), "w", encoding="utf-8") as file:
                file.write(run_settings(hand, lane_filter(arguments.lane)))
            with open(os.path.join(arguments.out, f"shard-{number}.projects"), "w", encoding="utf-8",
                      newline="\n") as file:
                file.write("".join(shown(project) + "\n" for project in hand_projects(hand)))
    return 0


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


def shards(results, system, lane):
    """One system's shards of a lane among downloaded results, by number: the folders whose shard.json says they are
    that shard (one that names no lane, from before there were two, is all's)."""
    found = {}
    for folder, _, files in sorted(os.walk(results)):
        if "shard.json" in files:
            state = load(os.path.join(folder, "shard.json"), {})
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
    for number, folder in folders.items():
        hand = hands.setdefault(number, {})
        for file in trx.trx_files([folder]):
            for test_class, name, spent, _ in trx.results(file)[1]:
                ran[key(name)] += 1
                hand[test_class] = hand.get(test_class, 0.0) + spent
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
        slowest = max(sum(hand.values()) for hand in hands.values())
        _, totals = deal(seconds, len(hands))
        if slowest - max(totals) >= WORTH_DEALING_AGAIN:
            message = (f"Dealt from this run's own times, the slowest shard on {where} would take "
                       f"{max(totals) / 60:.1f} minutes of tests rather than {slowest / 60:.1f}: `just test-times "
                       f"{os.environ.get('GITHUB_RUN_ID', 'RUN')}` writes them to TestTimes.json, to commit")
            say("warning", "Test shards out of balance", message)
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
        if not {"shard.json", "suite.txt", "whole.txt"} & set(files):
            continue
        system, _ = trx.shard_of(os.path.join(path, "shard.json"))
        lane = load(os.path.join(path, "shard.json"), {}).get("lane", "all")
        entry = found.setdefault(system, {"lanes": {lane}, "listed": {}})
        entry["lanes"].add(lane)
        for name, listed in (("suite.txt", lane), ("whole.txt", "all")):
            # A shard whose listing failed ran nothing, and says nothing of the suite.
            if name in files and unlisted(text := read(os.path.join(path, name))) is None:
                entry["listed"].setdefault(listed, Counter(owner(test) for test in tests_in(text)))
    return found


def held_whole(entry):
    """The classes a system's results hold whole, None for all of them: a run of every lane (or from before there were
    lanes) holds each; a run of one lane, only those with no test outside it, by its listings of the lane and of all."""
    if entry is None or "all" in entry["lanes"] or {"gate", "report"} <= entry["lanes"]:
        return None
    whole = entry["listed"].get("all", Counter())
    ran = sum((entry["listed"].get(lane, Counter()) for lane in entry["lanes"]), Counter())
    return {name for name, count in ran.items() if count >= whole[name] > 0}


def update(arguments):
    """TestTimes.json from runs' results: each class's seconds on each system, the mean of the runs that ran it whole."""
    old = load(arguments.times, {})
    spent = {}
    suites = {}
    partly = {}
    for source in arguments.runs:
        folder = source if os.path.isdir(source) else download(source) if source.isdigit() else None
        if folder is None:
            sys.exit(f"test-shards: {source} is neither a folder of results nor a run's number")
        listed = listings(folder)
        files = trx.trx_files([folder])
        if not files:
            sys.exit(f"test-shards: {folder} holds no .trx results")
        this = {}
        for file in files:
            system, _ = trx.shard_of(file)
            if system not in SYSTEMS:
                sys.exit(f"test-shards: nothing says which system {file} ran on: no shard.json beside it, and no "
                         "test-results-SYSTEM folder over it")
            whole = held_whole(listed.get(system))
            for test_class, _, seconds, _ in trx.results(file)[1]:
                if whole is not None and test_class not in whole:
                    partly.setdefault(system, set()).add(test_class)
                    continue
                this.setdefault(system, {}).setdefault(test_class, 0.0)
                this[system][test_class] += seconds
        for system, entry in listed.items():
            suites.setdefault(system, set()).update(name for counts in entry["listed"].values() for name in counts)
        for system, classes in this.items():
            for test_class, seconds in classes.items():
                spent.setdefault(system, {}).setdefault(test_class, []).append(seconds)
    times = {}
    for system in SYSTEMS:
        if system not in spent:
            if system in old:
                times[system] = old[system]
            continue
        measured = {name: round(sum(values) / len(values), 1) for name, values in spent[system].items()}
        # A class a shard that stopped early never reported keeps its seconds from before, while the suite has it.
        kept = {name: seconds for name, seconds in old.get(system, {}).items()
                if name not in measured and name in suites.get(system, set())}
        times[system] = dict(sorted({**kept, **measured}.items()))
        unmeasured = partly.get(system, set()) - set(measured)
        print(f"{system}: {len(measured)} classes measured" + (f", {len(kept)} kept from before" if kept else "")
              + (f" ({len(unmeasured)} ran only in part, in a run of one lane)" if unmeasured else ""))
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
    command.add_argument("--settings", help="a shard's dotnet test settings, to list only its hand")
    command.add_argument("--tries", type=int, default=3)
    command.set_defaults(run=list_tests)

    command = commands.add_parser("split", help="deal a system's test classes to its shards, their settings to --out")
    command.add_argument("suite", help="what dotnet test --list-tests printed for the lane")
    command.add_argument("--lane", choices=LANES, default="all", help="the lane listed, which the shards' settings select")
    command.add_argument("--whole", help="what it printed for all, by which a class with tests outside the lane weighs "
                         "its share of its seconds")
    command.add_argument("--system", required=True, choices=SYSTEMS)
    command.add_argument("--of", type=int, required=True, help="how many shards")
    command.add_argument("--out", help="a folder for each shard's dotnet test settings, shard-N.runsettings, and its "
                         "projects, shard-N.projects")
    command.add_argument("--times", default=TIMES)
    command.add_argument("--budget", default=BUDGET)
    command.set_defaults(run=split)

    command = commands.add_parser("check", help="fail unless every test of the lane is in exactly one shard's listing")
    command.add_argument("suite", help="what dotnet test --list-tests printed for the lane")
    command.add_argument("listed", nargs="+", help="what it printed with each shard's settings")
    command.set_defaults(run=check)

    command = commands.add_parser("cover", help="fail unless every shard succeeded and every test has exactly one result")
    command.add_argument("results", help="the downloaded artifacts of the system's shards, each with its shard.json")
    command.add_argument("--system", required=True, choices=SYSTEMS)
    command.add_argument("--lane", choices=LANES, default="all", help="the lane whose shards to hold to it")
    command.set_defaults(run=cover)

    command = commands.add_parser("failures", help="table the failed tests of results in the job's summary")
    command.add_argument("results", help="downloaded artifacts of shards, or a folder of .trx results")
    command.add_argument("--title", default="Failed tests", help="the table's heading")
    command.set_defaults(run=failures)

    command = commands.add_parser("update", help="rewrite TestTimes.json from CI runs' results")
    command.add_argument("runs", nargs="+", help="a CI run's number (downloaded with gh), or a folder of its artifacts")
    command.add_argument("--times", default=TIMES)
    command.set_defaults(run=update)

    arguments = parser.parse_args()
    return arguments.run(arguments)


if __name__ == "__main__":
    sys.exit(main())
