#!/usr/bin/env python3
"""Each test class's time in a run of Passenger's tests, from the run's .trx results, held to the suite's budget.

Prints every class with its tests and seconds (xunit's own time for each test, its class made and disposed; a class
fixture's setup is outside it), the slowest first, and each run's time from its start to its end. Given TestBudget.json,
a class over its seconds is a warning and a shard over its minutes fails (the report lane's only warns): the budget says
how long a job may take before someone decides it should, and a class's seconds say where the time went. On GitHub
Actions they are annotations and the table goes in the step's summary.

python3 -I tools/test-times.py artifacts/test-results --budget tests/Passenger.Tests/TestBudget.json
"""
import argparse
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ElementTree
from datetime import datetime, timedelta, timezone

TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
PREFIX = "Passenger.Tests."
# The folder a system's results come in, as its shard's job names its artifact (with its lane since there are two) and a
# download makes it a folder.
SHARD_FOLDER = re.compile(r"^test-results-(?P<system>[A-Za-z]+)(?:-(?:gate|report))?(?:-(?P<shard>\d+))?$")


def when(text):
    """A .trx time, 2026-10-13T20:38:14.1234567-07:00, which Python reads only to the microsecond."""
    stamp, zone = text[:-6], text[-6:]
    if text.endswith("Z"):
        stamp, zone = text[:-1], "+00:00"
    whole, _, fraction = stamp.partition(".")
    moment = datetime.strptime(whole, "%Y-%m-%dT%H:%M:%S") + timedelta(microseconds=int((fraction + "000000")[:6]))
    sign = -1 if zone[0] == "-" else 1
    return moment.replace(tzinfo=timezone(sign * timedelta(hours=int(zone[1:3]), minutes=int(zone[4:6]))))


def seconds(duration):
    hours, minutes, rest = duration.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(rest)


def trx_files(paths):
    files = []
    for path in paths:
        files += sorted(glob.glob(os.path.join(path, "**", "*.trx"), recursive=True)) if os.path.isdir(path) else [path]
    return files


def shard_of(file):
    """The system and shard number a result came from: the shard.json its shard's job leaves beside its .trx, else the
    folder its artifact was downloaded to (test-results-macOS-2, or test-results-macOS from an unsharded run), else
    (None, None). A download of one artifact lands in the folder asked for, not in one named after it."""
    folder = os.path.dirname(os.path.abspath(file))
    if os.path.exists(os.path.join(folder, "shard.json")):
        with open(os.path.join(folder, "shard.json"), encoding="utf-8") as stream:
            shard = json.load(stream)
        return shard.get("system"), shard.get("shard")
    for part in reversed(os.path.normpath(folder).split(os.sep)):
        match = SHARD_FOLDER.match(part)
        if match:
            return match["system"], int(match["shard"]) if match["shard"] else None
    return None, None


def results(file):
    """A .trx's run seconds and its results, each (class, display name, seconds, outcome), the class without PREFIX."""
    root = ElementTree.parse(file).getroot()
    times = root.find(f"{TRX}Times")
    run = (when(times.get("finish")) - when(times.get("start"))).total_seconds()
    owner = {test.get("id"): test.find(f"{TRX}TestMethod").get("className") for test in root.iter(f"{TRX}UnitTest")}
    found = []
    for result in root.iter(f"{TRX}UnitTestResult"):
        name = owner.get(result.get("testId"), "?")
        name = name[len(PREFIX):] if name.startswith(PREFIX) else name
        spent = seconds(result.get("duration", "00:00:00"))
        found.append((name, result.get("testName", ""), spent, result.get("outcome")))
    return run, found


def failed(file):
    """A .trx's tests that ran and did not pass, each (display name, outcome, what it said)."""
    found = []
    for result in ElementTree.parse(file).getroot().iter(f"{TRX}UnitTestResult"):
        if result.get("outcome") in ("Passed", "NotExecuted"):
            continue
        message = result.find(f"{TRX}Output/{TRX}ErrorInfo/{TRX}Message")
        found.append((result.get("testName", "?"), result.get("outcome"),
                      (message.text or "").strip() if message is not None else ""))
    return found


def read(paths, shard=None):
    """Each class's tests, seconds, failures and shard, and each run's shard and seconds, from every .trx under the paths;
    a run's shard is the given one, else its folder's, and a shard that ran several projects took their runs' seconds."""
    files = trx_files(paths)
    if not files:
        sys.exit(f"test-times: no .trx results under {', '.join(paths)}: run dotnet test with --logger trx")
    classes = {}
    spent_by = {}
    unsharded = []
    for file in files:
        run, found = results(file)
        number = shard if shard is not None else shard_of(file)[1]
        if number is None:
            unsharded.append((None, run))
        else:
            spent_by[number] = spent_by.get(number, 0.0) + run
        for name, _, spent, outcome in found:
            entry = classes.setdefault(name, {"tests": 0, "seconds": 0.0, "failed": 0, "shard": number})
            entry["tests"] += 1
            entry["seconds"] += spent
            entry["failed"] += outcome not in ("Passed", "NotExecuted")
    return classes, sorted(spent_by.items()) + unsharded


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("results", nargs="+", help="a .trx file, or a folder holding them")
    parser.add_argument("--budget", help="the suite's budget: tests/Passenger.Tests/TestBudget.json")
    parser.add_argument("--label", help="what ran, for the summary's heading: macOS (arm64), shard 2 of 3")
    parser.add_argument("--lane", choices=("gate", "report", "all"), default="all",
                        help="the lane the results are of: the report lane's shards only warn over their minutes")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--shard", type=int, help="the results are this shard's alone, not the whole suite's")
    mode.add_argument("--summary", action="store_true",
                      help="the results are every shard's, each already held to its budget by its own job")
    arguments = parser.parse_args()
    classes, runs = read(arguments.results, arguments.shard)
    budget = {}
    if arguments.budget:
        with open(arguments.budget, encoding="utf-8") as file:
            budget = json.load(file)
    run_limit = budget.get("shardMinutes")
    allowed = budget.get("classes", {})
    sharded = len(runs) > 1 and all(shard is not None for shard, _ in runs)
    # A shard runs only its own classes, and a lane only its own tests, so a budget for a class elsewhere says nothing there.
    unknown = sorted(set(allowed) - set(classes)) if arguments.shard is None and arguments.lane == "all" else []
    # The shards' own jobs have warned of their classes and failed on their minutes, so the summary only shows them.
    held = not arguments.summary

    def limit(name):
        return allowed.get(name, budget.get("classSeconds"))

    def run_name(shard):
        return f"Shard {shard}" if shard is not None else "The run"

    rows = sorted(classes.items(), key=lambda item: -item[1]["seconds"])
    total = sum(entry["seconds"] for entry in classes.values())
    lines = [f"{'Class':<44} {'Tests':>5} {'Seconds':>8} {'Budget':>7}" + (f" {'Shard':>5}" if sharded else "")]
    for name, entry in rows:
        given = limit(name)
        lines.append(f"{name:<44} {entry['tests']:>5} {entry['seconds']:>8.1f} {given if given is not None else '':>7}"
                     + (f" {entry['shard']:>5}" if sharded else "")
                     + (f"  {entry['failed']} failed" if entry["failed"] else ""))
    lines.append(f"{'Every class':<44} {sum(e['tests'] for e in classes.values()):>5} {total:>8.1f}")
    print("\n".join(lines))
    print()
    for shard, run in runs:
        print(f"{run_name(shard)} took {run / 60:.1f} minutes" + (f", of a budget of {run_limit}" if run_limit is not None else ""))

    github = os.environ.get("GITHUB_ACTIONS") == "true"
    over = [(name, entry["seconds"], limit(name)) for name, entry in rows
            if held and limit(name) is not None and entry["seconds"] > limit(name)]
    for name, spent, given in over:
        message = f"{name} took {spent:.0f} s, over its budget of {given} s in TestBudget.json"
        print(f"::warning title=Test class over budget::{message}" if github else f"warning: {message}")
    for name in unknown:
        message = f"TestBudget.json gives {name} a budget, but the run has no such class"
        print(f"::warning title=Test budget::{message}" if github else f"warning: {message}")
    late = [(shard, run) for shard, run in runs if held and run_limit is not None and run > run_limit * 60]
    blocking = arguments.lane != "report"
    for shard, run in late:
        message = (f"{run_name(shard)} took {run / 60:.1f} minutes, over a shard's budget of {run_limit} in "
                   "TestBudget.json: make what grew faster, deal the shards again from newer times (just test-times "
                   "RUN), add a shard, or raise the budget knowing why")
        kind = "error" if blocking else "warning"
        print(f"::{kind} title=Tests over budget::{message}" if github else f"{kind}: {message}")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        label = arguments.label or os.environ.get("RUNNER_OS", "this runner")
        took = ", ".join(f"{run / 60:.1f}" for _, run in runs)
        of = f" of {run_limit}" if run_limit is not None else ""
        heading = (f"### Test time on {label}: shards of {took} minutes, each{of}" if len(runs) > 1
                   else f"### Test time on {label}: {took} minutes{of}")
        with open(summary, "a", encoding="utf-8") as file:
            file.write(heading + "\n\n")
            file.write("| Class | Tests | Seconds | Budget |" + (" Shard |" if sharded else "")
                       + "\n|---|---:|---:|---:|" + ("---:|" if sharded else "") + "\n")
            for name, entry in rows:
                given = limit(name)
                mark = " **over**" if given is not None and entry["seconds"] > given else ""
                file.write(f"| {name} | {entry['tests']} | {entry['seconds']:.1f} | {given if given is not None else ''}{mark} |"
                           + (f" {entry['shard']} |" if sharded else "") + "\n")
            file.write("\n")
    return 1 if late and blocking else 0


if __name__ == "__main__":
    sys.exit(main())
