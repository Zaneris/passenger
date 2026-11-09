#!/usr/bin/env python3
"""Sweeps the real Godot projects of tools/godot-projects.json through Passenger before a release (`just godot-sweep`):
each cloned at its commit outside any repository (passenger-godot-sweep.noindex in the temporary folder), imported by
Godot, then read by a headless Passenger (mcp-call, a state of its own): diagnostics of every scene, resource, script
and shader, the main scene run and debugged headless, the project's files compared with before, and Godot's own load of
every resource beside each mark. Every .gd is parsed by Passenger's own parser (Passenger.GodotSweep). Every Godot has a
HOME of the run's own. It writes report.md and results.json in the run's folder, and opens no window.

python3 -I tools/godot-sweep.py [--godot EXECUTABLE] [--folder DIR] [--skip-run] [--parse-only] [--report RUN] [NAME...]
"""
import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SETS = os.path.join(ROOT, "tools", "godot-projects.json")
PARSER = os.path.join(ROOT, "artifacts", "bin", "Passenger.GodotSweep", "debug", "Passenger.GodotSweep")
KINDS = (".gd", ".tscn", ".tres", ".cs", ".gdshader")
SKIPPED = {".godot", ".git", "bin", "obj", ".generated", "node_modules", ".import", ".vs", ".idea"}
# How long a step may take before the sweep ends it and says so.
IMPORT_LIMIT = 900
CHECK_LIMIT = 600
CALL_LIMIT = 900
# Loads every scene, resource and script of a project as Godot does, instancing each scene, so Godot's own errors for each
# follow its CHECK line: its lines go to standard error, where Godot writes those errors, as standard output is buffered.
CHECK_SCRIPT = """extends SceneTree

func _init() -> void:
\tvar paths: Array[String] = []
\t_walk("res://", paths)
\tfor path in paths:
\t\tprinterr("CHECK ", path)
\t\tvar loaded := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REUSE)
\t\tif loaded == null:
\t\t\tprinterr("FAILED ", path)
\t\telif loaded is PackedScene:
\t\t\tvar made: Node = (loaded as PackedScene).instantiate()
\t\t\tif made == null:
\t\t\t\tprinterr("NOT MADE ", path)
\t\t\telse:
\t\t\t\tmade.free()
\tprinterr("CHECKED ", paths.size())
\tquit()

func _walk(folder: String, into: Array[String]) -> void:
\tvar dir := DirAccess.open(folder)
\tif dir == null:
\t\treturn
\tfor file in dir.get_files():
\t\tif file.ends_with(".tscn") or file.ends_with(".tres") or file.ends_with(".gd") or file.ends_with(".gdshader"):
\t\t\tinto.append(folder.path_join(file))
\tfor sub in dir.get_directories():
\t\tif not sub.begins_with(".") and sub != "addons":
\t\t\t_walk(folder.path_join(sub), into)
"""


def mcp_call_module():
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("mcp_call", os.path.join(ROOT, "tools", "mcp-call.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


MCP = mcp_call_module()


def log(said):
    print(f"{time.strftime('%H:%M:%S')} {said}", flush=True)


def godot_executable(given):
    """The Godot to sweep with: the one given, else GODOT's, else the .NET build in /Applications."""
    for candidate in (given, os.environ.get("GODOT"), "/Applications/Godot_mono.app/Contents/MacOS/Godot"):
        if candidate:
            if candidate.endswith(".app"):
                candidate = os.path.join(candidate, "Contents", "MacOS", "Godot")
            if os.access(candidate, os.X_OK):
                return os.path.abspath(candidate)
            if candidate == given:
                sys.exit(f"godot-sweep: no Godot at {given}")
    sys.exit("godot-sweep: no Godot found; name one with --godot or GODOT")


def homes(base):
    """The variables that keep a Godot's user folders under base."""
    os.makedirs(base, exist_ok=True)
    return {"HOME": base, "XDG_CONFIG_HOME": os.path.join(base, ".config"), "XDG_DATA_HOME": os.path.join(base, ".local", "share"),
            "XDG_CACHE_HOME": os.path.join(base, ".cache"), "APPDATA": os.path.join(base, "AppData", "Roaming"),
            "LOCALAPPDATA": os.path.join(base, "AppData", "Local")}


def run(command, cwd=None, env=None, limit=None, log_to=None):
    """A program's exit code, its two outputs as one in the order they came, and seconds; None for the code where it outlived
    its limit and was ended."""
    began = time.monotonic()
    try:
        done = subprocess.run(command, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace",
                              timeout=limit, stdin=subprocess.DEVNULL)
        code, out = done.returncode, done.stdout
    except subprocess.TimeoutExpired as ended:
        code = None
        out = (ended.stdout or b"").decode(errors="replace") if isinstance(ended.stdout, bytes) else (ended.stdout or "")
    if log_to:
        with open(log_to, "w") as written:
            written.write(out)
    return code, out, round(time.monotonic() - began, 1)


# The clones' git, which runs no hook of theirs: a hook a tool wrote there once (Git LFS's post-checkout) fails every
# checkout where that tool is gone.
GIT = ["git", "-c", "core.hooksPath=/dev/null"]


def clone(entry, clones):
    """The set's clone at its commit, fetched once and put back to the commit, untracked and ignored files gone."""
    folder = os.path.join(clones, entry["name"])
    if not os.path.isdir(os.path.join(folder, ".git")):
        os.makedirs(folder, exist_ok=True)
        subprocess.run([*GIT, "init", "-q", folder], check=True)
        subprocess.run([*GIT, "-C", folder, "remote", "add", "origin", entry["repository"]], check=True)
    have = subprocess.run([*GIT, "-C", folder, "cat-file", "-e", entry["commit"] + "^{commit}"], capture_output=True).returncode == 0
    if not have:
        log(f"{entry['name']}: fetching {entry['commit'][:10]}")
        subprocess.run([*GIT, "-C", folder, "fetch", "-q", "--depth", "1", "origin", entry["commit"]], check=True)
    subprocess.run([*GIT, "-C", folder, "checkout", "-q", "-f", entry["commit"]], check=True)
    subprocess.run([*GIT, "-C", folder, "clean", "-q", "-ffdx"], check=True)
    lfs = "missing"
    attributes = os.path.join(folder, ".gitattributes")
    if os.path.exists(attributes) and "filter=lfs" in open(attributes, errors="replace").read():
        if subprocess.run(["git", "lfs", "version"], capture_output=True).returncode == 0:
            subprocess.run([*GIT, "-C", folder, "lfs", "pull", "origin"], check=False, capture_output=True)
            lfs = "fetched"
    else:
        lfs = None
    return folder, lfs


def snapshot(folder):
    """Each file git would show in a folder (tracked, or untracked and not ignored) with its content's hash."""
    listed = subprocess.run(["git", "-C", folder, "ls-files", "-z", "-c", "-o", "--exclude-standard", "--", "."],
                            capture_output=True, check=True).stdout.decode(errors="replace").split("\0")
    hashes = {}
    for name in listed:
        path = os.path.join(folder, name)
        if name and os.path.isfile(path) and not os.path.islink(path):
            with open(path, "rb") as read:
                hashes[name] = hashlib.sha1(read.read()).hexdigest()
    return hashes


def changed(before, after):
    return sorted([f"changed {n}" for n in before if n in after and before[n] != after[n]] + [f"added {n}" for n in after if n not in before]
                  + [f"removed {n}" for n in before if n not in after])


def files_of(project):
    found = []
    for root, dirs, names in os.walk(project):
        dirs[:] = sorted(d for d in dirs if d not in SKIPPED)
        for name in sorted(names):
            if name.endswith(KINDS) or name == "project.godot":
                found.append(os.path.relpath(os.path.join(root, name), project))
    return found


class Passenger:
    """The headless Passenger over one project folder through mcp-call, its state its own and begun afresh."""

    def __init__(self, folder, env):
        self.folder = MCP.on_disk(folder)
        self.env = env
        serving = MCP.serving_checkout()
        sock, _, self.log = MCP.places(self.folder, serving)
        self.state = os.path.join(ROOT, "artifacts", "mcp-call", "state-" + os.path.basename(sock).removesuffix(".sock"))

    def call(self, tool, arguments=None, limit=CALL_LIMIT):
        command = [sys.executable, "-I", os.path.join(ROOT, "tools", "mcp-call.py"), self.folder, tool]
        if arguments is not None:
            command.append(json.dumps(arguments))
        code, out, seconds = run(command, env=self.env, limit=limit)
        text = "\n".join(line for line in out.splitlines() if not line.startswith("mcp-call: ") or code != 0)
        return {"tool": tool, "arguments": arguments, "code": code, "seconds": seconds, "answer": text.strip()}

    def stop(self):
        run([sys.executable, "-I", os.path.join(ROOT, "tools", "mcp-call.py"), self.folder, "--stop"], env=self.env, limit=180)

    def fresh(self):
        self.stop()
        subprocess.run(["rm", "-rf", self.state], check=False)


def godot_errors(text):
    """Godot's error lines of a log, each with what it names (res://path:line where it says)."""
    errors = []
    for line in text.splitlines():
        if re.search(r"\b(ERROR|Parse Error)\b", line):
            errors.append(line.strip()[:400])
    return errors


def checked(text):
    """Godot's own errors for each resource the check loaded, by res:// path; a resource loaded cleanly has none."""
    by_path, current = {}, None
    for line in text.splitlines():
        if line.startswith("CHECK "):
            current = line[6:].strip()
            by_path.setdefault(current, [])
        elif line.startswith("CHECKED "):
            current = None
        elif current and re.search(r"\b(ERROR|FAILED|NOT MADE)\b", line):
            by_path[current].append(line.strip()[:300])
    return by_path


def main_script(project):
    """The main scene's root script and the line inside its ready function a breakpoint stops at; None where not found."""
    text = open(os.path.join(project, "project.godot"), errors="replace").read()
    named = re.search(r'^run/main_scene="([^"]+)"', text, re.M)
    if not named:
        return None
    scene = named.group(1)
    if scene.startswith("uid://"):
        scene = next((f for f in files_of(project) if f.endswith(".tscn") and f'uid="{scene}"' in open(os.path.join(project, f), errors="replace").readline()), None)
        if scene is None:
            return None
    else:
        scene = scene.removeprefix("res://")
    path = os.path.join(project, scene)
    if not os.path.exists(path):
        return None
    lines = open(path, errors="replace").read().split("\n")
    resources = {}
    root_script = None
    in_root = False
    for line in lines:
        if line.startswith("[ext_resource"):
            id_ = re.search(r'\bid="([^"]+)"', line)
            res = re.search(r'\bpath="res://([^"]+)"', line)
            if id_ and res:
                resources[id_.group(1)] = res.group(1)
        elif line.startswith("[node"):
            if in_root:
                break
            in_root = "parent=" not in line
        elif in_root and line.startswith("script = ExtResource("):
            root_script = resources.get(re.search(r'ExtResource\("([^"]+)"\)', line).group(1))
    if not root_script or not os.path.exists(os.path.join(project, root_script)):
        return None
    code = open(os.path.join(project, root_script), errors="replace").read().split("\n")
    ready = re.compile(r"^\s*func _ready\(" if root_script.endswith(".gd") else r"^\s*public override void _Ready\(")
    for number, line in enumerate(code, 1):
        if ready.search(line):
            for after in range(number, min(number + 6, len(code))):
                body = code[after].strip()
                if body and body not in ("{", "}") and not body.startswith(("#", "//")):
                    return root_script, after + 1
    return root_script, None


def sweep_project(entry, clone_folder, relative, run_folder, godot, environment, skip_run):
    project = os.path.normpath(os.path.join(clone_folder, relative))
    name = entry["name"] if relative == "." else f"{entry['name']}/{relative}"
    slug = re.sub(r"[^A-Za-z0-9_.-]", "_", name)
    logs = os.path.join(run_folder, "logs")
    result = {"name": name, "folder": project, "times": {}, "steps": []}
    log(f"{name}: importing")
    code, out, seconds = run([godot, "--headless", "--path", project, "--import"], env=environment["godot"], limit=IMPORT_LIMIT,
                             log_to=os.path.join(logs, f"{slug}-import.log"))
    result["times"]["import"] = seconds
    result["import"] = {"code": code, "errors": godot_errors(out)}
    before = snapshot(project)

    passenger = Passenger(project, environment["passenger"])
    passenger.fresh()
    began = time.monotonic()
    step = passenger.call("workspace", {})
    result["times"]["first answer"] = round(time.monotonic() - began, 1)
    result["workspace"] = step["answer"][:4000]
    files = files_of(project)
    marks, failed = [], []
    log(f"{name}: diagnostics of {len(files)} files")
    diagnostics_began = time.monotonic()
    by_kind = {}
    for done, path in enumerate(files):
        if done and done % 50 == 0:
            log(f"{name}: {done} of {len(files)} files read, {round(time.monotonic() - diagnostics_began)} s")
        step = passenger.call("diagnostics", {"path": path})
        kind = "project.godot" if os.path.basename(path) == "project.godot" else os.path.splitext(path)[1]
        by_kind.setdefault(kind, []).append(step["seconds"])
        try:
            answer = json.loads(step["answer"]) if step["answer"] else []
        except ValueError:
            answer = None
        if not isinstance(answer, list):
            failed.append({"path": path, "answer": step["answer"][:600]})
            continue
        marks.extend(answer)
    result["times"]["diagnostics"] = round(time.monotonic() - diagnostics_began, 1)
    result["times"]["diagnostics by kind"] = {k: {"files": len(v), "mean": round(sum(v) / len(v), 2), "max": max(v)} for k, v in by_kind.items()}
    result["marks"] = marks
    result["diagnostics failed"] = failed

    if not skip_run:
        log(f"{name}: run")
        for tool, arguments in (("run", {"waitSeconds": 90}), ("app_tree", {"depth": 1}), ("stop", {})):
            result["steps"].append(passenger.call(tool, arguments))
        stop = main_script(project)
        result["breakpoint"] = stop
        log(f"{name}: debug{f' at {stop[0]}:{stop[1]}' if stop and stop[1] else ''}")
        if stop and stop[1]:
            result["steps"].append(passenger.call("breakpoint_set", {"path": stop[0], "line": stop[1]}))
        result["steps"].append(passenger.call("debug_start", {"waitSeconds": 90}))
        result["steps"].append(passenger.call("stop", {}))
        if stop and stop[1]:
            result["steps"].append(passenger.call("breakpoint_set", {"path": stop[0], "line": stop[1], "remove": True}))
    passenger.stop()
    result["times"]["passenger"] = round(time.monotonic() - began, 1)
    result["files changed"] = changed(before, snapshot(project))

    log(f"{name}: Godot's own check")
    check = os.path.join(run_folder, "check_res.gd")
    code, out, seconds = run([godot, "--headless", "--path", project, "--script", check], env=environment["godot"], limit=CHECK_LIMIT,
                             log_to=os.path.join(logs, f"{slug}-check.log"))
    result["times"]["godot check"] = seconds
    result["godot check"] = {"code": code, "errors": {p: e for p, e in checked(out).items() if e}, "loaded": len(checked(out))}
    return result


def parse_check(clone_folder, run_folder, slug):
    """Passenger's parser over every .gd of a clone: each file's errors, and its speed on the largest."""
    if not os.access(PARSER, os.X_OK):
        return {"skipped": f"no parser tool at {PARSER}: run just build"}
    out_path = os.path.join(run_folder, "logs", f"{slug}-parse.json")
    env = {**os.environ, "DOTNET_GCHeapHardLimit": "0x80000000"}
    code, out, seconds = run([PARSER, clone_folder], env=env, limit=CHECK_LIMIT, log_to=out_path)
    try:
        return {**json.loads(out), "seconds": seconds}
    except ValueError:
        return {"failed": out[-2000:], "code": code}


def unexplained_parse_errors(parsed, results):
    """The parser's errors on a line where Godot's own server marks no error, for the files a project's sweep read."""
    godot_lines = {}
    for result in results:
        for mark in result.get("marks", []):
            if mark.get("severity") == "error" and mark["path"].endswith(".gd"):
                full = os.path.normpath(os.path.join(result["folder"], mark["path"]))
                godot_lines.setdefault(full, set()).add(mark.get("line"))
    swept = {os.path.normpath(r["folder"]) for r in results}
    found = []
    for file in parsed.get("files", []):
        full = os.path.normpath(file["path"])
        read = any(full.startswith(s + os.sep) for s in swept)
        for error in file.get("errors", []):
            if error["line"] not in godot_lines.get(full, ()):
                found.append({"path": full, "line": error["line"], "column": error["column"], "message": error["message"],
                              "godot read it": read})
    return found


def flagged(result):
    """The error and warning marks to review, each with whose it is and whether Godot's own load of the file gave an error:
    "passenger" for Passenger's own checks (every mark in a scene, resource or project.godot, and a C# file's mark with
    no compiler's code), "compiler" for a C# compiler error, "godot" for Godot's server's errors on a GDScript (its
    warnings, named by their code, are Godot's own and left out); Roslyn's style marks are left out."""
    check = result.get("godot check", {}).get("errors", {})
    rows = []
    for mark in result.get("marks", []):
        if mark.get("severity") not in ("error", "warning"):
            continue
        code = mark.get("code") or ""
        path = mark["path"]
        if path.endswith((".tscn", ".tres", ".godot", ".gdshader")) or path.endswith(".cs") and not code:
            whose = "passenger"
        elif path.endswith(".cs") and code.startswith("CS") and mark["severity"] == "error":
            whose = "compiler"
        elif path.endswith(".gd") and mark["severity"] == "error":
            whose = "godot"
        else:
            continue
        rows.append({**mark, "whose": whose, "godot": "errors there too" if check.get("res://" + path) else "none"})
    return rows


def report(run_folder, godot_version, results, parsed, unexplained):
    lines = [f"# Godot sweep, {time.strftime('%Y-%m-%d %H:%M')}, {godot_version}", ""]
    lines.append("| Project | Files | Marks (error/warning) | Passenger's own | Files changed | Run | Debug | Import s | Diagnostics s | Passenger s |")
    lines.append("|---|---|---|---|---|---|---|---|---|---|")
    for r in results:
        marks = r.get("marks", [])
        errors = sum(1 for m in marks if m.get("severity") == "error")
        warnings = sum(1 for m in marks if m.get("severity") == "warning")
        steps = {s["tool"]: s for s in r.get("steps", [])}
        run_said = steps.get("run", {}).get("answer", "-")[:60].replace("\n", " ").replace("|", "/")
        debug_said = steps.get("debug_start", {}).get("answer", "-")[:60].replace("\n", " ").replace("|", "/")
        files = sum(v["files"] for v in r["times"].get("diagnostics by kind", {}).values())
        lines.append(f"| {r['name']} | {files} | {errors}/{warnings} | {sum(1 for m in flagged(r) if m['whose'] == 'passenger')} | {len(r.get('files changed', []))} | {run_said} | {debug_said} "
                     f"| {r['times'].get('import')} | {r['times'].get('diagnostics')} | {r['times'].get('passenger')} |")
    for r in results:
        lines += ["", f"## {r['name']}", "", f"Times: {json.dumps(r['times'])}", ""]
        if r["import"]["errors"]:
            lines += ["Godot's import said:", ""] + [f"    {e}" for e in r["import"]["errors"][:20]] + [""]
        if r.get("diagnostics failed"):
            lines += ["Diagnostics that failed:", ""] + [f"- {f['path']}: {f['answer'][:200]}" for f in r["diagnostics failed"]] + [""]
        rows = flagged(r)
        for whose, title in (("passenger", "Passenger's own marks, which Godot does not give"), ("compiler", "C# compiler errors"),
                             ("godot", "Godot's server's errors on its scripts")):
            these = [m for m in rows if m["whose"] == whose]
            if these:
                lines += [f"{title} ({len(these)}; Godot's own load of the file in brackets):", ""]
                lines += [f"- {m['path']}:{m.get('line')}:{m.get('column')} {m['severity']} {m.get('code') or ''} {m['message']} (Godot's load: {m['godot']})" for m in these]
                lines.append("")
        if r.get("files changed"):
            lines += ["Files changed while Passenger had the folder open:", ""] + [f"- {c}" for c in r["files changed"]] + [""]
        for s in r.get("steps", []):
            lines += [f"- {s['tool']} {json.dumps(s['arguments'])} ({s['seconds']} s, exit {s['code']}): {s['answer'][:500].replace(chr(10), ' / ')}"]
        if r["godot check"]["errors"]:
            lines += ["", f"Godot's own load ({r['godot check']['loaded']} resources) gave errors for {len(r['godot check']['errors'])}:", ""]
            lines += [f"- {p}: {e[0]}" for p, e in list(r["godot check"]["errors"].items())[:30]]
    lines += ["", "## Passenger's GDScript parser", ""]
    for name, p in parsed.items():
        if "files" in p:
            errors = sum(len(f.get("errors", [])) for f in p["files"])
            largest = p.get("largest") or {}
            lines.append(f"- {name}: {len(p['files'])} scripts, {errors} errors, {len(p.get('editsMismatched', []))} edits read otherwise than a whole parse, "
                         f"{p.get('totalMilliseconds')} ms in all" + (f"; the largest, {largest['path']}: {json.dumps({k: v for k, v in largest.items() if k != 'path'})}" if largest else ""))
        else:
            lines.append(f"- {name}: {p.get('skipped') or p.get('failed', '')[:300]}")
    if unexplained:
        lines += ["", "Errors Godot's server does not give on the line:", ""]
        lines += [f"- {u['path']}:{u['line']}:{u['column']} {u['message']}{'' if u['godot read it'] else ' (a script no swept project opened)'}" for u in unexplained]
    else:
        lines += ["", "Every parse error the parser gives, Godot's server gives on the line too."]
    path = os.path.join(run_folder, "report.md")
    with open(path, "w") as written:
        written.write("\n".join(lines) + "\n")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--godot", help="the Godot executable (or .app) to sweep with; else GODOT's, else /Applications/Godot_mono.app")
    parser.add_argument("--folder", default=os.path.join(tempfile.gettempdir(), "passenger-godot-sweep.noindex"),
                        help="where the clones and runs go, outside any repository (MSBuild takes in a Directory.Build.props above a project)")
    parser.add_argument("--skip-run", action="store_true", help="read diagnostics only, running and debugging nothing")
    parser.add_argument("--parse-only", action="store_true", help="only parse every .gd of the clones")
    parser.add_argument("--report", metavar="RUN", help="write a finished run's report again from its results.json")
    parser.add_argument("names", nargs="*", help="the sets to sweep, by name (all by default)")
    given = parser.parse_args()
    if given.report:
        done = json.load(open(os.path.join(given.report, "results.json")))
        log(f"report: {report(given.report, done['godot'], done['projects'], done['parsed'], done['unexplained parse errors'])}")
        return 0
    if subprocess.run(["git", "-C", os.path.dirname(os.path.abspath(given.folder)) or "/", "rev-parse"], capture_output=True).returncode == 0:
        sys.exit(f"godot-sweep: {given.folder} is inside a git repository, whose MSBuild files a C# project there would take in; choose a folder outside one")
    sets = json.load(open(SETS))["sets"]
    unknown = [n for n in given.names if n not in {s["name"] for s in sets}]
    if unknown:
        sys.exit(f"godot-sweep: no set named {', '.join(unknown)} in {SETS}")
    chosen = [s for s in sets if not given.names or s["name"] in given.names]
    godot = godot_executable(given.godot)
    version = subprocess.run([godot, "--version"], capture_output=True, text=True).stdout.strip().splitlines()[-1]
    run_folder = os.path.join(os.path.abspath(given.folder), "runs", f"{time.strftime('%Y%m%d-%H%M%S')}-{re.sub(r'[^A-Za-z0-9.]', '_', version)}")
    os.makedirs(os.path.join(run_folder, "logs"), exist_ok=True)
    with open(os.path.join(run_folder, "check_res.gd"), "w") as written:
        written.write(CHECK_SCRIPT)
    godot_env = {**os.environ, **homes(os.path.join(run_folder, "home", "godot")), "SDL_JOYSTICK_HIDAPI": "0"}
    passenger_home = os.path.join(run_folder, "home", "passenger")
    os.makedirs(passenger_home, exist_ok=True)
    passenger_env = {**os.environ, "GODOT": godot, "PASSENGER_GODOT_HOME": passenger_home}
    log(f"sweeping {len(chosen)} sets with {version} into {run_folder}")
    results, parsed = [], {}
    for entry in chosen:
        folder, lfs = clone(entry, os.path.join(os.path.abspath(given.folder), "clones"))
        if lfs == "missing":
            log(f"{entry['name']}: its LFS files are not fetched (no git lfs), so Godot imports their pointers")
        if not given.parse_only:
            for relative in entry["projects"]:
                results.append(sweep_project(entry, folder, relative, run_folder, godot, {"godot": godot_env, "passenger": passenger_env}, given.skip_run))
                with open(os.path.join(run_folder, "results.json"), "w") as written:
                    json.dump(results, written, indent=1)
        parsed[entry["name"]] = parse_check(folder, run_folder, entry["name"])
    unexplained = unexplained_parse_errors({"files": [f for p in parsed.values() for f in p.get("files", [])]}, results)
    with open(os.path.join(run_folder, "results.json"), "w") as written:
        json.dump({"godot": version, "projects": results, "parsed": parsed, "unexplained parse errors": unexplained}, written, indent=1)
    log(f"report: {report(run_folder, version, results, parsed, unexplained)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
