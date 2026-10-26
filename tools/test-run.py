#!/usr/bin/env python3
"""The whole suite on one machine as several test processes at once, then what must run alone (`just test`).

1. Every class outside the Alone collection, dealt to N processes, since one runs its window tests one at a time: by its
   seconds (the most any system took in TestTimes.json, else its budget), the heaviest first, each to the lightest
   process so far. The classes run beside the window tests are dealt apart, their heaviest hand to the lightest window
   hand; the container engine's go to one process together (LANES). Limits are scaled by SIDE_BY_SIDE_SCALE.
2. The Alone classes in one process once the first phase has ended, so nothing runs beside them.

Results go to artifacts/test-results/local/ (each process's .trx and console log); the failures are listed at the end.
"""
import argparse
import glob
import importlib.util
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ElementTree
from xml.sax.saxutils import escape

sys.dont_write_bytecode = True
_spec = importlib.util.spec_from_file_location("test_shards", os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                                           "test-shards.py"))
shards = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(shards)
trx = shards.trx

ROOT = shards.ROOT
PROJECT = shards.PROJECT
ASSEMBLY = os.path.join(ROOT, "artifacts", "bin", "Passenger.Tests", "debug", "Passenger.Tests.dll")
RESULTS = os.path.join(ROOT, "artifacts", "test-results", "local")
SYSTEM = {"Darwin": "macOS", "Windows": "Windows", "Linux": "Linux"}.get(platform.system(), "Linux")
TIME_SCALE = "PASSENGER_TEST_TIME_SCALE"
# The first phase's limits, its processes sharing the machine: a test's limits are written for a machine that runs it
# alone, and this lengthens only how long a test may wait, never how long it does.
SIDE_BY_SIDE_SCALE = "1.5"
# A hung test is stopped and named after this, rather than holding the run.
HANG = "10m"
# Classes that share something of the machine beyond the run's own folder, run in one process together in the first phase:
# the container engine (its virtual machine's processors, image builds, Compose), by what a class's source calls.
LANES = {
    "the container engine": re.compile(r"\bDocker\.(?:WhyNot|Run|Ask|Said|ComposeUp|Container|State|VolumeExists|Stopped"
                                       r"|TakenDown|NoneLeft|Images?)\b|\bContainerPrograms\b|\bStandInEngine\b"
                                       r"|\bContainerEngine\.Shared\b"),
}
# The collections that run beside the window tests' own in a process (Collections.cs).
BESIDE = ("Collection(DebugSession.Collection)", "Collection(Beside.Name)")
DECLARATION = re.compile(r"^(?P<attributes>(?:\[[^\n]*\][ \t]*\r?\n)*)(?:(?:public|internal|sealed|static|partial|abstract"
                         r"|file)\s+)*class\s+(?P<name>\w+)(?P<rest>[^\n{]*)", re.M)
NAMESPACE = re.compile(r"^namespace\s+(?P<name>[\w.]+)\s*;", re.M)
# A test's result as the console logger writes it, never a line of a test's message ("Failed to restore …").
RESULT = re.compile(r"^  (?P<outcome>Passed|Failed|Skipped) " + re.escape(trx.PREFIX) + r"\S+")


def sources():
    """Each top-level class the test project declares, by its name as a listing's tests give it (without PREFIX): whether
    its declaration says it is Alone or runs beside the window tests, its bases' simple names, and the lanes its source
    names."""
    declared = {}
    for path in glob.glob(os.path.join(PROJECT, "**", "*.cs"), recursive=True):
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
            name = whole[len(trx.PREFIX):] if whole.startswith(trx.PREFIX) else whole
            rest = declaration["rest"]
            bases = re.findall(r"\b([A-Z]\w*)", rest.split(":", 1)[1]) if ":" in rest else []
            declared[name] = {
                "alone": "Collection(Alone.Name)" in declaration["attributes"],
                "beside": any(collection in declaration["attributes"] for collection in BESIDE),
                "bases": bases,
                "lanes": [lane for lane, pattern in LANES.items() if pattern.search(body)],
            }
    return declared


def declares(name, declared, key, unknown):
    """Whether a class's declaration, or a base class's, says what key names; unknown for one the sources do not show."""
    by_simple = {full.rsplit(".", 1)[-1]: value for full, value in declared.items()}
    seen = set()
    entry = declared.get(name)
    if entry is None:
        return unknown
    while entry is not None:
        if entry[key]:
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


def weight(name, times, budget):
    """A class's seconds for dealing here: the most any system's runs took for it, else its budget."""
    known = [system[name] for system in times.values() if name in system]
    return max(known) if known else budget.get("classes", {}).get(name, budget.get("classSeconds", 60))


def settings(classes, given):
    """dotnet test's settings that run these classes' tests, and of them only those the given filter selects."""
    names = "|".join(f"FullyQualifiedName~{trx.PREFIX}{name}." for name in sorted(classes))
    chosen = f"({given})&({names})" if given else names
    return ("<RunSettings>\n  <RunConfiguration>\n    <TestCaseFilter>" + escape(chosen)
            + "</TestCaseFilter>\n  </RunConfiguration>\n</RunSettings>\n")


def default_processes():
    """How many processes the first phase runs at once: a quarter of the processors, and no more than one per 8 GB of
    memory where the system says how much it has (each holds a window's language server, its project host and MSBuild's
    nodes), between 1 and 4."""
    count = max(1, (os.cpu_count() or 1) // 4)
    memory = None
    try:
        if SYSTEM == "macOS":
            memory = int(subprocess.run(["sysctl", "-n", "hw.memsize"], capture_output=True, text=True, check=True).stdout)
        elif SYSTEM == "Linux":
            memory = os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES")
    except (OSError, ValueError, subprocess.CalledProcessError):
        memory = None
    if memory:
        count = min(count, max(1, memory // (8 << 30)))
    return min(4, count)


def listing(given):
    """The tests dotnet test lists, with the filter given, asked again while a listing says discovery failed
    (xunit #3576); None, saying why, when the third is no better. An empty listing with a filter is no test chosen."""
    command = ["dotnet", "test", ASSEMBLY, "--list-tests"] + (["--filter", given] if given else [])
    why = None
    for attempt in range(1, 4):
        done = subprocess.run(command, stdout=subprocess.PIPE, check=False)
        text = done.stdout.decode("utf-8", errors="replace")
        tests = shards.tests_in(text)
        if given and tests == [] and shards.DISCOVERY_FAILED not in text:
            return []
        why = shards.unlisted(text)
        if why is None:
            return tests
        print(f"The listing of the tests (try {attempt} of 3) is no listing of the suite: {why}", flush=True)
    shards.echo(done.stdout)
    return None


class Process:
    """One dotnet test of the run, its words written to its log, its results to its own folder."""

    def __init__(self, label, classes, given, folder, scale, shown):
        self.label = label
        self.classes = classes
        self.folder = folder
        self.log_path = os.path.join(folder, "console.log")
        os.makedirs(folder, exist_ok=True)
        settings_path = os.path.join(folder, "hand.runsettings")
        with open(settings_path, "w", encoding="utf-8") as file:
            file.write(settings(classes, given))
        environment = dict(os.environ)
        if scale is not None:
            environment[TIME_SCALE] = scale
        command = ["dotnet", "test", ASSEMBLY, "--settings", settings_path, "--results-directory", folder,
                   "--logger", "trx", "--logger", "console;verbosity=normal",
                   "--blame-hang-timeout", HANG, "--blame-hang-dump-type", "none"]
        self.started = time.monotonic()
        self.log = open(self.log_path, "w", encoding="utf-8", errors="replace")
        if shown:
            self.process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                                            stderr=subprocess.STDOUT, bufsize=1, encoding="utf-8", errors="replace")
        else:
            self.process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=self.log, stderr=subprocess.STDOUT)

    def follow(self):
        """For a process whose words are shown: copies them to the terminal and its log until it ends."""
        for line in self.process.stdout:
            sys.stdout.write(line)
            self.log.write(line)
        self.process.wait()

    def counts(self):
        """How many of its tests have passed, failed and been skipped so far, by the lines its console logger wrote."""
        if not self.log.closed:
            self.log.flush()
        found = {"Passed": 0, "Failed": 0, "Skipped": 0}
        try:
            with open(self.log_path, encoding="utf-8", errors="replace") as file:
                for line in file:
                    if result := RESULT.match(line):
                        found[result["outcome"]] += 1
        except OSError:
            pass
        return found

    def end(self):
        self.log.close()
        return self.process.returncode

    def minutes(self):
        return (time.monotonic() - self.started) / 60


def stop(processes):
    """Ends the run's processes still going (Ctrl+C reaches them too, from the terminal), each with all it started."""
    for process in processes:
        if process.process.poll() is None:
            process.process.terminate()
    for process in processes:
        try:
            process.process.wait(timeout=60)
        except subprocess.TimeoutExpired:
            process.process.kill()


def failures(folder):
    """Every test that ran and did not pass, with the first lines of what it said, from the run's .trx results."""
    found = []
    for file in trx.trx_files([folder]):
        root = ElementTree.parse(file).getroot()
        for result in root.iter(f"{trx.TRX}UnitTestResult"):
            if result.get("outcome") in ("Passed", "NotExecuted"):
                continue
            message = result.find(f"{trx.TRX}Output/{trx.TRX}ErrorInfo/{trx.TRX}Message")
            text = (message.text or "").strip() if message is not None else ""
            found.append((result.get("testName", "?"), result.get("outcome"), "\n      ".join(text.splitlines()[:6])))
    return found


def plural(count, word):
    return f"{count} {word}" if count == 1 else f"{count} {word}es" if word.endswith("s") else f"{count} {word}s"


def tally(folder):
    counts = {}
    for file in trx.trx_files([folder]):
        for _, _, _, outcome in trx.results(file)[1]:
            said = {"NotExecuted": "skipped"}.get(outcome, outcome.lower())
            counts[said] = counts.get(said, 0) + 1
    return counts


def one_process(given):
    """The suite as one dotnet test, which builds what it needs first, xunit keeping its own order."""
    return subprocess.run(["dotnet", "test", PROJECT] + (["--filter", given] if given else []), cwd=ROOT).returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("filter", nargs="?", default="", help="dotnet test's --filter, as `just test Editing` gives it")
    parser.add_argument("--processes", type=int, help="how many processes the first phase runs at once (default: "
                        "PASSENGER_TEST_PROCESSES, else a quarter of the processors, at most 4)")
    parser.add_argument("--plan", action="store_true", help="say how the classes would be dealt, and run none")
    arguments = parser.parse_args()
    given = arguments.filter.strip()
    count = arguments.processes
    if count is None and os.environ.get("PASSENGER_TEST_PROCESSES"):
        try:
            count = int(os.environ["PASSENGER_TEST_PROCESSES"])
        except ValueError:
            sys.exit(f"test-run: PASSENGER_TEST_PROCESSES is \"{os.environ['PASSENGER_TEST_PROCESSES']}\": it must be a "
                     "whole number of processes, 1 or more")
    if count is None:
        count = default_processes()
    if count < 1:
        sys.exit(f"test-run: {count} processes cannot run the tests: give 1 or more")
    if count == 1:
        return one_process(given)
    if TIME_SCALE in os.environ:
        # Passed on unchecked: a malformed one fails every test, saying why.
        side_by_side = os.environ[TIME_SCALE]
    else:
        side_by_side = SIDE_BY_SIDE_SCALE

    began = time.monotonic()
    built = subprocess.run(["dotnet", "build", PROJECT], cwd=ROOT)
    if built.returncode != 0:
        return built.returncode
    tests = listing(given)
    if tests is None:
        print("test-run: dotnet test listed no tests, so none ran", file=sys.stderr)
        return 1
    if not tests:
        print(f"No test matches the filter {given}")
        return 1
    classes = sorted(set(shards.owner(test) for test in tests))
    declared = sources()
    times = shards.load(shards.TIMES, {})
    budget = shards.load(shards.BUDGET, {})
    last = [name for name in classes if alone(name, declared)]
    rest = [name for name in classes if name not in last]

    units = {}
    side = set()
    for name in rest:
        lanes = declared.get(name, {}).get("lanes", [])
        unit = f"[{lanes[0]}]" if lanes else name
        units.setdefault(unit, []).append(name)
        if not lanes and beside(name, declared):
            side.add(unit)
    unit_weights = {unit: sum(weight(name, times, budget) for name in members) for unit, members in units.items()}
    windowed = {unit: seconds for unit, seconds in unit_weights.items() if unit not in side}
    besides = {unit: seconds for unit, seconds in unit_weights.items() if unit in side}
    count = min(count, max(len(windowed), len(besides)))
    window_hands, window_totals = shards.deal(windowed, count) if count else ([], [])
    side_hands, side_totals = shards.deal(besides, count) if count else ([], [])
    by_window = sorted(range(count), key=lambda i: window_totals[i])
    by_side = sorted(range(count), key=lambda i: -side_totals[i])
    hands = [window_hands[w] + side_hands[b] for w, b in zip(by_window, by_side)]
    totals = [max(window_totals[w], side_totals[b]) for w, b in zip(by_window, by_side)]
    if arguments.plan:
        for number, (hand, total) in enumerate(zip(hands, totals), 1):
            print(f"\nProcess {number}, about {total / 60:.1f} minutes by CI's times:")
            for unit in hand:
                print(f"  {unit:<44} {unit_weights[unit]:>7.1f}" + (f"  ({', '.join(units[unit])})" if unit.startswith("[") else "")
                      + ("  (beside)" if unit in side else ""))
        print(f"\nAlone, after them, about {sum(weight(n, times, budget) for n in last) / 60:.1f} minutes by CI's times:")
        for name in last:
            print(f"  {name:<44} {weight(name, times, budget):>7.1f}" + ("" if name in declared else "  (not in the sources)"))
        return 0

    shutil.rmtree(RESULTS, ignore_errors=True)
    os.makedirs(RESULTS)
    print(f"\n{plural(len(tests), 'test')} in {plural(len(classes), 'class')}: {plural(len(rest), 'class')} in "
          f"{plural(len(hands), 'process')} at once, then {plural(len(last), 'class')} alone. Results and each process's "
          f"log: {os.path.relpath(RESULTS, ROOT)}", flush=True)
    failed_any = False
    processes = []
    try:
        for number, (hand, total) in enumerate(zip(hands, totals), 1):
            members = [name for unit in hand for name in units[unit]]
            lanes = [unit for unit in hand if unit.startswith("[")]
            print(f"  Process {number}: {plural(len(members), 'class')}, about {total / 60:.1f} minutes by CI's times"
                  + (f", with {', '.join(lanes)}" if lanes else ""), flush=True)
            processes.append(Process(f"Process {number}", members, given, os.path.join(RESULTS, f"process-{number}"),
                                     side_by_side, shown=False))
        report = time.monotonic()
        while any(p.process.poll() is None for p in processes):
            time.sleep(1)
            for process in processes:
                if process.process.poll() is not None and not process.log.closed:
                    code = process.end()
                    counts = process.counts()
                    failed_any |= code != 0
                    print(f"  {process.label} ended after {process.minutes():.1f} minutes: {counts['Passed']} passed, "
                          f"{counts['Failed']} failed, {counts['Skipped']} skipped"
                          + (f" (exit code {code}; its log: {os.path.relpath(process.log_path, ROOT)})" if code else ""),
                          flush=True)
                    for name, outcome, message in failures(process.folder):
                        print(f"    {name} ({outcome})" + (f"\n      {message}" if message else ""), flush=True)
            if time.monotonic() - report >= 60:
                report = time.monotonic()
                going = [p for p in processes if p.process.poll() is None]
                print("  " + "; ".join(f"{p.label}: {c['Passed']} passed, {c['Failed']} failed" for p in going
                                       for c in [p.counts()]), flush=True)
        for process in processes:
            if not process.log.closed:
                failed_any |= process.end() != 0
        first = time.monotonic()

        if last:
            print(f"\n{plural(len(last), 'class')} alone, in one process with nothing beside it:", flush=True)
            alone_process = Process("Alone", last, given, os.path.join(RESULTS, "alone"), None, shown=True)
            processes.append(alone_process)
            alone_process.follow()
            failed_any |= alone_process.end() != 0
    except KeyboardInterrupt:
        stop(processes)
        print("\ntest-run: stopped", file=sys.stderr)
        return 130

    counts = tally(RESULTS)
    failed = failures(RESULTS)
    print(f"\n{plural(sum(counts.values()), 'test')}: " + ", ".join(f"{n} {said}" for said, n in sorted(counts.items()))
          + f". The build and the processes side by side {(first - began) / 60:.1f} minutes, those alone "
          f"{(time.monotonic() - first) / 60:.1f}, {(time.monotonic() - began) / 60:.1f} in all.")
    for name, outcome, message in failed:
        print(f"  {name} ({outcome})" + (f"\n      {message}" if message else ""))
    if failed_any and not failed:
        print("A process ended with a failure and no failed test in its results: its log says what crashed or hung")
    return 1 if failed_any or failed else 0


if __name__ == "__main__":
    sys.exit(main())
