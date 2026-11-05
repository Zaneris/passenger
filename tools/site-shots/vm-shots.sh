#!/usr/bin/env bash
# The site's html-preview scene, which needs a window someone could see, taken in the Parallels Windows 11 VM, whose
# desktop is its own: vm-shots.sh IMAGES in each of SITE_SHOTS_THEMES (shots.sh runs it). The git HEAD is built in a
# clone of its own there (passenger-site-shots, as vm-test.sh builds one); vm-scene.ps1, through the Mac's Documents
# folder the VM shares, prepares the page's folder and each run's state under %USERPROFILE%\site-shots; Passenger is
# started from a batch file and placed, its window taken as the VM's desktop composes it and scaled to the site's 2400
# pixels; then the Passenger it started is ended.
set -euo pipefail

images=$1
read -r -a themes <<< "${SITE_SHOTS_THEMES:-dark light}"
repo=$(cd "$(dirname "$0")/../.." && pwd -P)
work="$repo/artifacts/site-shots"
vm="Windows 11"
share="$HOME/Documents/passenger-vm/site-shots"
shared='C:\Mac\Home\Documents\passenger-vm\site-shots'
root='C:\Users\zaneris\site-shots'
clone=passenger-site-shots
exe="C:\\Users\\zaneris\\src\\$clone\\artifacts\\bin\\Passenger.App\\debug\\Passenger.exe"
mkdir -p "$share" "$work/png"
# The version whose What's New each state has shown, so none opens over the scene.
version=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$repo/Directory.Build.props" | head -1)

# A command in the VM, bounded as prlctl exec can hang on what it started; Parallels sometimes answers 255 before
# anything runs, as vm-test.sh finds, so that alone is tried again.
in_vm() {
    local limit=$1 attempt status
    shift
    for attempt in 1 2 3; do
        status=0
        perl -e 'alarm shift; exec @ARGV' "$limit" prlctl exec "$vm" --current-user "$@" || status=$?
        [[ $status == 255 ]] || return 0
        sleep 15
    done
    echo "vm-shots: Parallels answered 255 three times" >&2
    return 1
}

# One step of vm-scene.ps1 in the VM, what it said printed.
step() {
    local name=$1
    shift
    rm -f "$share/$name.log"
    in_vm 1200 powershell -NoProfile -ExecutionPolicy Bypass -File "$shared\\vm-scene.ps1" -Step "$name" -Share "$shared" \
        -Root "$root" "$@" || true
    [[ -f "$share/$name.log" ]] || return 1
    sed "s/^/vm-shots: /" "$share/$name.log"
    ! grep -q '^failed:' "$share/$name.log"
}

# The HEAD built in the VM's clone, the commits origin/main lacks brought as a bundle, once a sitting.
built() {
    git -C "$repo" fetch -q origin main 2>/dev/null || true
    git -C "$repo" bundle create -q "$share/head.bundle" HEAD ^origin/main
    cat > "$share/build.cmd" <<BATCH
@echo off
cd /d %USERPROFILE%\\src
if not exist $clone git clone -q passenger $clone
cd $clone || exit /b 1
git fetch -q https://github.com/Zaneris/passenger.git main || exit /b 1
git fetch -q $shared\\head.bundle HEAD || exit /b 1
git checkout -q --detach $(git -C "$repo" rev-parse HEAD) || exit /b 1
set AVALONIA_TELEMETRY_OPTOUT=1
dotnet build Passenger.slnx -v quiet -nologo > $shared\\build.log 2>&1 || exit /b 1
BATCH
    rm -f "$share/build.log"
    in_vm 1800 cmd /c "$shared\\build.cmd" || true
    grep -q "Build succeeded" "$share/build.log" \
        || { tail -20 "$share/build.log"; echo "vm-shots: the VM's build failed" >&2; exit 1; }
}

cp "$repo/tools/site-shots/html/index.html" "$repo/tools/site-shots/vm-scene.ps1" "$share/"
# Node.js for the HTML server, which the VM lacks: nodejs.org's Windows ARM64 zip, checked against its published hash.
node=node-v26.12.0-win-arm64
node_sha=cc773b9829623e3d4fd13aef394f05010e0459273f27fb3255150f38a2577abf
mkdir -p "$share/node"
if ! echo "$node_sha  $share/node/$node.zip" | shasum -a 256 -c --status 2> /dev/null; then
    curl -fsSL -o "$share/node/$node.zip" "https://nodejs.org/dist/v26.12.0/$node.zip"
    echo "$node_sha  $share/node/$node.zip" | shasum -a 256 -c --status || { echo "vm-shots: $node.zip is not nodejs.org's" >&2; exit 1; }
fi
built
step stop
step prepare -Node "$node"

# One theme's picture: Passenger on the page beside its markup, the editor at the hero the preview shows.
take() {
    local scene=html-preview theme=$1 folder="$root\\GreenhouseSite"
    step state -Scene "$scene" -Theme "$theme" -Folder "$folder" -Version "$version" -Node "$node"
    cat > "$share/start.cmd" <<BATCH
@echo off
set PASSENGER_SETTINGS=$root\\state-$scene-$theme
set AVALONIA_TELEMETRY_OPTOUT=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
start "" "$exe" "$folder" --file index.html --caret 103:7
BATCH
    in_vm 20 cmd /c "$shared\\start.cmd" 2> /dev/null || true
    if ! step ready -Scene "$scene" -Theme "$theme" -Height 1468; then
        step stop
        exit 1
    fi
    # The preview's page is drawn by the system's web view, which tells nothing as it finishes.
    sleep 15
    step capture -Scene "$scene" -Theme "$theme" || { step stop; exit 1; }
    cp "$share/frames/$scene-$theme.png" "$work/png/"
    step stop
    sips --resampleWidth 2400 "$work/png/$scene-$theme.png" --out "$work/png/$scene-$theme.png" > /dev/null
    sips --cropToHeightWidth 1500 2400 --cropOffset 0 0 "$work/png/$scene-$theme.png" > /dev/null
    cwebp -quiet -q 82 -m 6 "$work/png/$scene-$theme.png" -o "$images/$scene-$theme.webp"
    if (($(wc -c < "$images/$scene-$theme.webp") > 150000)); then
        cwebp -quiet -size 150000 -pass 10 -m 6 "$work/png/$scene-$theme.png" -o "$images/$scene-$theme.webp"
    fi
    echo "vm-shots: $images/$scene-$theme.webp ($(($(wc -c < "$images/$scene-$theme.webp") / 1024)) KB)"
}

for theme in "${themes[@]}"; do
    take "$theme"
done
