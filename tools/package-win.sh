#!/bin/sh
# Passenger's Windows installer: `just package-win [win-x64|win-arm64]`, on Windows (through Git's sh) or macOS, makes
# artifacts/package/Passenger-windows-x64.exe (or -arm64), a name each release keeps: the Release publish packed by
# Velopack's vpk into a Setup.exe that installs for the person alone, with no administrator, into
# %LocalAppData%\PassengerIDE, adds a Start menu entry and an uninstall entry in Settings > Apps, and runs
# Passenger.exe's install hook. Velopack's release (the full package and its channel's feed) stays in
# artifacts/package/<rid>/velopack.
#
# Signed only on Windows, by signtool, when one is set: PASSENGER_SIGNTOOL_PARAMS, signtool sign's parameters (/f or
# /sha1, /fd sha256, /tr and /td sha256), or PASSENGER_ARTIFACT_SIGNING_METADATA, an Azure Artifact Signing account's
# metadata.json, its identity from AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_CLIENT_SECRET or an `az login`.
set -eu
. "$(dirname "$0")/release-common.sh"
rid=${1:-win-x64}
case $rid in
    win-x64 | win-arm64) ;;
    *) fail "'$rid' is not a Windows system Passenger ships for: win-x64 or win-arm64" ;;
esac
# Git's sh would rewrite signtool's /fd as one of its paths; every path here is made Windows' own by cygpath instead.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
root=$(cd "$(dirname "$0")/.." && pwd)
if command -v cygpath >/dev/null 2>&1; then
    root=$(cygpath -m "$root")
fi
out="$root/artifacts/package"
work="$out/$rid"
publish="$work/publish"
release="$work/velopack"
installer="$out/Passenger-windows-${rid#win-}.exe"

signtool_params=${PASSENGER_SIGNTOOL_PARAMS:-}
signing_metadata=${PASSENGER_ARTIFACT_SIGNING_METADATA:-}
if [ -n "$signtool_params" ] && [ -n "$signing_metadata" ]; then
    fail "PASSENGER_SIGNTOOL_PARAMS and PASSENGER_ARTIFACT_SIGNING_METADATA are both set; sign with one"
fi
if [ -n "$signtool_params$signing_metadata" ]; then
    case $(uname -s) in
        MINGW* | MSYS* | CYGWIN*) ;;
        *) fail "signing runs Windows' signtool, so a signed installer is built on Windows; unset the signing variables for an unsigned one here" ;;
    esac
fi
if [ -n "$signing_metadata" ]; then
    [ -f "$signing_metadata" ] || fail "PASSENGER_ARTIFACT_SIGNING_METADATA names '$signing_metadata', which is not a file"
    signing_metadata=$(cygpath -m "$signing_metadata")
fi

version=$(release_version "$root")

rm -rf "$work" "$installer"
publish_app "$root" "$rid" "$version" -o "$publish" -nologo -v quiet

# Not Passenger as pack id (its uninstaller empties %LocalAppData%\Passenger, the person's state); --aumid is the process's own, so a pinned button shows the jump list.
set -- --packId PassengerIDE --packVersion "$version" --packTitle Passenger --packAuthors Passenger \
    --packDir "$publish" --mainExe Passenger.exe --icon "$root/src/Passenger.App/Assets/Passenger.ico" \
    --runtime "$rid" --channel "$rid" --shortcuts StartMenuRoot --aumid velopack.PassengerIDE --noPortable \
    --exclude 'lib(SkiaSharp|HarfBuzzSharp)\.pdb$' --outputDir "$release"
if [ -n "$signtool_params" ]; then
    set -- "$@" --signParams "$signtool_params"
elif [ -n "$signing_metadata" ]; then
    set -- "$@" --azureTrustedSignFile "$signing_metadata"
fi
cd "$root"
dotnet tool restore
dotnet vpk '[win]' pack --skip-updates "$@"

setup=$(find "$release" -maxdepth 1 -name '*-Setup.exe')
[ "$(echo "$setup" | grep -c .)" = 1 ] || fail "vpk was to write one Setup.exe in $release, and wrote: ${setup:-none}"
cp "$setup" "$installer"
echo "package: $installer ($( [ -n "$signtool_params$signing_metadata" ] && echo "signed" || echo "unsigned: SmartScreen asks once, More info > Run anyway"))"
