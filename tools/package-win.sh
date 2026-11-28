#!/bin/sh
# Passenger's Windows installer and portable build: `just package-win [win-x64|win-arm64]`, on Windows (Git's sh) or
# macOS, makes artifacts/package/Passenger-windows-x64.exe and -x64-portable.zip (or -arm64), with Passenger-duckdb-win-x64.zip
# (DuckDB's engine) and for win-x64 Passenger-debugger-win-x86.zip, which neither carries, names each release keeps: the
# Release publish packed by Velopack's vpk into a Setup.exe that installs for the person alone, with no administrator,
# into %LocalAppData%\PassengerIDE, adds Start menu and Settings > Apps entries, and runs its install hook, which puts
# current\bin on the person's own Path; and the app as vpk's full package carries it, in a Passenger folder, portable.
# Velopack's release (the full package and its channel's feed) stays in artifacts/package/<rid>/velopack. Signed only on
# Windows, by signtool, when one is set: PASSENGER_SIGNTOOL_PARAMS, signtool sign's parameters (/f or /sha1, /fd
# sha256, /tr and /td sha256), or PASSENGER_ARTIFACT_SIGNING_METADATA, an Azure Artifact Signing account's
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
root=$(checkout_root)
if command -v cygpath >/dev/null 2>&1; then
    root=$(cygpath -m "$root")
fi
out="$root/artifacts/package"
work="$out/$rid"
publish="$work/publish"
release="$work/velopack"
installer="$out/Passenger-windows-${rid#win-}.exe"
portable="$out/Passenger-windows-${rid#win-}-portable.zip"
debugger_x86="$out/Passenger-debugger-win-x86.zip"
duckdb="$out/Passenger-duckdb-$rid.zip"
# bsdtar, which writes and reads a zip: Windows' own, as Git's tar is GNU tar.
bsdtar=tar
if [ -n "${SYSTEMROOT:-}" ]; then
    bsdtar="$(cygpath -u "$SYSTEMROOT")/System32/tar.exe"
fi

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

rm -rf "$work" "$installer" "$portable" "$duckdb"
[ "$rid" != win-x64 ] || rm -f "$debugger_x86"
publish_app "$root" "$rid" "$version" -o "$publish" -nologo -v quiet

# The debugger's 32-bit host (debugger\x86) is no part of a release's app, but a release file of its own made below.
[ -f "$publish/debugger/x86/passenger-helper.exe" ] && [ -f "$publish/debugger/x86/dbgshim.dll" ] \
    || fail "the publish has no debugger/x86 holding passenger-helper.exe and dbgshim.dll, the debugger's 32-bit host"
mv "$publish/debugger/x86" "$work/debugger-x86"
# DuckDB's engine is a release file of its own too, downloaded the first time a DuckDB connection opens.
duckdb_engine=$(duckdb_engine_of "$rid")
take_duckdb "$publish" "$duckdb_engine" "$work/duckdb"

# The passenger-ide command alone in bin\, so the folder on the person's Path offers no other program.
[ "$(ls "$publish/bin")" = passenger-ide.exe ] || fail "the publish's bin folder holds $(ls "$publish/bin" | tr '\n' ' '), not the passenger-ide command alone"
for part in passenger-ide.dll passenger-ide.deps.json passenger-ide.runtimeconfig.json; do
    [ -f "$publish/$part" ] || fail "the publish has no $part, which bin/passenger-ide.exe runs"
done

# vpk's signing, wherever one is set, after the rest of a pack's arguments.
signed_pack() {
    if [ -n "$signtool_params" ]; then
        set -- "$@" --signParams "$signtool_params"
    elif [ -n "$signing_metadata" ]; then
        set -- "$@" --azureTrustedSignFile "$signing_metadata"
    fi
    dotnet vpk '[win]' pack --skip-updates "$@"
}

cd "$root"
dotnet tool restore
# Not Passenger as pack id (its uninstaller empties %LocalAppData%\Passenger, the person's state); --aumid is the process's own, so a pinned button shows the jump list.
signed_pack --packId PassengerIDE --packVersion "$version" --packTitle Passenger --packAuthors Passenger \
    --packDir "$publish" --mainExe Passenger.exe --icon "$root/src/Passenger.App/Assets/Passenger.ico" \
    --runtime "$rid" --channel "$rid" --shortcuts StartMenuRoot --aumid velopack.PassengerIDE --noPortable \
    --exclude 'lib(SkiaSharp|HarfBuzzSharp)\.pdb$' --outputDir "$release"

setup=$(find "$release" -maxdepth 1 -name '*-Setup.exe')
[ "$(echo "$setup" | grep -c .)" = 1 ] || fail "vpk was to write one Setup.exe in $release, and wrote: ${setup:-none}"
cp "$setup" "$installer"

# The full package's app, signed wherever the installer is: the publish but for what vpk adds and leaves out (createdump).
full="$release/PassengerIDE-$version-$rid-full.nupkg"
[ -f "$full" ] || fail "vpk was to write the full package $full"
mkdir -p "$work/full" "$work/portable"
"$bsdtar" -xf "$full" -C "$work/full"
app="$work/full/lib/app"
rm -f "$app/sq.version" "$app/Squirrel.exe" "$app"/*_ExecutionStub.exe
(cd "$publish" && find . -type f | grep -Ev '(^\./sq\.version|^\./Squirrel\.exe|_ExecutionStub\.exe|^\./createdump\.exe|lib(SkiaSharp|HarfBuzzSharp)\.pdb)$' | LC_ALL=C sort) \
    > "$work/published.txt"
(cd "$app" && find . -type f | LC_ALL=C sort) > "$work/packed.txt"
if ! cmp -s "$work/published.txt" "$work/packed.txt"; then
    diff "$work/published.txt" "$work/packed.txt" | head -n 20 >&2 || true
    fail "the full package's app is not the publish file for file, once Velopack's own files are taken away"
fi
mv "$app" "$work/portable/Passenger"
"$bsdtar" -a -cf "$portable" -C "$work/portable" Passenger
others=$("$bsdtar" -tf "$portable" | grep -v '^Passenger/' || true)
[ -z "$others" ] || fail "the portable build holds more than its Passenger folder: $(echo "$others" | head -n 3)"
mkdir -p "$work/check"
"$bsdtar" -xf "$portable" -C "$work/check"
[ -f "$work/check/Passenger/Passenger.exe" ] && [ -f "$work/check/Passenger/bin/passenger-ide.exe" ] \
    || fail "the portable build has no Passenger.exe and bin/passenger-ide.exe"
if [ -n "$signtool_params$signing_metadata" ]; then
    status=$(powershell -NoProfile -Command "(Get-AuthenticodeSignature '$work/check/Passenger/Passenger.exe').Status" | tr -d '\r')
    [ "$status" = Valid ] || fail "the portable build's Passenger.exe is not signed ($status)"
fi
# An x64 build runs on either processor, an Arm one on Arm alone: the machine's, from the registry, whatever runs emulated.
case $(uname -s) in
    MINGW* | MSYS* | CYGWIN*)
        machine=$(reg query 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' /v PROCESSOR_ARCHITECTURE | tr -d '\r' \
            | awk '$1 == "PROCESSOR_ARCHITECTURE" { print $NF }')
        if [ "$rid" = win-x64 ] || [ "$machine" = ARM64 ]; then
            check_portable "$work/check/Passenger/Passenger.exe" "$work/check/Passenger"
        else
            echo "package-win: the portable build for $rid is not run on this $machine Windows" >&2
        fi
        ;;
esac
rm -rf "$work/full" "$work/check"

# The 32-bit host as a release file, signed as the app is: packed apart, as vpk signs only a copy of what it packs.
if [ "$rid" = win-x64 ]; then
    host="$work/debugger-x86"
    signed_pack --packId PassengerDebuggerX86 --packVersion "$version" --packTitle "Passenger 32-bit debugger" --packAuthors Passenger \
        --packDir "$host" --mainExe passenger-helper.exe --runtime win-x86 --channel win-x86 --noInst --skipVeloAppCheck \
        --outputDir "$work/debugger-x86-velopack"
    hosts=$(find "$work/debugger-x86-velopack" -maxdepth 1 -name '*-full.nupkg')
    [ "$(echo "$hosts" | grep -c .)" = 1 ] || fail "vpk was to write one full package of the debugger's 32-bit host, and wrote: ${hosts:-none}"
    mkdir -p "$work/debugger-x86-full"
    "$bsdtar" -xf "$hosts" -C "$work/debugger-x86-full"
    packed="$work/debugger-x86-full/lib/app"
    rm -f "$packed/sq.version" "$packed/Squirrel.exe" "$packed"/*_ExecutionStub.exe
    (cd "$host" && find . -type f | LC_ALL=C sort) > "$work/debugger-x86-published.txt"
    (cd "$packed" && find . -type f | LC_ALL=C sort) > "$work/debugger-x86-packed.txt"
    if ! cmp -s "$work/debugger-x86-published.txt" "$work/debugger-x86-packed.txt"; then
        diff "$work/debugger-x86-published.txt" "$work/debugger-x86-packed.txt" | head -n 20 >&2 || true
        fail "the debugger's 32-bit host as packed is not the publish's file for file, once Velopack's own files are taken away"
    fi
    (cd "$packed" && "$bsdtar" -a -cf "$debugger_x86" -- *)
    "$bsdtar" -tf "$debugger_x86" | grep -qx passenger-helper.exe || fail "$debugger_x86 holds no passenger-helper.exe at its root"
    if [ -n "$signtool_params$signing_metadata" ]; then
        status=$(powershell -NoProfile -Command "(Get-AuthenticodeSignature '$packed/passenger-helper.exe').Status" | tr -d '\r')
        [ "$status" = Valid ] || fail "the debugger's 32-bit host's passenger-helper.exe is not signed ($status)"
    fi
    rm -rf "$work/debugger-x86-full" "$work/debugger-x86-velopack"
fi

# DuckDB's engine as a release file, signed as the app is: packed apart, the passenger-ide command lent as its program.
engine="$work/duckdb"
cp "$publish/bin/passenger-ide.exe" "$engine/passenger-ide.exe"
signed_pack --packId PassengerDuckDb --packVersion "$version" --packTitle "DuckDB's engine for Passenger" --packAuthors Passenger \
    --packDir "$engine" --mainExe passenger-ide.exe --runtime "$rid" --channel "duckdb-$rid" --noInst --skipVeloAppCheck \
    --outputDir "$work/duckdb-velopack"
engines=$(find "$work/duckdb-velopack" -maxdepth 1 -name '*-full.nupkg')
[ "$(echo "$engines" | grep -c .)" = 1 ] || fail "vpk was to write one full package of DuckDB's engine, and wrote: ${engines:-none}"
mkdir -p "$work/duckdb-full"
"$bsdtar" -xf "$engines" -C "$work/duckdb-full"
[ -f "$work/duckdb-full/lib/app/$duckdb_engine" ] || fail "the package of DuckDB's engine holds no $duckdb_engine"
(cd "$work/duckdb-full/lib/app" && "$bsdtar" -a -cf "$duckdb" -- "$duckdb_engine")
[ "$("$bsdtar" -tf "$duckdb")" = "$duckdb_engine" ] || fail "$duckdb holds more than DuckDB's engine"
if [ -n "$signtool_params$signing_metadata" ]; then
    status=$(powershell -NoProfile -Command "(Get-AuthenticodeSignature '$work/duckdb-full/lib/app/$duckdb_engine').Status" | tr -d '\r')
    [ "$status" = Valid ] || fail "DuckDB's engine, $duckdb_engine, is not signed ($status)"
fi
rm -rf "$work/duckdb-full" "$work/duckdb-velopack"

signed=$( [ -n "$signtool_params$signing_metadata" ] && echo "signed" || echo "unsigned: SmartScreen asks once, More info > Run anyway")
echo "package: $installer ($signed)"
echo "package: $portable (the same app, its portable build, $signed)"
[ "$rid" != win-x64 ] || echo "package: $debugger_x86 (the debugger's 32-bit host, downloaded the first time a 32-bit program is debugged, $signed)"
echo "package: $duckdb (DuckDB's engine, downloaded the first time a DuckDB connection opens, $signed)"
