# Passenger's recipes. `just --list` shows them all.

set windows-shell := ["powershell.exe", "-NoLogo", "-Command"]

app := "src/Passenger.App"

# Avalonia's build task sends usage data from every build of an Avalonia project unless this is in the environment.
export AVALONIA_TELEMETRY_OPTOUT := "1"

# The checkout as the disk names it, for recipes' shells and dotnet, which take a PWD naming their folder as its path.
export PWD := justfile_directory()

# Build everything (Debug). Warnings are errors.
build:
    dotnet build

# The suite dealt to several test processes at once, then the Alone classes in one (tools/test-run.py; results in
# artifacts/test-results/local). PROCESSES is PASSENGER_TEST_PROCESSES by default, else a quarter of the processors, at
# most 4, and 1 runs one dotnet test in xunit's own order; --plan shows the deal and runs nothing.
# Every test, headless: just test [FILTER] [PROCESSES] [--plan]
test filter="" processes="" *args:
    python3 -I tools/test-run.py {{ quote(filter) }} {{ if processes == "" { "" } else { "--processes " + processes } }} {{args}}

# Every comment held to its limit, a file's or type's 10 lines, a member's 5, a body's 1: just comments [PATH...]
comments *paths:
    python3 -I tools/check-comments.py {{paths}}

# Rewrite TestTimes.json, by which CI deals each system's tests to its shards, from CI runs' results: just test-times RUN...
test-times +runs:
    python3 -I tools/test-shards.py update {{runs}}

# Run the IDE (Debug, JIT): just run [--theme dark|light|system]
run *args:
    dotnet run --project {{app}} -- {{args}}

# --window-buttons draws in macOS's traffic lights, which the off-screen window lacks (Windows' and Linux's are the bar's).
# Draw the main window off-screen to a PNG: just screenshot out.png [--size 1600x1000] [--scale 2] [--theme light] [--window-buttons]
screenshot path *args:
    dotnet run --project {{app}} -- --screenshot {{path}} {{args}}

# Precompiled with ReadyToRun for startup; no Intel Mac build ships.
# A self-contained Release build for one platform: just publish osx-arm64 | win-x64 | win-arm64 | linux-x64 | linux-arm64
publish rid:
    dotnet publish {{app}} -c Release -r {{rid}} --self-contained -p:PublishReadyToRun=true

# Passenger's container tools (its debug adapter and process lister, run inside a Linux container) self-contained for one
# system into artifacts/container-tools/<rid>/, where a dev build finds them; --archive also packs the release's
# Passenger-container-tools-<rid>.tar.gz. No RID: the glibc one for the container engine's processor.
# just container-tools [linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64] [--archive]
[unix]
container-tools *args:
    tools/container-tools.sh {{args}}

# The container tools, by Git's sh: just container-tools [RID] [--archive]
[windows]
container-tools *args:
    $sh = (Get-Command sh -ErrorAction SilentlyContinue).Source; if (-not $sh) { $sh = Join-Path (Split-Path (Split-Path (Get-Command git).Source)) 'bin\sh.exe' }; & $sh tools/container-tools.sh {{args}}

# The app signed ad hoc, or with PASSENGER_SIGN_IDENTITY and then, with PASSENGER_NOTARY_PROFILE, the app and the image
# notarized and stapled, and checked mounted; --dry-run writes what those two would run instead.
# The macOS disk image, artifacts/package/Passenger-macos-arm64.dmg (Apple Silicon): just package-mac [osx-arm64] [--dry-run]
package-mac *args:
    tools/package-mac.sh {{args}}

# Passenger's Windows installer, packed by Velopack's vpk (dotnet-tools.json): it installs per user with no
# administrator, adds a Start menu entry, opens .sln, .slnx and .csproj in Passenger, and uninstalls from Settings > Apps.
# Signed on Windows by PASSENGER_SIGNTOOL_PARAMS or PASSENGER_ARTIFACT_SIGNING_METADATA (tools/package-win.sh).
# The Windows installer, artifacts/package/Passenger-windows-x64.exe or -arm64.exe: just package-win [win-x64|win-arm64]
[unix]
package-win rid="win-x64":
    tools/package-win.sh {{rid}}

# The Windows installer, by Git's sh: just package-win [win-x64|win-arm64]
[windows]
package-win rid="win-x64":
    $sh = (Get-Command sh -ErrorAction SilentlyContinue).Source; if (-not $sh) { $sh = Join-Path (Split-Path (Split-Path (Get-Command git).Source)) 'bin\sh.exe' }; & $sh tools/package-win.sh {{rid}}

# Passenger for Linux: one passenger/ folder (the app, its launcher, install.sh and the desktop entry with its icon) in
# artifacts/package/Passenger-linux-x64.tar.gz or -arm64.tar.gz, unpacked and checked.
# On Linux or macOS: just package-linux [linux-x64|linux-arm64]
[unix]
package-linux rid="linux-x64":
    tools/package-linux.sh {{rid}}

# Passenger's own icons (Passenger.icns, passenger.png, Passenger.ico) from Assets/passenger.svg; macOS, by sips.
app-icon:
    tools/app-icon.sh

# Rewrite the generated themes (Theme/Sorbet.axaml and the rest) from the palettes in tools/themes.py, contrast checked.
themes:
    python3 -I tools/themes.py

# Rewrite Theme/Icons.axaml from the list in tools/icons.py (fetches any icon not yet cached).
icons:
    python3 -I tools/icons.py

# Rewrite Theme/FileIconSet.axaml, the File Icons set's shapes, from the list in tools/file-icons.py.
file-icons:
    python3 -I tools/file-icons.py

# Rewrite the C# of Aspire's resource service protocol (src/Passenger.App/Aspire/Protocol) from its .proto, with protoc
# from a pinned Grpc.Tools fetched into artifacts/aspire-protocol (tools/aspire-protocol.py).
aspire-protocol:
    python3 -I tools/aspire-protocol.py

# Fail naming any package the solution restores, Roslyn's server carries or dotnet-tools.json pins that the notices miss.
notices:
    python3 -I tools/check-notices.py

# Rewrite the terminal's table of wide characters from the Unicode Character Database (tools/unicode-widths.py).
unicode-widths:
    python3 -I tools/unicode-widths.py

# Run from a clone of the last build, so rebuilding Passenger never changes the files under it (tools/mcp.sh).
# Passenger as an MCP server over a folder, its tools on standard input and output: just mcp [FOLDER]
mcp folder="":
    @tools/mcp.sh {{folder}}

# Its server is kept for the folder's next calls, so the language server loads once; it ends after 20 idle minutes or at
# just mcp-call FOLDER --stop, and just mcp-call FOLDER lists the tools.
# One MCP tool called over a folder from the shell (tools/mcp-call.py): just mcp-call FOLDER TOOL ['{"name": value}' | -]
[unix]
mcp-call folder tool="" arguments="":
    @python3 -I tools/mcp-call.py --from {{ quote(invocation_directory()) }} {{ quote(folder) }} {{ quote(tool) }} {{ quote(arguments) }}

# Retake the site's pictures, site/images/<scene>-<dark|light>.webp, after just build (macOS): just site-shots [SCENE...]
[unix]
site-shots *scenes:
    tools/site-shots/shots.sh {{scenes}}
