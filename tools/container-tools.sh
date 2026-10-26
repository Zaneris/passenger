#!/bin/sh
# Passenger's container tools for one Linux system (src/Passenger.ContainerTools), self-contained, into
# artifacts/container-tools/<rid>/, where a dev build of the IDE finds them; --archive also packs the release's
# artifacts/container-tools/Passenger-container-tools-<rid>.tar.gz (the folder's files at its root). With no RID, the
# glibc one for the container engine's processor, else this machine's.
#   tools/container-tools.sh [linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64] [--archive]
set -eu
cd "$(dirname "$0")/.."

rid=
archive=no
for arg in "$@"; do
    case $arg in
        --archive) archive=yes ;;
        linux-x64 | linux-arm64 | linux-musl-x64 | linux-musl-arm64)
            if [ -n "$rid" ]; then
                echo "One system at a time, not $rid and $arg" >&2
                exit 2
            fi
            rid=$arg
            ;;
        *)
            echo "Unknown argument '$arg': a RID (linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64) or --archive" >&2
            exit 2
            ;;
    esac
done

if [ -z "$rid" ]; then
    processor=$(docker version --format '{{.Server.Arch}}' 2>/dev/null || uname -m)
    case $processor in
        amd64 | x86_64) rid=linux-x64 ;;
        arm64 | aarch64) rid=linux-arm64 ;;
        *)
            echo "No container tools are made for a $processor engine" >&2
            exit 1
            ;;
    esac
fi

# A vX.Y.Z tag on this commit, else Directory.Build.props' Version: the IDE takes tools of its own version alone.
version=$(git describe --tags --exact-match --match 'v[0-9]*' 2>/dev/null | sed 's/^v//' || true)
[ -n "$version" ] || version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)

out=artifacts/container-tools/$rid
rm -rf "$out"
# An artifacts root of its own: a RID's publish restores what it references for that RID, which a build restores back.
AVALONIA_TELEMETRY_OPTOUT=1 dotnet publish src/Passenger.ContainerTools -c Release -r "$rid" --self-contained -p:Version="$version" \
    --artifacts-path artifacts/container-tools/build -o "$out" -nologo -v:q
test -x "$out/passenger-tools"
# Its licence and notices, which must name the runtime and the dbgshim it carries for its system (musl's on a musl one).
test -s "$out/LICENSE.txt"
for package in Microsoft.NETCore.App.Runtime.$rid Microsoft.Diagnostics.DbgShim.$rid; do
    if ! grep -q "^$package " "$out/THIRD-PARTY-NOTICES.txt"; then
        echo "$out/THIRD-PARTY-NOTICES.txt does not name $package, which the tools carry" >&2
        exit 1
    fi
done
echo "$out: $(du -sh "$out" | cut -f1)"

if [ "$archive" = yes ]; then
    tarball=artifacts/container-tools/Passenger-container-tools-$rid.tar.gz
    # Owned by root, as what docker cp puts in a container is (GNU tar names it --owner, bsdtar --uid).
    tar -czf "$tarball" -C "$out" --owner=0 --group=0 . 2>/dev/null || tar -czf "$tarball" -C "$out" --uid 0 --gid 0 .
    echo "$tarball: $(du -h "$tarball" | cut -f1)"
fi
