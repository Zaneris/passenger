#!/usr/bin/env python3
"""Fails, naming each one, when a package Passenger restores or carries is missing from THIRD_PARTY_NOTICES.md.

The packages are every one the solution's projects use, transitive ones included (so the solution must be restored
first), every one Roslyn's language server carries under roslyn/, every one Coverlet carries under coverage/, every one the container tools carry on one of their
systems with its runtime pack, every npm package the web language servers' lock file and the tests' own pin, and every
tool dotnet-tools.json pins. A package is named in the notices by its id in backticks.
"""
import glob
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NOTICES = os.path.join(ROOT, "THIRD_PARTY_NOTICES.md")

# Restored with the packages that use them, but in no app Passenger is published as nor in the container tools: their
# natives are for the browser, 32-bit Arm Linux and 32-bit Windows. A system Passenger comes to ship for takes its own
# out of here and into the notices; one the container tools carry is checked whatever this says.
NOT_SHIPPED = {
    "HarfBuzzSharp.NativeAssets.WebAssembly",
    "SkiaSharp.NativeAssets.WebAssembly",
    "Microsoft.Diagnostics.DbgShim.linux-arm",
    "Microsoft.Diagnostics.DbgShim.linux-musl-arm",
    "Microsoft.Diagnostics.DbgShim.win-x86",
}

# The container tools' project and the systems they are published for.
CONTAINER_TOOLS = os.path.join("src", "Passenger.ContainerTools", "Passenger.ContainerTools.csproj")
CONTAINER_TOOLS_RIDS = ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"]


def dotnet(*args):
    result = subprocess.run(["dotnet", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        sys.exit(f"check-notices: dotnet {' '.join(args)} failed:\n{result.stdout}{result.stderr}")
    return result.stdout


def solution_packages():
    """Each package id the solution's projects use, with the projects that use it."""
    listed = json.loads(dotnet("list", "Passenger.slnx", "package", "--include-transitive", "--format", "json", "--no-restore"))
    used = {}
    for project in listed.get("projects", []):
        name = os.path.splitext(os.path.basename(project["path"]))[0]
        for framework in project.get("frameworks", []):
            for package in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                used.setdefault(package["id"], set()).add(name)
    if not used:
        sys.exit("check-notices: dotnet list package found no packages; restore the solution first")
    return used


def roslyn_packages():
    """Each package id the restored Roslyn language server's .deps.json files name."""
    properties = json.loads(dotnet("msbuild", "src/Passenger.Lsp/Passenger.Lsp.csproj", "-getProperty:RoslynServerRid",
                                   "-getProperty:RoslynServerVersion", "-getProperty:NuGetPackageRoot"))["Properties"]
    rid, version, packages = properties["RoslynServerRid"], properties["RoslynServerVersion"], properties["NuGetPackageRoot"]
    folder = os.path.join(packages, f"roslyn-language-server.{rid.lower()}", version.lower())
    deps = glob.glob(os.path.join(folder, "tools", "*", rid, "**", "*.deps.json"), recursive=True)
    if not deps:
        sys.exit(f"check-notices: no .deps.json under {folder}; restore the solution first")
    used = {}
    for path in deps:
        with open(path, encoding="utf-8-sig") as f:
            libraries = json.load(f).get("libraries", {})
        for name, library in libraries.items():
            if library.get("type") == "package":
                used.setdefault(name.split("/", 1)[0], set()).add("roslyn/")
    return used


def coverage_packages():
    """Coverlet's packages, carried under coverage/, and each package their .deps.json files name."""
    properties = json.loads(dotnet("msbuild", "src/Passenger.App/Passenger.App.csproj", "-getProperty:CoverletVersion",
                                   "-getProperty:NuGetPackageRoot"))["Properties"]
    version, packages = properties["CoverletVersion"], properties["NuGetPackageRoot"]
    used = {}
    # The builds Passenger.App.csproj carries (FindCoverlet): the collector's for .NET 8, the console's for .NET 10.
    for package, carried in (("coverlet.collector", ("build", "net8.0")), ("coverlet.console", ("tools", "net10.0", "any"))):
        folder = os.path.join(packages, package, version.lower(), *carried)
        deps = glob.glob(os.path.join(folder, "*.deps.json"))
        if not deps:
            sys.exit(f"check-notices: no .deps.json under {folder}; restore the solution first")
        used.setdefault(package, set()).add("coverage/")
        for path in deps:
            with open(path, encoding="utf-8-sig") as f:
                libraries = json.load(f).get("libraries", {})
            for name, library in libraries.items():
                if library.get("type") == "package":
                    used.setdefault(name.split("/", 1)[0], set()).add("coverage/")
    return used


def container_tools_packages():
    """Each package the container tools carry on one of their systems: those the tools' restore gives a file for that
    system (project.assets.json, its runtime, native and resource files; dbgshim's musl natives among them), and the
    runtime pack each self-contained publish carries."""
    properties = json.loads(dotnet("msbuild", CONTAINER_TOOLS, "-getProperty:ProjectAssetsFile", "-getProperty:TargetFramework"))["Properties"]
    assets = properties["ProjectAssetsFile"]
    if not os.path.exists(assets):
        sys.exit(f"check-notices: no {assets}; restore the solution first")
    with open(assets, encoding="utf-8") as f:
        targets = json.load(f).get("targets", {})
    used = {}
    for rid in CONTAINER_TOOLS_RIDS:
        target = targets.get(f"{properties['TargetFramework']}/{rid}")
        if target is None:
            sys.exit(f"check-notices: the container tools' restore has nothing for {rid}; restore the solution first")
        user = f"the container tools for {rid}"
        used.setdefault(f"Microsoft.NETCore.App.Runtime.{rid}", set()).add(user)
        for name, library in target.items():
            files = [file for kind in ("runtime", "native", "resource") for file in library.get(kind, {}) if not file.endswith("/_._")]
            if library.get("type") == "package" and files:
                used.setdefault(name.split("/", 1)[0], set()).add(user)
    return used


def web_packages():
    """Each npm package the web language servers' lock file pins, carried under web/, and each the tests' own lock file
    pins (their project's own ESLint and Prettier)."""
    used = {}
    for lock, user in [(("src", "Passenger.Lsp", "WebServers"), "web/"), (("tests", "Passenger.Tests", "NodePackages"), "the tests' node_modules")]:
        with open(os.path.join(ROOT, *lock, "package-lock.json"), encoding="utf-8") as f:
            packages = json.load(f).get("packages", {})
        for place, package in packages.items():
            if place and not package.get("dev") and not package.get("link"):
                used.setdefault(place.rsplit("node_modules/", 1)[-1], set()).add(user)
    return used


def tool_packages():
    """Each package id the tool manifest pins: what the build runs (vpk packs the Windows installer)."""
    with open(os.path.join(ROOT, "dotnet-tools.json"), encoding="utf-8") as f:
        tools = json.load(f).get("tools", {})
    return {name: {"dotnet-tools.json"} for name in tools}


def named_in_notices():
    with open(NOTICES, encoding="utf-8") as f:
        text = f.read()
    return {name.strip().lower() for name in re.findall(r"`([^`\n]+)`", text)}


def main():
    used = solution_packages()
    for package, users in [*roslyn_packages().items(), *coverage_packages().items(), *web_packages().items(), *tool_packages().items()]:
        used.setdefault(package, set()).update(users)
    carried = container_tools_packages()
    for package, users in carried.items():
        used.setdefault(package, set()).update(users)
    named = named_in_notices()
    skipped = {package.lower() for package in NOT_SHIPPED} - {package.lower() for package in carried}
    missing = sorted((package for package in used if package.lower() not in named and package.lower() not in skipped),
                     key=str.lower)
    if not missing:
        print(f"check-notices: THIRD_PARTY_NOTICES.md names all {len(used)} packages")
        return
    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    print(f"check-notices: THIRD_PARTY_NOTICES.md does not name {len(missing)} package(s) Passenger uses; add each with "
          "its licence and copyright as its package states them:")
    for package in missing:
        line = f"{package} (used by {', '.join(sorted(used[package]))})"
        print(f"  {line}")
        if annotate:
            print(f"::error file=THIRD_PARTY_NOTICES.md,title=Package missing from the notices::{line}")
    sys.exit(1)


if __name__ == "__main__":
    main()
