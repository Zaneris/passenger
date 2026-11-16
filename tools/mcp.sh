#!/bin/sh
# Passenger as an MCP server over a folder (the current one by default), for an agent to start: `just mcp`.
# It runs from a clone of the last Debug build, so building Passenger while an agent uses it never changes a
# file under the running IDE. The clone is APFS's (or a reflink), so it costs no space, and it goes when the
# server does. Standard output is the protocol's.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
build="$root/artifacts/bin/Passenger.App/debug"
if [ ! -x "$build/Passenger" ]; then
    echo "passenger: no build at $build; run just build first" >&2
    exit 1
fi
# The app host finds .NET by DOTNET_ROOT, else the installer's folder: a .NET dotnet-install put in ~/.dotnet, which a
# client may start this without, is named where it holds the runtime the build takes and the installer's folder doesn't.
runtime=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$build/Passenger.runtimeconfig.json" | head -n 1)
if [ -z "${DOTNET_ROOT:-}" ] && [ -n "$runtime" ] && [ -d "$HOME/.dotnet/shared/Microsoft.NETCore.App/$runtime" ] \
    && [ ! -d "/usr/local/share/dotnet/shared/Microsoft.NETCore.App/$runtime" ]; then
    DOTNET_ROOT="$HOME/.dotnet"
    export DOTNET_ROOT
fi
copies="$root/artifacts/mcp"
mkdir -p "$copies"
# A copy named for a shell no longer running is left from a server that was killed, unless a Passenger still runs from it.
for old in "$copies"/*; do
    [ -d "$old" ] || continue
    kill -0 "$(basename "$old")" 2>/dev/null && continue
    pgrep -f "$old/Passenger" >/dev/null 2>&1 && continue
    rm -rf "$old"
done
copy="$copies/$$"
if [ "$(uname)" = Darwin ]; then
    cp -cR "$build" "$copy"
else
    cp -R --reflink=auto "$build" "$copy"
fi
# A signal to this shell is passed on to the server, and the copy goes only once the server has ended.
"$copy/Passenger" --mcp "${1:-$PWD}" <&0 &
server=$!
trap 'kill -TERM "$server" 2>/dev/null || true' INT TERM HUP
trap 'rm -rf "$copy"' EXIT
status=0
while kill -0 "$server" 2>/dev/null; do
    wait "$server" && status=0 || status=$?
done
exit "$status"
