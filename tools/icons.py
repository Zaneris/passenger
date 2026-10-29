#!/usr/bin/env python3
"""Writes src/Passenger.App/Theme/Icons.axaml from Microsoft's Fluent UI System Icons (MIT), a few made of two of them,
and, where an icon is taken from Visual Studio Code, its Codicons (CC BY 4.0).

Each icon is fetched once into artifacts/icons/ and kept there; the lists below are the only places an icon is named.
"""
import os
import re
import sys
import urllib.parse
import urllib.request

# The repository's commit the icons come from, so every run draws the same ones.
COMMIT = "463d58b8186f874ab2f51cf0a08b4ff5097c912f"
BASE = f"https://raw.githubusercontent.com/microsoft/fluentui-system-icons/{COMMIT}/assets"
CODICONS_COMMIT = "5a6ad49557af022f97987cb3cfc3412b5d03f554"
CODICONS_BASE = f"https://raw.githubusercontent.com/microsoft/vscode-codicons/{CODICONS_COMMIT}/src/icons"

# Resource key, the icon's folder in the Fluent repo, and the file's size and style.
ICONS = [
    ("Explorer", "Folder", "20_regular"),
    ("FolderOpen", "Folder Open", "20_regular"),
    ("Structure", "Text Bullet List Tree", "20_regular"),
    ("Branch", "Branch", "20_regular"),
    ("Run", "Play", "20_filled"),
    ("Debug", "Bug", "20_regular"),
    ("Search", "Search", "20_regular"),
    ("Terminal", "Window Console", "20_regular"),
    ("Problems", "Warning", "20_regular"),
    ("Notifications", "Alert", "20_regular"),
    ("Settings", "Settings", "20_regular"),
    ("ChevronDown", "Chevron Down", "16_regular"),
    ("ChevronRight", "Chevron Right", "16_regular"),
    ("DocumentText", "Document Text", "20_regular"),
    ("Close", "Dismiss", "16_regular"),
    ("ArrowUp", "Arrow Up", "16_regular"),
    ("ArrowDown", "Arrow Down", "16_regular"),
    ("MatchCase", "Text Case Title", "16_regular"),
    ("WholeWord", "Text Whole Word", "20_regular"),
    ("Build", "Wrench", "20_regular"),
    ("RunOutline", "Play", "20_regular"),
    ("Stop", "Stop", "20_filled"),
    ("Rerun", "Arrow Clockwise", "20_regular"),
    ("Clear", "Delete", "20_regular"),
    ("Solution", "Grid", "20_regular"),
    # A solution folder is the solution's, in no folder on disk: the folder filled, where a disk folder's is an outline.
    ("SolutionFolder", "Folder", "20_filled"),
    ("SolutionFolderOpen", "Folder Open", "20_filled"),
    ("Package", "Box Multiple", "20_regular"),
    ("Reference", "Link", "20_regular"),
    ("Error", "Error Circle", "20_regular"),
    ("Info", "Info", "20_regular"),
    ("Resume", "Play", "20_filled"),
    ("Pause", "Pause", "20_regular"),
    ("StepOver", "Arrow Step Over", "20_regular"),
    ("StepInto", "Arrow Step In", "20_regular"),
    ("StepOut", "Arrow Step Out", "20_regular"),
    ("RunToCursor", "Arrow Step In Right", "20_regular"),
    # Detach, after Stop: the debugger unplugged, the program left running.
    ("Detach", "Plug Disconnected", "20_regular"),
    # Smart Step Into: a call of the line chosen to step into, a target.
    ("SmartStepInto", "Target Arrow", "20_regular"),
    ("Breakpoints", "Record", "20_regular"),
    ("MuteBreakpoints", "Circle Off", "20_regular"),
    ("Evaluate", "Calculator", "20_regular"),
    ("Add", "Add", "16_regular"),
    ("Copy", "Copy", "16_regular"),
    ("Variable", "Braces Variable", "20_regular"),
    ("More", "More Vertical", "16_regular"),
    ("Minimize", "Subtract", "16_regular"),
    ("Tests", "Beaker", "20_regular"),
    ("Commit", "Checkmark", "20_regular"),
    ("Check", "Checkmark", "16_regular"),
    ("Menu", "Line Horizontal 3", "20_regular"),
    ("Rollback", "Arrow Undo", "20_regular"),
    ("Push", "Arrow Upload", "20_regular"),
    ("Diff", "Split Vertical", "20_regular"),
    ("Profile", "Top Speed", "20_regular"),
    ("Bookmark", "Bookmark", "20_regular"),
    ("TestPassed", "Checkmark", "16_filled"),
    ("TestFailed", "Dismiss", "16_filled"),
    ("TestSkipped", "Subtract", "16_filled"),
    ("TestNotRun", "Circle", "16_regular"),
    ("TestRunning", "Circle", "16_filled"),
    # The Unit Tests window's toolbar: every outcome's filter, Expand All, Collapse All and Group By.
    ("TestAll", "Circle Half Fill", "16_regular"),
    ("ExpandAll", "Arrow Expand All", "20_regular"),
    ("CollapseAll", "Arrow Collapse All", "20_regular"),
    ("GroupBy", "Group List", "20_regular"),
    ("Pin", "Pin", "16_regular"),
    # The merge window's Apply All Non-Conflicting Changes (arrows meeting in the result) and Resolve Simple Conflicts.
    ("ApplyNonConflicts", "Arrow Minimize Vertical", "20_regular"),
    ("Wand", "Wand", "20_regular"),
    # Local History's tool window and its labels.
    ("History", "History", "20_regular"),
    ("Label", "Tag", "16_regular"),
    # A change bar's popup's Highlight Words: a highlighter pen.
    ("Highlight", "Highlight", "16_regular"),
    # Git log branches: a favourite's star (filled once marked), Update Selected, Navigate Log to Selected Branch Head.
    ("Star", "Star", "16_regular"),
    ("StarFilled", "Star", "16_filled"),
    ("Update", "Arrow Download", "16_regular"),
    ("Locate", "Target", "16_regular"),
    # The NuGet window's Sources tab: a feed's Edit, and the lock of one whose nuget.config holds its credentials.
    ("Edit", "Edit", "16_regular"),
    ("Lock", "Lock Closed", "16_regular"),
    # Settings' Keymap: Find Actions by Shortcut, a keyboard with a magnifier over it (Search drawn on it).
    ("Keyboard", "Keyboard", "20_regular"),
    # The Live Visual Tree: its window's tree of controls, and Select Element, a control picked in the app.
    ("LiveTree", "Organization", "20_regular"),
    ("SelectElement", "Select Object", "20_regular"),
    # A test frame's viewer: Zoom In, Zoom Out, Actual Size and Fit Zoom to Window.
    ("ZoomIn", "Zoom In", "20_regular"),
    ("ZoomOut", "Zoom Out", "20_regular"),
    ("ActualSize", "Ratio One To One", "20_regular"),
    ("ZoomFit", "Zoom Fit", "20_regular"),
    # The Run and Debug windows' Avalonia debug overlays: layers drawn over the app's window.
    ("Overlays", "Layer", "20_regular"),
    # Apply Hot Reload Changes on the toolbar and the Run and Debug windows: a flame.
    ("HotReload", "Fire", "20_regular"),
    # The Hierarchy window's stripe button: a type over the types it derives from or that derive from it.
    ("Hierarchy", "Diagram", "20_regular"),
    # The Endpoints window's stripe button: the requests of the folder's .http files, sent over the web.
    ("Endpoints", "Globe", "20_regular"),
    # The Containers window's stripe button: the engine's containers, images, volumes and networks.
    ("Containers", "Box Multiple", "20_regular"),
    # The Aspire window's stripe button: an AppHost over the resources it runs.
    ("Aspire", "Cube Tree", "20_regular"),
    # Attach to a service as it runs: the debugger plugged in, beside Detach's plug unplugged.
    ("Attach", "Plug Connected", "20_regular"),
    # The Migrations window's stripe button: a database, whose migrations it lists and applies.
    ("Database", "Database", "20_regular"),
    # A web project's web root (wwwroot) in the Explorer: the folder whose files are served on the web.
    ("WebRootFolder", "Folder Globe", "20_regular"),
]

# Resource key, then two icons (folder and size and style, as above) whose parts make it: the first's shape outside its
# badge (the corner right of and below its middle, where Fluent puts every badge), the second's badge inside it.
COMPOSED = [
    # A project's Properties in the Explorer: Folder Globe's folder, cut round its badge, with Calendar Settings' gear.
    ("PropertiesFolder", ("Folder Globe", "20_regular"), ("Calendar Settings", "20_regular")),
]

# Resource key and the Codicon's name.
CODICONS = [
    # A XAML or Markdown file's preview beside its editor, shown or hidden from the editor's tab strip.
    ("Preview", "open-preview"),
]


def fetch(folder, stem, cache):
    name = f"ic_fluent_{folder.lower().replace(' ', '_')}_{stem}.svg"
    return fetched(os.path.join(cache, name), f"{BASE}/{urllib.parse.quote(folder)}/SVG/{name}")


def fetched(path, url):
    if not os.path.exists(path):
        with urllib.request.urlopen(url) as response:
            data = response.read()
        with open(path, "wb") as out:
            out.write(data)
    with open(path, encoding="utf-8") as svg:
        return svg.read()


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    cache = os.path.join(root, "artifacts", "icons", COMMIT)
    codicons = os.path.join(root, "artifacts", "icons", "codicons-" + CODICONS_COMMIT)
    os.makedirs(cache, exist_ok=True)
    os.makedirs(codicons, exist_ok=True)
    lines = [
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "    <!-- Written by tools/icons.py from Fluent UI System Icons (MIT) and Codicons (CC BY 4.0), Microsoft's; see",
        "         THIRD_PARTY_NOTICES.md. F1 is the nonzero fill rule the SVGs are drawn for. -->",
    ]
    for key, folder, stem in ICONS:
        lines.append(geometry(key, f"{folder} {stem}", fetch(folder, stem, cache)))
    for key, name in CODICONS:
        lines.append(geometry(key, name, fetched(os.path.join(codicons, name + ".svg"), f"{CODICONS_BASE}/{name}.svg")))
    for key, (folder, stem), (badged, badge_stem) in COMPOSED:
        shape = [p for p in parts(f"{folder} {stem}", fetch(folder, stem, cache)) if not in_badge(p, stem)]
        badge = [p for p in parts(f"{badged} {badge_stem}", fetch(badged, badge_stem, cache)) if in_badge(p, badge_stem)]
        if not shape or not badge or stem != badge_stem:
            sys.exit(f"icons: {key} has no shape of {folder} or no badge of {badged} at its size")
        lines.append(f'    <StreamGeometry x:Key="Icon.{key}">F1 {"".join(shape + badge)}</StreamGeometry>')
    lines.append("</ResourceDictionary>")
    target = os.path.join(root, "src", "Passenger.App", "Theme", "Icons.axaml")
    with open(target, "w", encoding="utf-8") as out:
        out.write("\n".join(lines) + "\n")
    print(f"icons: {len(ICONS) + len(CODICONS) + len(COMPOSED)} written to {os.path.relpath(target, root)}")


def path_of(named, svg):
    paths = re.findall(r'<path[^>]*\sd="([^"]+)"', svg)
    if len(paths) != 1 or re.search(r"<(rect|circle|line|polygon|g)\b", svg):
        sys.exit(f"icons: {named} is not a single path")
    return paths[0]


def geometry(key, named, svg):
    return f'    <StreamGeometry x:Key="Icon.{key}">F1 {path_of(named, svg)}</StreamGeometry>'


def parts(named, svg):
    """An icon's path split where each of its figures begins, every command absolute, so a figure is placed by its start."""
    data = path_of(named, svg)
    if re.search(r"[a-df-z]", data):
        sys.exit(f"icons: {named} has relative commands, whose figures cannot be taken apart")
    return [part for part in re.split(r"(?=M)", data) if part]


def in_badge(part, stem):
    """Whether a figure begins in the badge's corner: right of and below the icon's middle."""
    x, y = (float(n) for n in re.findall(r"-?\d*\.?\d+", part)[:2])
    middle = int(stem.split("_")[0]) / 2
    return x >= middle and y >= middle


if __name__ == "__main__":
    main()
