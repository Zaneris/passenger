#!/usr/bin/env python3
# The site's godot-game scene (just site-shots godot-game): a real Godot game drawn in Passenger's Game window beside
# its code, its members' values at their lines as it plays. macOS draws a game in the Game window only in a window on
# the screen, so this one is: for each theme Passenger opens on the game in a window of its own state's
# (PASSENGER_SETTINGS, the configuration drawing in the Game window) and runs it from its command line (--run), is
# brought in front, and its window is taken several times by screencapture once the game draws there; then Passenger
# ends, the game with it. Usage: game.py PASSENGER FOLDER STATE CONFIGURATION FILE CARET OUT THEME...
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time

passenger, folder, state, configuration, file, caret, out = sys.argv[1:8]
themes = sys.argv[8:]
here = os.path.dirname(os.path.abspath(__file__))
logs = os.path.join(state, "logs")
# How many pictures each theme's run takes, a second apart, from which the scene's is chosen.
FRAMES = 6
# What the IDE's log says once the game's layer is shown in the Game window (GamePane.Layer.cs).
DRAWN = "layer shared through context"


def fail(why):
    print(f"game: {why}", file=sys.stderr, flush=True)
    sys.exit(1)


def write(path, value):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(value, f, indent=2)


def window(pid, *arguments):
    done = subprocess.run(["swift", os.path.join(here, "window.swift"), arguments[0], str(pid), *arguments[1:]],
                          capture_output=True, text=True, timeout=120)
    return done.stdout.strip() if done.returncode == 0 else None


def logged(text):
    for name in os.listdir(logs) if os.path.isdir(logs) else []:
        path = os.path.join(logs, name)
        if os.path.isfile(path):
            with open(path, encoding="utf-8", errors="replace") as f:
                if text in f.read():
                    return True
    return False


# The folder's state as a person's window keeps it: Explorer closed, Game open beside the code, the game drawn in it.
workspace = os.path.join(state, "workspaces", f"{os.path.basename(folder)}-{hashlib.sha256(folder.encode()).hexdigest()[:12]}")
write(os.path.join(workspace, "layout.json"), {"version": 1, "windows": {
    "explorer": {"anchor": "LeftTop", "order": 0, "shown": False},
    "game": {"anchor": "RightTop", "order": 0, "shown": True},
    "liveTree": {"anchor": "RightBottom", "order": 0, "shown": False}}, "rightWidth": 700})
write(os.path.join(workspace, "workspace.json"), {"version": 1, "godotEditorOffered": True})
with open(os.path.join(here, "..", "..", "Directory.Build.props"), encoding="utf-8") as props:
    version = re.search(r"<Version>([^<]+)</Version>", props.read()).group(1)
write(os.path.join(state, "updates.json"), {"version": 1, "notesShown": version})
write(os.path.join(workspace, "runConfigurations.json"),
      {"version": 3, "configurations": [], "edits": {configuration: {"godotInPane": True}}, "selected": configuration})

for theme in themes:
    write(os.path.join(state, "settings.json"), {"version": 3, "theme": theme.capitalize(), "look": "Passenger"})
    shutil.rmtree(logs, ignore_errors=True)
    with open(os.path.join(state, f"passenger-{theme}.log"), "w", encoding="utf-8") as log:
        ide = subprocess.Popen([passenger, folder, "--file", file, "--caret", caret, "--run", configuration],
                               env=dict(os.environ, PASSENGER_SETTINGS=state), stdout=log, stderr=subprocess.STDOUT)
        try:
            shown = None
            for _ in range(240):
                if ide.poll() is not None:
                    fail(f"Passenger ended with exit code {ide.returncode} before the game drew (passenger-{theme}.log)")
                shown = shown or window(ide.pid, "id")
                if shown and logged(DRAWN):
                    break
                time.sleep(1)
            else:
                fail("the game drew nothing in the Game window within 4 minutes")
            window(ide.pid, "front")
            time.sleep(3)
            shown = window(ide.pid, "id") or shown
            for frame in range(FRAMES):
                picture = f"{out}-{theme}-{frame}.png"
                subprocess.run(["screencapture", "-x", "-o", "-l", shown, picture], check=True, timeout=60)
                print(f"game: {picture}", flush=True)
                time.sleep(1)
        finally:
            ide.send_signal(signal.SIGTERM)
            try:
                ide.wait(timeout=60)
            except subprocess.TimeoutExpired:
                ide.kill()
