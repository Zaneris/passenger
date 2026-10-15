#!/bin/sh
# Passenger's macOS disk image: `just package-mac [osx-arm64|osx-x64] [--dry-run]` makes
# artifacts/package/Passenger-macos-arm64.dmg (or -x64), the names a release keeps from one to the next (#56). The
# Release publish (self-contained, ReadyToRun, the debugger and Roslyn's server beside it) becomes Passenger.app, signed
# under the hardened runtime with the entitlements below: by PASSENGER_SIGN_IDENTITY (a Developer ID Application
# identity), else ad hoc, which Apple Silicon needs to run it at all and which a person opens once through System
# Settings > Privacy & Security > Open Anyway. With PASSENGER_NOTARY_PROFILE (a notarytool keychain profile) the app is
# notarized and stapled, then the image signed, notarized and stapled too. --dry-run writes the Developer ID's and the
# notary's commands to a file instead of running them, and signs ad hoc. The image is then mounted read-only and checked.
set -eu
fail() {
    echo "package-mac: $*" >&2
    exit 1
}
rid=osx-arm64
dry_run=
for arg in "$@"; do
    case $arg in
        osx-arm64 | osx-x64) rid=$arg ;;
        --dry-run) dry_run=1 ;;
        *) fail "'$arg' is neither osx-arm64, osx-x64 nor --dry-run" ;;
    esac
done
arch=${rid#osx-}
# lipo's name for the processor, which every Mach-O file in the app must be built for.
machine=arm64
[ "$arch" = arm64 ] || machine=x86_64
identity=${PASSENGER_SIGN_IDENTITY:-}
profile=${PASSENGER_NOTARY_PROFILE:-}
if [ -n "$profile" ] && [ -z "$identity" ]; then
    fail "PASSENGER_NOTARY_PROFILE needs PASSENGER_SIGN_IDENTITY: the notary takes only what a Developer ID signed"
fi
if [ -n "$dry_run" ] && [ -z "$identity" ]; then
    fail "--dry-run shows what a Developer ID would run: set PASSENGER_SIGN_IDENTITY (and PASSENGER_NOTARY_PROFILE)"
fi
if [ -n "$identity" ] && [ -z "$dry_run" ]; then
    security find-identity -v -p codesigning | grep -qF "$identity" || fail "no code signing identity '$identity' in the keychains"
fi

root=$(cd "$(dirname "$0")/.." && pwd)
out="$root/artifacts/package"
work="$out/$rid"
publish="$work/publish"
# The image's top folder: the app is built where it is packed.
stage="$work/image"
app="$stage/Passenger.app"
mount="$work/mount"
commands="$work/dry-run.txt"
image="$out/Passenger-macos-$arch.dmg"
# The version is a vX.Y.Z tag on this commit, else the Version every project carries (Directory.Build.props), and
# the assemblies are built with it too, so the app says the version its bundle does. The build number counts the
# commits, so each package's is higher than the last; a shallow clone would count only the ones it has.
version=$(git -C "$root" describe --tags --exact-match --match 'v[0-9]*' 2>/dev/null | sed 's/^v//' || true)
[ -n "$version" ] || version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n 1)
if ! echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    fail "the version '$version' is not X.Y.Z in numbers (a vX.Y.Z tag on the commit, or Version in Directory.Build.props)"
fi
if [ "$(git -C "$root" rev-parse --is-shallow-repository)" = true ]; then
    fail "a shallow clone cannot count its commits for the build number; fetch the whole history"
fi
build=$(git -C "$root" rev-list --count HEAD)

# The image is mounted under the work folder while it is made and checked, and never left mounted, a failure included.
mounted=
attach() {
    mkdir -p "$mount"
    hdiutil attach "$@" -nobrowse -noautoopen -mountpoint "$mount" -quiet
    mounted=1
}
detach() {
    # Spotlight or XProtect can hold a volume just made for a moment; the last tries force it, the very last saying why
    # and what holds it (-quiet closes stderr, which left a failure here with no word on CI).
    for try in 1 2 3 4 5; do
        force=
        if [ "$try" -gt 3 ]; then
            force=-force
        fi
        if hdiutil detach "$mount" $force -quiet; then
            mounted=
            return
        fi
        sleep "$try"
    done
    if ! hdiutil detach "$mount" -force; then
        lsof +D "$mount" >&2 || true
        fail "the image mounted at $mount could not be detached"
    fi
    mounted=
}
# hdiutil create and convert now and then fail on a CI runner ("Resource busy", the system's scanners on what was just
# written), and -quiet closes stderr: tried three times, a moment apart, the last one saying why. OUT is removed first.
retry_hdiutil() {
    out=$1
    shift
    for try in 1 2; do
        rm -f "$out"
        if hdiutil "$@" -quiet; then
            return
        fi
        echo "package-mac: hdiutil $1 failed (try $try), trying again" >&2
        sleep $((try * 5))
    done
    rm -f "$out"
    hdiutil "$@" || fail "hdiutil $1 failed three times"
}
trap '[ -z "$mounted" ] || hdiutil detach "$mount" -force -quiet' EXIT
trap 'exit 1' HUP INT TERM
if [ -d "$mount" ]; then
    hdiutil detach "$mount" -force -quiet 2>/dev/null || true
fi
rm -rf "$work" "$image"
mkdir -p "$work"

# Each RID owns even its portable build outputs, so the next image cannot reuse the other RID's intermediates.
# Only the packages the lock files pin, as CI restores; and no MSBuild node is left running once it is done.
dotnet publish "$root/src/Passenger.App" -c Release -r "$rid" --self-contained -p:PublishReadyToRun=true -p:Version="$version" \
    -p:RestoreLockedMode=true -p:ArtifactsPath="$work/build" -nodeReuse:false -o "$publish" -nologo -v quiet
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"
cp "$root/src/Passenger.App/Assets/Passenger.icns" "$app/Contents/Resources/Passenger.icns"
# The document types are what the Finder's Open With and a drop on the Dock's icon offer Passenger: solutions and C#
# projects (by extension, having no system type) and folders, never claimed as their default. macOS hands them to the
# Passenger running, which opens each in a window (PassengerApp.OpenFromSystem), rather than starting another.
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Passenger</string>
    <key>CFBundleDisplayName</key><string>Passenger</string>
    <key>CFBundleIdentifier</key><string>io.github.zaneris.passenger</string>
    <key>CFBundleExecutable</key><string>Passenger</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleIconFile</key><string>Passenger</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$build</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key><string>Solution File</string>
            <key>CFBundleTypeExtensions</key><array><string>sln</string><string>slnx</string></array>
            <key>CFBundleTypeRole</key><string>Editor</string>
            <key>LSHandlerRank</key><string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key><string>C# Project File</string>
            <key>CFBundleTypeExtensions</key><array><string>csproj</string></array>
            <key>CFBundleTypeRole</key><string>Editor</string>
            <key>LSHandlerRank</key><string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key><string>Folder</string>
            <key>LSItemContentTypes</key><array><string>public.folder</string></array>
            <key>CFBundleTypeRole</key><string>Viewer</string>
            <key>LSHandlerRank</key><string>None</string>
        </dict>
    </array>
</dict>
</plist>
EOF

# The JIT writes code, and the native libraries (Skia, dbgshim, the pseudo-terminal's shim) are not signed by
# Passenger's team. No debugger entitlement (com.apple.security.cs.debugger): the debugger's adapter runs under the
# machine's dotnet, whose signature is Microsoft's, and reaches a program's runtime, launched or attached to, through the
# pipes the runtime opens for a debugger in the user's temporary folder, never through the task port the entitlement
# guards (the system refuses dotnet the one it asks for each session, and nothing is lost, #17). Never get-task-allow,
# which lets anything attach to Passenger itself and which Apple's notary refuses.
entitlements="$work/entitlements.plist"
cat > "$entitlements" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.security.cs.allow-jit</key><true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
    <key>com.apple.security.cs.disable-library-validation</key><true/>
    <key>com.apple.security.cs.allow-dyld-environment-variables</key><true/>
</dict>
</plist>
EOF

# A Developer ID's or the notary's command: run, or in a dry run written to $commands, in the order it would run.
developer() {
    if [ -n "$dry_run" ]; then
        for word in "$@"; do
            printf "'%s' " "$(printf '%s' "$word" | sed "s/'/'\\\\''/g")"
        done >> "$commands"
        echo >> "$commands"
    else
        "$@"
    fi
}
# Under the hardened runtime: with the Developer ID, timestamped, else ad hoc the same way, so a package made here runs
# as the shipped one does. A dry run also signs ad hoc, so its package runs too.
sign() {
    if [ -n "$identity" ]; then
        developer codesign --force --options runtime --timestamp --sign "$identity" "$@"
    fi
    if [ -z "$identity" ] || [ -n "$dry_run" ]; then
        codesign --force --options runtime --sign - "$@"
    fi
}
# Sends the app (zipped) or the image to Apple's notary and waits. The submission's status, not notarytool's exit, says
# whether the notary accepted it; its log says why not.
notarize() {
    if [ -n "$dry_run" ]; then
        developer xcrun notarytool submit "$1" --keychain-profile "$profile" --wait --output-format json
        return
    fi
    result=$(xcrun notarytool submit "$1" --keychain-profile "$profile" --wait --output-format json)
    status=$(echo "$result" | plutil -extract status raw -o - -)
    if [ "$status" != Accepted ]; then
        echo "package-mac: notarizing $(basename "$1") came back $status" >&2
        xcrun notarytool log "$(echo "$result" | plutil -extract id raw -o - -)" --keychain-profile "$profile" >&2
        exit 1
    fi
}

# Inside out, then the bundle, which signs its main executable. The bundle's seal takes everything in Contents/MacOS
# for code, so .NET's DLLs and PDBs are signed too (their signatures kept in extended attributes); the entitlements are
# only the executables'.
find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Passenger" | while IFS= read -r file; do
    case $(file -b "$file") in
        Mach-O*executable*) sign --entitlements "$entitlements" "$file" ;;
        *) sign "$file" ;;
    esac
done
sign --entitlements "$entitlements" "$app"
codesign --verify --deep --strict "$app"
# Stapled, the app opens with no Gatekeeper warning offline too, once copied out of the image.
if [ -n "$profile" ]; then
    ditto -c -k --keepParent "$app" "$work/Passenger.zip"
    notarize "$work/Passenger.zip"
    developer xcrun stapler staple "$app"
fi

# Beside the app, a link to /Applications to drag it onto, Passenger's icon for the volume, and the window Finder opens
# on it (tools/dmg-layout.py). The volume's name carries the version, as the file's name cannot.
volume_name="Passenger $version"
ln -s /Applications "$stage/Applications"
cp "$root/src/Passenger.App/Assets/Passenger.icns" "$stage/.VolumeIcon.icns"
# The window's picture, an arrow from the app to Applications (#188): tools/dmg-background.svg drawn by sips at its size
# and at twice it, each from the SVG as `just app-icon` draws the icons, joined by tiffutil into one TIFF that Finder
# shows at the screen's scale. sips says nothing in its exit status of a file it skipped, so the picture's absence
# says it.
background="$root/tools/dmg-background.svg"
size=$(sed -n 's/^<svg [^>]*width="\([0-9]*\)" height="\([0-9]*\)".*/\1 \2/p' "$background")
[ -n "$size" ] || fail "tools/dmg-background.svg names no width and height in pixels on its <svg>"
width=${size% *}
height=${size#* }
mkdir "$stage/.background"
for scale in 1 2; do
    picture="$work/background@${scale}x.png"
    sips -s format png -z $((height * scale)) $((width * scale)) "$background" --out "$picture" >/dev/null || true
    [ -s "$picture" ] || fail "sips drew no ${scale}x picture of $background (does this macOS's sips read SVG?)"
done
tiffutil -cathidpicheck "$work/background@1x.png" "$work/background@2x.png" -out "$stage/.background/background.tiff"
# fseventsd logs nothing on a volume whose .fseventsd holds no_log, so the read-write mount leaves no log in the image.
mkdir "$stage/.fseventsd"
touch "$stage/.fseventsd/no_log"
retry_hdiutil "$work/image.dmg" create -volname "$volume_name" -srcfolder "$stage" -fs HFS+ -format UDRW -ov "$work/image.dmg"
attach "$work/image.dmg" -readwrite
# The volume's top folder says it has an icon of its own (Finder's kHasCustomIcon), which hdiutil does not copy.
xattr -wx com.apple.FinderInfo 0000000000000000040000000000000000000000000000000000000000000000 "$mount"
# Written on the volume itself, since the picture's alias names the volume's creation date and the catalog IDs the
# volume gave the picture and its folder, which the compressed image keeps.
python3 -I "$root/tools/dmg-layout.py" write "$mount" "$volume_name"
detach
# LZMA: about a quarter smaller than zlib or LZFSE (ULFO) to download; macOS reads it from 10.15, and the app needs 13.
retry_hdiutil "$image" convert "$work/image.dmg" -format ULMO -o "$image"
rm -f "$work/image.dmg"
if [ -n "$identity" ]; then
    developer codesign --force --timestamp --sign "$identity" "$image"
fi
if [ -n "$profile" ]; then
    notarize "$image"
    developer xcrun stapler staple "$image"
fi

# The image as a person gets it: compressed and whole, mounted read-only, Passenger.app beside the Applications link
# under Passenger's icon and over the window's picture, which the .DS_Store names, the app's signature whole and without
# get-task-allow, its version and the volume's the package's, Roslyn's server, the debugger's adapter, the licence and
# the notices in it, and every Mach-O file built for the image's processor (a file built only for the other would make
# macOS run the app through Rosetta, or not at all).
hdiutil verify -quiet "$image"
format=$(hdiutil imageinfo "$image" 2>/dev/null | sed -n 's/^Format: //p')
[ "$format" = ULMO ] || fail "the image's format is '$format', not ULMO"
attach "$image" -readonly
shipped="$mount/Passenger.app"
volume=$(diskutil info "$mount" | sed -n 's/^ *Volume Name: *//p')
[ "$volume" = "$volume_name" ] || fail "the image's volume is named '$volume', not '$volume_name'"
[ "$(readlink "$mount/Applications")" = /Applications ] || fail "the image has no link to /Applications beside the app"
xattr -px com.apple.FinderInfo "$mount" | grep -q '^00 00 00 00 00 00 00 00 04' || fail "the image's volume has no icon of its own"
# The picture in .background, of the window's size and twice it, and the .DS_Store's alias to it naming this volume,
# its creation date and the picture's catalog IDs and paths, so that Finder finds it once a Mac mounts it in /Volumes.
python3 -I "$root/tools/dmg-layout.py" check "$mount" "$volume_name"
[ "$(ls -A "$mount/.fseventsd")" = no_log ] || fail "the image carries the event log of its read-write mount"
codesign --verify --deep --strict "$shipped"
if codesign -d --entitlements - --xml "$shipped" 2>/dev/null | grep -q get-task-allow; then
    fail "the app's entitlements carry get-task-allow"
fi
plist="$shipped/Contents/Info.plist"
said=$(plutil -extract CFBundleShortVersionString raw -o - "$plist")
[ "$said" = "$version" ] || fail "the app's Info.plist says version $said, not $version"
said=$(plutil -extract CFBundleVersion raw -o - "$plist")
[ "$said" = "$build" ] || fail "the app's Info.plist says build $said, not $build"
for part in roslyn/Microsoft.CodeAnalysis.LanguageServer.dll roslyn-extension/Passenger.Roslyn.Extension.dll debugger/Passenger.DebugAdapter.dll LICENSE.txt THIRD-PARTY-NOTICES.txt; do
    [ -f "$shipped/Contents/MacOS/$part" ] || fail "the app has no $part"
done
find "$shipped/Contents/MacOS" -type f -exec file -F '|' {} + | grep '| *Mach-O' | cut -d'|' -f1 > "$work/mach-o"
[ -s "$work/mach-o" ] || fail "the app has no Mach-O file"
while IFS= read -r file; do
    lipo "$file" -verify_arch "$machine" || fail "${file#"$shipped/"} is built for $(lipo -archs "$file"), not $machine"
done < "$work/mach-o"
# Notarized, Gatekeeper takes both as a download: the image to open, the app to run.
if [ -n "$profile" ]; then
    developer xcrun stapler validate "$image"
    developer xcrun stapler validate "$shipped"
    developer spctl --assess --type open --context context:primary-signature --verbose "$image"
    developer spctl --assess --type execute --verbose "$shipped"
fi
detach

size=$(du -h "$image" | cut -f1 | tr -d ' ')
if [ -n "$dry_run" ]; then
    signed="signed ad hoc; what the Developer ID would run is in $commands"
elif [ -z "$identity" ]; then
    signed="signed ad hoc and not notarized: opened once through System Settings > Privacy & Security > Open Anyway"
elif [ -z "$profile" ]; then
    signed="signed by $identity, not notarized"
else
    signed="signed by $identity, notarized and stapled"
fi
echo "package: $image ($size, Passenger $version build $build for $rid, $signed)"
