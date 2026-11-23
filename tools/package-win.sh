#!/bin/sh
# Passenger's Windows installer and portable build: `just package-win [win-x64|win-arm64]`, on Windows (Git's sh) or
# macOS, makes artifacts/package/Passenger-windows-x64.exe and -x64-portable.zip (or -arm64), names each release keeps:
# the Release publish packed by Velopack's vpk into a Setup.exe that installs for the person alone, with no
# administrator, into %LocalAppData%\PassengerIDE, adds Start menu and Settings > Apps entries, and runs Passenger.exe's
# install hook, which puts current\bin, the passenger-ide command's folder, on the person's own Path; and the app as
# vpk's full package carries it, in a Passenger folder with nothing of Velopack's, which runs wherever it is unpacked.
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

rm -rf "$work" "$installer" "$portable"
publish_app "$root" "$rid" "$version" -o "$publish" -nologo -v quiet

# The passenger-ide command alone in bin\, so the folder on the person's Path offers no other program.
[ "$(ls "$publish/bin")" = passenger-ide.exe ] || fail "the publish's bin folder holds $(ls "$publish/bin" | tr '\n' ' '), not the passenger-ide command alone"
for part in passenger-ide.dll passenger-ide.deps.json passenger-ide.runtimeconfig.json; do
    [ -f "$publish/$part" ] || fail "the publish has no $part, which bin/passenger-ide.exe runs"
done

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

signed=$( [ -n "$signtool_params$signing_metadata" ] && echo "signed" || echo "unsigned: SmartScreen asks once, More info > Run anyway")
echo "package: $installer ($signed)"
echo "package: $portable (the same app, its portable build, $signed)"
