#!/bin/bash
# The iPhone simulator the iOS tests take, booted and shut down again before them, so the runner's first boot of it, and
# what its system goes on setting up after the boot, take minutes of no test's: the one the tests pick, an iPhone of
# those the iOS workload lists (ComputeAvailableDevices), one already up first, else the first; one already up is left
# as it is. Its boot is given at most BOOT seconds, a boot still under way then shut down as it stands, and a booted one
# stays up SETTLE seconds more. What fails only leaves the first boot to the first iOS test. test.yml has test-shards.py
# run it before a macOS mobile shard's classes apart; by hand on a Mac with the iOS workload:
#   tools/simulator-first-boot.sh [BOOT [SETTLE]]    (600 and 120)
set -u
boot=${1:-600}
settle=${2:-120}
root=$(cd "$(dirname "$0")/.." && pwd)
warn() {
    if [ "${GITHUB_ACTIONS:-}" = true ]; then
        echo "::warning title=iOS simulator::$*"
    else
        echo "warning: $*"
    fi
}
first="so the first iOS test's boot of it may be the runner's first"

# No MSBuild server or node left holding memory into the tests.
listed=$(DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDUSESERVER=0 dotnet msbuild "$root/tests/MauiGraphs/iOS" -nologo \
    -restore -nodeReuse:false -target:ComputeAvailableDevices -getItem:Devices -getProperty:_SdkDevPath) \
    || { echo "$listed"; warn "The iOS workload did not list its simulators, $first"; exit 1; }
# The developer folder, the simulator's UDID, whether it is up, and its description, a tab apart.
picked=$(python3 -I -c 'import json, sys
text = sys.stdin.read()
if "{" not in text:
    sys.exit(text)
answer = json.JSONDecoder().raw_decode(text, text.index("{"))[0]
up = lambda d: d.get("Status", "").lower() not in ("notrunning", "shutdown", "offline")
phones = sorted((d for d in answer["Items"].get("Devices", [])
                 if d.get("Type", "").lower() == "simulator" and d.get("Description", "").startswith("iPhone")),
                key=lambda d: not up(d))
if not phones:
    sys.exit("The iOS workload lists no iPhone simulator")
said = phones[0]["Description"]
said = next((said[:-len(state)] for state in (" (Shutdown)", " (Booted)") if said.endswith(state)), said)
print("\t".join((answer["Properties"]["_SdkDevPath"], phones[0]["Identity"], "up" if up(phones[0]) else "down", said)))' <<< "$listed") || { warn "No iPhone simulator was picked, $first"; exit 1; }
IFS=$'\t' read -r developer udid state description <<< "$picked"
simctl=$developer/usr/bin/simctl
echo "The iOS tests' simulator: $description ($udid)"
if [ "$state" = up ]; then
    echo "It is up already, so it is left as it is"
    exit 0
fi

began=$(date +%s)
booted=0
perl -e 'alarm shift; exec @ARGV' "$boot" "$simctl" bootstatus "$udid" -b || booted=$?
if [ "$booted" = 0 ]; then
    echo "Booted in $(( $(date +%s) - began )) s; up $settle s more as its system goes on starting ($(uptime))"
    sleep "$settle"
    echo "Settled ($(uptime))"
elif [ "$booted" = 142 ]; then
    warn "$description did not boot in $boot s and is shut down as it stands, $first"
else
    warn "$description did not boot (simctl's words above), $first"
fi
"$simctl" shutdown "$udid" 2>&1 || true
state=$("$simctl" list devices "$udid" 2>&1 | grep -F "$udid" || true)
echo "$state"
case "$state" in
    *"(Shutdown)"*) [ "$booted" = 0 ] ;;
    *) warn "$description was not shut down, so the iOS tests take it as they find it"; exit 1 ;;
esac
