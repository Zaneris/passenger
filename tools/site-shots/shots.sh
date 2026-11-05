#!/usr/bin/env bash
# The site's pictures, site/images/<scene>-<dark|light>.webp, retaken from the sample solutions beside this script:
# just site-shots [SCENE...] (every headless scene when none is named), after just build, on macOS with libwebp's cwebp
# and img2webp; godot-game, taken only when named, runs in a window on the screen (game.py), and html-preview, taken only
# when named too, in the Windows VM (vm-shots.sh), since neither draws in a window nobody sees. Each sample is copied to
# a folder of its own under TMPDIR and given a git history there; its tools' state stays under artifacts/site-shots and
# Passenger's beside the copy, never the person's. SITE_SHOTS_FLAGS (--window-buttons by default, so macOS's are in
# every main-window picture) is added to every capture's command line; SITE_SHOTS_REUSE=1 takes the pictures in the
# last run's copy without making it again; SITE_SHOTS_THEMES=dark (or light) takes one theme's. A step's point is in the
# window's logical units: half the picture's pixels.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd -P)
passenger="$repo/artifacts/bin/Passenger.App/debug/Passenger"
work="$repo/artifacts/site-shots"
images="$repo/site/images"
# About 100–150 KB a picture at 2400x1500: this quality, or the most bytes when a busy one would take more.
quality=82
most=150000
read -r -a extra <<< "${SITE_SHOTS_FLAGS---window-buttons}"

if [[ ! -x "$passenger" ]]; then
    echo "site-shots: $passenger is not there; run just build first" >&2
    exit 2
fi
command -v cwebp > /dev/null || { echo "site-shots: cwebp is not on the PATH (brew install webp)" >&2; exit 2; }

all_scenes=(editor debugger git find-in-files profiler xaml project-files web live-tree agent themes aspire requests
    godot-scene godot-values godot-live-tree godot-profile blazor-wasm usages resx gdscript godot-shader godot-tests
    aspire-metrics)
# Taken only when named: a window someone could see, on the screen (game.py) or in the Windows VM (vm-shots.sh).
windowed_scenes=(godot-game html-preview)
scenes=("$@")
if [[ ${#scenes[@]} -eq 0 ]]; then
    scenes=("${all_scenes[@]}")
fi
for scene in "${scenes[@]}"; do
    if [[ " ${all_scenes[*]} ${windowed_scenes[*]} " != *" $scene "* ]]; then
        echo "site-shots: no scene '$scene'; the scenes are ${all_scenes[*]} ${windowed_scenes[*]}" >&2
        exit 2
    fi
done

# Everything the sample's tools write goes under artifacts/site-shots, and git's configuration is an empty one.
mkdir -p "$work/home" "$work/nuget" "$work/npm" "$work/png"
: > "$work/gitconfig"
: > "$work/npm/npmrc"
export DOTNET_CLI_HOME="$work/home"
export NUGET_PACKAGES="$work/nuget/packages"
export NUGET_HTTP_CACHE_PATH="$work/nuget/http-cache"
export NUGET_PLUGINS_CACHE_PATH="$work/nuget/plugins-cache"
export npm_config_cache="$work/npm/cache"
export npm_config_userconfig="$work/npm/npmrc"
export GIT_CONFIG_GLOBAL="$work/gitconfig"
export GIT_CONFIG_NOSYSTEM=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export AVALONIA_TELEMETRY_OPTOUT=1
# The sample's Avalonia app draws its window off-screen (its Program.cs), so nothing shows while a picture is taken.
export GREENHOUSE_HEADLESS=1
# The log shows its fixed times as they are wherever the pictures are taken.
export TZ=UTC

# A canonical path, since MSBuild and Roslyn report resolved ones (macOS's TMPDIR is under the /var symbolic link).
tmp=$(realpath "${TMPDIR:-/tmp}")
folder="$tmp/passenger-site-shots/Greenhouse"

# One commit of the sample's history, at a fixed time.
history() {
    local when=$1 message=$2
    shift 2
    git -C "$folder" add -- "$@"
    GIT_AUTHOR_DATE="$when" GIT_COMMITTER_DATE="$when" git -C "$folder" commit -q -m "$message"
}

# The copy, the same every time: the sample, a history with a branch merged back, two files changed since the last
# commit (their committed text is under before/) and one never added; then built once, so XAML Preview has the app's
# build and Debug and Profile start at once.
prepare() {
    rm -rf "$folder"
    mkdir -p "$folder"
    cp -R "$repo/tools/site-shots/sample/." "$folder/"
    export GIT_AUTHOR_NAME="Rosa Field" GIT_AUTHOR_EMAIL="rosa@greenhouse.example"
    export GIT_COMMITTER_NAME="Rosa Field" GIT_COMMITTER_EMAIL="rosa@greenhouse.example"
    git -C "$folder" init -q -b main
    mv "$folder/web/styles/dashboard.css" "$work/dashboard.css"
    local changed
    for changed in src/Greenhouse.Cli/Program.cs src/Greenhouse.Core/WateringPlan.cs; do
        cp "$folder/$changed" "$work/$(basename "$changed").final"
        cp "$repo/tools/site-shots/before/$changed" "$folder/$changed"
    done
    history "2026-10-05T09:12:00+00:00" "A greenhouse of plants and when each needs water" \
        .gitignore .editorconfig nuget.config Directory.Build.props Directory.Packages.props Greenhouse.slnx \
        src/Greenhouse.Core/Plant.cs src/Greenhouse.Core/WateringPlan.cs src/Greenhouse.Core/Greenhouse.Core.csproj
    history "2026-10-05T15:40:00+00:00" "Tests for the watering plan" tests
    git -C "$folder" checkout -q -b forecast
    history "2026-10-06T10:05:00+00:00" "How long the rain tank lasts" src/Greenhouse.Core/Forecast.cs
    history "2026-10-06T11:30:00+00:00" "The forecast on the command line" src/Greenhouse.Cli
    git -C "$folder" checkout -q main
    history "2026-10-07T09:20:00+00:00" "The greenhouse in a window" src/Greenhouse.App
    GIT_AUTHOR_DATE="2026-10-07T14:02:00+00:00" GIT_COMMITTER_DATE="2026-10-07T14:02:00+00:00" \
        git -C "$folder" merge -q --no-ff -m "Merge the tank forecast" forecast
    history "2026-10-08T16:45:00+00:00" "The greenhouse's page" web
    cp "$work/Program.cs.final" "$folder/src/Greenhouse.Cli/Program.cs"
    cp "$work/WateringPlan.cs.final" "$folder/src/Greenhouse.Core/WateringPlan.cs"
    cp "$work/dashboard.css" "$folder/web/styles/dashboard.css"
    unset GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
    dotnet build "$folder/Greenhouse.slnx" -nologo -v:q -clp:ErrorsOnly
}

if [[ -z ${SITE_SHOTS_REUSE:-} || ! -d "$folder/.git" ]]; then
    prepare
fi

# The aspire and requests scenes' solution, services/ beside this script, copied beside the sample's copy.
services="$tmp/passenger-site-shots/GreenhouseServices"
# The AppHost's home of its own, short as macOS's socket paths ask, since its record of itself is a socket there.
aspire_home=/private/tmp/passenger-site-shots-aspire
services_prepared=
# The services (an Aspire AppHost over a web API on EF Core and SQLite and a worker that calls it each second) with a
# history of their own, built once, when a scene first asks; the AppHost asks the window nothing
# (ASPIRE_INTERACTIVITY_ENABLED), where a dialog would say no development certificate is trusted there.
services_ready() {
    if [[ -n $services_prepared ]] || [[ -n ${SITE_SHOTS_REUSE:-} && -d "$services/.git" ]]; then
        services_prepared=1
        return
    fi
    rm -rf "$services" "$aspire_home"
    mkdir -p "$services" "$aspire_home"
    cp -R "$repo/tools/site-shots/services/." "$services/"
    python3 -I - "$services/src/Greenhouse.AppHost/Properties/launchSettings.json" "$aspire_home" <<'EOF'
import json, sys
path, home = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as f:
    launch = json.load(f)
variables = launch["profiles"]["http"]["environmentVariables"]
variables["HOME"] = home
variables["ASPIRE_INTERACTIVITY_ENABLED"] = "false"
with open(path, "w", encoding="utf-8") as f:
    json.dump(launch, f, indent=2)
    f.write("\n")
EOF
    export GIT_AUTHOR_NAME="Rosa Field" GIT_AUTHOR_EMAIL="rosa@greenhouse.example"
    export GIT_COMMITTER_NAME="Rosa Field" GIT_COMMITTER_EMAIL="rosa@greenhouse.example"
    git -C "$services" init -q -b main
    git -C "$services" add -- . ':!src/Greenhouse.AppHost'
    GIT_AUTHOR_DATE="2026-10-12T09:30:00+00:00" GIT_COMMITTER_DATE="2026-10-12T09:30:00+00:00" \
        git -C "$services" commit -q -m "The greenhouse's API and the sensors that ask it"
    git -C "$services" add -- src/Greenhouse.AppHost
    GIT_AUTHOR_DATE="2026-10-13T10:15:00+00:00" GIT_COMMITTER_DATE="2026-10-13T10:15:00+00:00" \
        git -C "$services" commit -q -m "Both run by an Aspire AppHost"
    unset GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
    dotnet build "$services/GreenhouseServices.slnx" -nologo -v:q -clp:ErrorsOnly
    services_prepared=1
}

# The Godot scenes' game (godot/), on the Godot found, its settings and user:// under artifacts/site-shots, run headless.
game="$tmp/passenger-site-shots/GreenhouseGame"
game_prepared=
export PASSENGER_GODOT_HOME="$work/godot-home"
# The Godot .NET build the Godot scenes run on, exported as GODOT.
godot_found() {
    local godot=${GODOT:-}
    for found in "$godot" "/Applications/Godot_mono.app/Contents/MacOS/Godot" "$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot"; do
        if [[ -n $found && -x $found ]]; then
            godot=$found
            break
        fi
    done
    if [[ -z $godot || ! -x $godot ]]; then
        echo "site-shots: no Godot .NET build found (GODOT, or Godot_mono.app in Applications)" >&2
        exit 2
    fi
    export GODOT=$godot
}

game_ready() {
    if [[ -n $game_prepared ]] || [[ -n ${SITE_SHOTS_REUSE:-} && -d "$game/.git" ]]; then
        game_prepared=1
        return
    fi
    godot_found
    local godot=$GODOT version packages
    version=$(HOME="$PASSENGER_GODOT_HOME" "$godot" --version | grep -Eo '^[0-9]+\.[0-9]+(\.[0-9]+)?' | head -1)
    packages=$(cd "$(dirname "$godot")/../Resources/GodotSharp/Tools/nupkgs" && pwd -P)
    rm -rf "$game"
    mkdir -p "$game" "$PASSENGER_GODOT_HOME"
    cp -R "$repo/tools/site-shots/godot/." "$game/"
    sed "s/@FEATURES@/${version%.*}/" "$game/project.godot.in" > "$game/project.godot"
    sed "s/@VERSION@/$version/" "$game/Greenhouse.csproj.in" > "$game/Greenhouse.csproj"
    rm "$game/project.godot.in" "$game/Greenhouse.csproj.in"
    printf '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="godot" value="%s" />\n  </packageSources>\n</configuration>\n' \
        "$packages" > "$game/nuget.config"
    export GIT_AUTHOR_NAME="Rosa Field" GIT_AUTHOR_EMAIL="rosa@greenhouse.example"
    export GIT_COMMITTER_NAME="Rosa Field" GIT_COMMITTER_EMAIL="rosa@greenhouse.example"
    git -C "$game" init -q -b main
    git -C "$game" add -- .
    GIT_AUTHOR_DATE="2026-10-20T09:00:00+00:00" GIT_COMMITTER_DATE="2026-10-20T09:00:00+00:00" \
        git -C "$game" commit -q -m "The greenhouse as a game: its benches, sprinkler and rain tank"
    unset GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
    dotnet build "$game/Greenhouse.csproj" -nologo -v:q -clp:ErrorsOnly
    game_prepared=1
}

# The gdscript, godot-shader and godot-tests scenes' game, with GUT 9.6.1 (MIT) at its commit.
more_game="$tmp/passenger-site-shots/more/GreenhouseGame"
more_prepared=
gut_commit=cf45f668709d988346e766afee916d4169a9283b
# The game given godot-more/ (a script, a shader and GUT tests) and GUT's addon, in a copy of its own, so the other
# Godot scenes' Explorer lists what it always has.
more_ready() {
    game_ready
    if [[ -n $more_prepared ]] || [[ -n ${SITE_SHOTS_REUSE:-} && -d "$more_game/.git" ]]; then
        more_prepared=1
        return
    fi
    # Fetched once into artifacts/site-shots; only its addon goes into the game.
    if [[ ! -f "$work/gut/$gut_commit/addons/gut/plugin.cfg" ]]; then
        rm -rf "$work/gut"
        git init -q "$work/gut/$gut_commit"
        git -C "$work/gut/$gut_commit" fetch -q --depth 1 https://github.com/bitwes/Gut "$gut_commit"
        git -C "$work/gut/$gut_commit" checkout -q FETCH_HEAD -- addons/gut
    fi
    rm -rf "$more_game"
    mkdir -p "$more_game/addons"
    cp -R "$repo/tools/site-shots/godot/." "$more_game/"
    cp -R "$repo/tools/site-shots/godot-more/." "$more_game/"
    cp -R "$work/gut/$gut_commit/addons/gut" "$more_game/addons/gut"
    # The game's own project files as its copy committed them, whatever its runs have written since.
    local made
    for made in project.godot Greenhouse.csproj nuget.config; do
        git -C "$game" show "HEAD:$made" > "$more_game/$made"
    done
    rm "$more_game/project.godot.in" "$more_game/Greenhouse.csproj.in"
    export GIT_AUTHOR_NAME="Rosa Field" GIT_AUTHOR_EMAIL="rosa@greenhouse.example"
    export GIT_COMMITTER_NAME="Rosa Field" GIT_COMMITTER_EMAIL="rosa@greenhouse.example"
    git -C "$more_game" init -q -b main
    git -C "$more_game" add -- .
    GIT_AUTHOR_DATE="2026-10-20T09:00:00+00:00" GIT_COMMITTER_DATE="2026-10-20T09:00:00+00:00" \
        git -C "$more_game" commit -q -m "The greenhouse as a game: its benches, sprinkler and rain tank"
    unset GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
    dotnet build "$more_game/Greenhouse.csproj" -nologo -v:q -clp:ErrorsOnly
    more_prepared=1
}

# The godot-game scene's game: Squash the Creeps (3D) with C#, of godot-demo-projects (MIT, its art too), at its commit.
creeps="$tmp/passenger-site-shots/SquashTheCreeps"
creeps_commit=b761b4cd5718a7118f2c95eb55f181d0a5f004f1
# The game copied out of its clone, outside any repository as MSBuild asks, on the Godot found's GodotSharp and
# nuget.org's reference packs, built and imported once.
creeps_ready() {
    godot_found
    if [[ -n ${SITE_SHOTS_REUSE:-} && -d "$creeps/.git" ]]; then
        return
    fi
    local clone="$tmp/passenger-site-shots/clones/godot-demo-projects" version
    if [[ ! -f "$clone/mono/squash_the_creeps/project.godot" ]] \
        || [[ $(git -C "$clone" rev-parse HEAD) != "$creeps_commit" ]]; then
        rm -rf "$clone"
        git init -q "$clone"
        git -C "$clone" sparse-checkout set --no-cone /mono/squash_the_creeps/
        git -C "$clone" fetch -q --depth 1 --filter=blob:none https://github.com/godotengine/godot-demo-projects \
            "$creeps_commit"
        git -C "$clone" checkout -q FETCH_HEAD
    fi
    version=$(HOME="$PASSENGER_GODOT_HOME" "$GODOT" --version | grep -Eo '^[0-9]+\.[0-9]+(\.[0-9]+)?' | head -1)
    rm -rf "$creeps"
    mkdir -p "$creeps"
    cp -R "$clone/mono/squash_the_creeps/." "$creeps/"
    sed -i '' "s|Godot.NET.Sdk/[0-9.]*\"|Godot.NET.Sdk/$version\"|" "$creeps/Squash the Creeps (3D).csproj"
    cat > "$creeps/nuget.config" <<EOF3
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="godot" value="$(cd "$(dirname "$GODOT")/../Resources/GodotSharp/Tools/nupkgs" && pwd -P)" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="godot">
      <package pattern="Godot*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF3
    git -C "$creeps" init -q -b main
    printf '.godot/\nbin/\nobj/\n*.user\nnuget.config\n' > "$creeps/.git/info/exclude"
    git -C "$creeps" add -- .
    GIT_AUTHOR_NAME="Godot Engine contributors" GIT_AUTHOR_EMAIL="demos@godotengine.example" \
        GIT_COMMITTER_NAME="Godot Engine contributors" GIT_COMMITTER_EMAIL="demos@godotengine.example" \
        GIT_AUTHOR_DATE="2026-10-25T09:00:00+00:00" GIT_COMMITTER_DATE="2026-10-25T09:00:00+00:00" \
        git -C "$creeps" commit -q -m "Squash the Creeps (3D) with C#, godot-demo-projects at ${creeps_commit:0:9}"
    dotnet build "$creeps/Squash the Creeps (3D).csproj" -nologo -v:q -clp:ErrorsOnly
    HOME="$PASSENGER_GODOT_HOME" XDG_CONFIG_HOME="$PASSENGER_GODOT_HOME/.config" \
        XDG_DATA_HOME="$PASSENGER_GODOT_HOME/.local/share" XDG_CACHE_HOME="$PASSENGER_GODOT_HOME/.cache" SDL_JOYSTICK_HIDAPI=0 \
        "$GODOT" --headless --path "$creeps" --import > "$work/creeps-import.log" 2>&1
}

# The blazor-wasm scene's app: a WebAssembly Blazor Web App given blazor/'s page, debugged in a headless Chromium.
web_app="$tmp/passenger-site-shots/GreenhouseWeb"
web_app_prepared=
web_app_ready() {
    if [[ -n $web_app_prepared ]] || [[ -n ${SITE_SHOTS_REUSE:-} && -d "$web_app/.git" ]]; then
        web_app_prepared=1
        return
    fi
    rm -rf "$web_app"
    mkdir -p "$web_app"
    cp "$repo/tools/site-shots/blazor/nuget.config" "$web_app/"
    dotnet new blazor -n Greenhouse.Web -o "$web_app" --interactivity WebAssembly --no-https > /dev/null
    cp "$repo/tools/site-shots/blazor/Benches.razor" "$web_app/Greenhouse.Web.Client/Pages/Benches.razor"
    python3 -I - "$web_app/Greenhouse.Web/Properties/launchSettings.json" <<'EOF2'
import json, sys
path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    launch = json.load(f)
http = launch["profiles"]["http"]
http["applicationUrl"] = "http://localhost:5173"
http["launchUrl"] = "benches"
http["launchBrowser"] = False
with open(path, "w", encoding="utf-8") as f:
    json.dump(launch, f, indent=2)
    f.write("\n")
EOF2
    export GIT_AUTHOR_NAME="Rosa Field" GIT_AUTHOR_EMAIL="rosa@greenhouse.example"
    export GIT_COMMITTER_NAME="Rosa Field" GIT_COMMITTER_EMAIL="rosa@greenhouse.example"
    git -C "$web_app" init -q -b main
    git -C "$web_app" add -- .
    GIT_AUTHOR_DATE="2026-10-22T09:00:00+00:00" GIT_COMMITTER_DATE="2026-10-22T09:00:00+00:00" \
        git -C "$web_app" commit -q -m "The greenhouse's benches on the web"
    unset GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
    dotnet build "$web_app/Greenhouse.Web/Greenhouse.Web.csproj" -nologo -v:q -clp:ErrorsOnly
    web_app_prepared=1
}

# One picture: a fresh state of its own (layout, breakpoints, logs), the window at the site's size, the scene's steps.
shot() {
    local name=$1 theme=$2
    shift 2
    local state="$tmp/passenger-site-shots/state/$name-$theme" png="$work/png/$name-$theme.png"
    if [[ $name == agent ]]; then
        # The window's agent socket is in its state's mcp/, and macOS keeps a socket's path to 103 bytes.
        state="$tmp/pss-$name-$theme"
    elif [[ $name == profiler ]]; then
        # The status bar names a profile's snapshot by its folder, so this is one named for the sample at a fixed place.
        state=/private/tmp/Greenhouse
    fi
    rm -rf "$state"
    mkdir -p "$state"
    # The sample's solution, unless the scene names another (solution=…, the services scenes').
    PASSENGER_SETTINGS="$state" "$passenger" "${solution:-$folder/Greenhouse.slnx}" --theme "$theme" --look passenger \
        --size 1200x750 --scale 2 ${extra[@]+"${extra[@]}"} "$@" --screenshot "$png"
    encode "$png" "$images/$name-$theme.webp"
}

# A capture to the site's WebP: at the quality above, or at the most bytes when a busy picture would take more.
encode() {
    local png=$1 webp=$2
    cwebp -quiet -q "$quality" -m 6 "$png" -o "$webp"
    if (($(wc -c < "$webp") > most)); then
        cwebp -quiet -size "$most" -pass 10 -m 6 "$png" -o "$webp"
    fi
    echo "site-shots: $webp ($(($(wc -c < "$webp") / 1024)) KB)"
}

# Every look, in Themes.cs' order.
looks=(Passenger Harbour Sorbet Nocturne Saffron Bonbon Ledger Slate Orbit Nightjar Moss Phosphor)
# The themes scene: the same window in every look, each over a copy of the solution file named after its look, so its
# title bar and Solution view name it; then those frames as one animated picture, Passenger's own first, every frame a
# key frame so each is whole wherever a viewer joins; and Passenger's own look still, for those who ask for less motion.
themes() {
    local theme=$1 look lower state
    mkdir -p "$work/png/themes"
    for look in "${looks[@]}"; do
        lower=$(tr '[:upper:]' '[:lower:]' <<< "$look")
        state="$tmp/passenger-site-shots/state/themes-$lower-$theme"
        rm -rf "$state"
        mkdir -p "$state"
        cp "$folder/Greenhouse.slnx" "$folder/$look.slnx"
        # The class's doc comment, keywords, types, a lambda and numbers in the editor, the Explorer beside it.
        if ! PASSENGER_SETTINGS="$state" "$passenger" "$folder/$look.slnx" --theme "$theme" --look "$lower" \
            --size 932x530 --scale 2 ${extra[@]+"${extra[@]}"} --file src/Greenhouse.Core/WateringPlan.cs \
            --caret 4:21 --screenshot "$work/png/themes/$lower-$theme.png"; then
            rm -f "$folder/$look.slnx"
            exit 1
        fi
        # Gone again at once: the other scenes' Commit window would list it as a file never added.
        rm -f "$folder/$look.slnx"
    done
    local frames=()
    for look in "${looks[@]}"; do
        frames+=("$work/png/themes/$(tr '[:upper:]' '[:lower:]' <<< "$look")-$theme.png")
    done
    img2webp -loop 0 -kmin 0 -kmax 1 -lossy -q 78 -m 6 -d 1800 "${frames[@]}" -o "$images/themes-$theme.webp" > /dev/null
    echo "site-shots: $images/themes-$theme.webp ($(($(wc -c < "$images/themes-$theme.webp") / 1024)) KB)"
    encode "$work/png/themes/passenger-$theme.png" "$images/themes-still-$theme.webp"
}

scene() {
    local name=$1 theme=$2
    case $name in
        editor)
            # Completion after a dot, parameters' names before the literals, and the change bars of uncommitted lines.
            shot "$name" "$theme" --file src/Greenhouse.Cli/Program.cs --caret 14:2 --key enter --key enter \
                --type "plan" --type "." --wait 1500 ;;
        debugger)
            # At a breakpoint in the library called from the command line's program, resumed twice to day 3's round.
            shot "$name" "$theme" --file src/Greenhouse.Core/WateringPlan.cs --caret 27:9 --key F9 \
                --click-name DebugButton --wait 6000 --key F5 --wait 1500 --key F5 --wait 1500 --hover 180,400 ;;
        git)
            # The Commit window, then the Git window's log widened past its dates, a commit on the merged branch chosen.
            shot "$name" "$theme" --file src/Greenhouse.Core/WateringPlan.cs --caret 26:9 --click 20,95 --key "meta+9" \
                --drag 808,600,975,600 --click 414,602 --hover 900,200 ;;
        find-in-files)
            # The popup's matches across the solution and the first one's preview.
            shot "$name" "$theme" --file src/Greenhouse.Cli/Program.cs --key "meta+shift+F" --type "Litres" \
                --wait 1000 ;;
        profiler)
            # The command-line program profiled to its end (about 2 s): its timeline and call tree.
            shot "$name" "$theme" --file src/Greenhouse.Core/Forecast.cs --click-name ProfileButton --wait 15000 ;;
        xaml)
            # XAML Preview, then completion from its x:DataType, with nothing typed so the preview stays live.
            shot "$name" "$theme" --file src/Greenhouse.App/MainWindow.axaml --caret 16:40 --key "meta+shift+F12" \
                --key "meta+shift+A" --type "XAML Preview" --key enter --wait 8000 --key "control+SPACE" --wait 1500 ;;
        project-files)
            # The hover card of a property used in Directory.Packages.props: its value and where it is set.
            shot "$name" "$theme" --file Directory.Packages.props --hover 832,214 --wait 2000 ;;
        web)
            # TypeScript's completion of a type from another file.
            shot "$name" "$theme" --file web/src/dashboard.ts --caret 12:20 --key enter --type "plant" --type "." \
                --wait 1500 ;;
        live-tree)
            # The app debugged, its summary's TextBlock chosen in the Live Visual Tree and its bindings' values inline.
            shot "$name" "$theme" --file src/Greenhouse.App/MainWindow.axaml --caret 16:1 \
                --click-name RunConfigButton --click 890,74 --click-name DebugButton --wait 10000 \
                --key "meta+shift+F12" --key "meta+shift+A" --type "Live Visual Tree" --key enter --wait 3000 \
                --click 994,363 --wait 2000 --hover 600,600 ;;
        agent)
            # agent.py in the Terminal's Agent tab debugs the app and clicks its button; the Live Visual Tree shows it.
            shot "$name" "$theme" --file src/Greenhouse.App/MainWindow.axaml --caret 16:1 \
                --agent "python3 -I \"$repo/tools/site-shots/agent.py\"" --key "meta+shift+F12" \
                --key "meta+shift+A" --type "Live Visual Tree" --key enter --wait 3000 --click 1020,387 --wait 2000 \
                --key "meta+control+1" --drag 700,444,700,404 --drag 1155,124,1155,148 --wait 1000 --hover 600,300 ;;
        themes)
            themes "$theme" ;;
        aspire)
            # The AppHost debugged, stopped in the API's handler while the sensors run on, beside the Aspire window.
            services_ready
            solution="$services/GreenhouseServices.slnx" shot "$name" "$theme" --file src/Greenhouse.Api/Program.cs \
                --caret 32:9 --key F9 --click 16,50 --drag 20,329,900,640 --click-name DebugButton --wait 30000 \
                --drag 600,600,585,600 --wait 500 --drag 815,640,778,640 --wait 500 --click 921,531 --wait 1500 \
                --hover 900,120 ;;
        godot-scene)
            # The game's C# beside its scene, whose tree Structure shows with each node's script.
            game_ready
            solution="$game" shot "$name" "$theme" --file Greenhouse.cs --caret 25:5 \
                --key "meta+shift+A" --type "Split Right" --key enter --wait 500 \
                --key "meta+shift+A" --type "Go to File" --key enter --type "main.tscn" --wait 800 --key enter --wait 1000 \
                --key "meta+shift+A" --type "Structure" --key enter --wait 2000 --drag 751,400,880,400 --wait 500 ;;
        godot-values)
            # The game run: its scripts' values at their lines as it plays.
            game_ready
            solution="$game" shot "$name" "$theme" --file Greenhouse.cs --caret 8:5 --click-name RunButton --wait 30000 \
                --key "meta+shift+F12" --wait 1000 --hover 600,640 ;;
        godot-live-tree)
            # The game run: its scene's nodes as they are in the Live tree, a plant chosen with its values.
            game_ready
            solution="$game" shot "$name" "$theme" --file plant.gd --caret 6:1 --click-name RunButton --wait 30000 \
                --key "meta+shift+F12" --key "meta+shift+A" --type "Live Visual Tree" --key enter --wait 4000 \
                --drag 859,400,740,400 --click 900,184 --key right --wait 1000 --key down --key down --key right --wait 1000 \
                --key down --key down --wait 2000 --hover 600,640 ;;
        godot-profile)
            # Profiled past its start: C#'s hot path open to Forecast, then GDScript's root, its heaviest opened at its line.
            game_ready
            solution="$game" shot "$name" "$theme" --file Greenhouse.cs --caret 1:1 --key "meta+shift+F12" \
                --click-name ProfileButton --wait 40000 --click 1002,497 --wait 15000 \
                --drag 700,444,700,160 --wait 1000 --drag 332,316,1147,316 --wait 4000 --click 600,470 \
                --key end --key up --key right --key down --key down --key down --key up --key up \
                --wait 500 --key enter --wait 2000 --hover 600,100 ;;
        blazor-wasm)
            # The Blazor Web App debugged: its WebAssembly page stopped in the browser at its breakpoint, its fields shown.
            web_app_ready
            solution="$web_app" shot "$name" "$theme" --file Greenhouse.Web.Client/Pages/Benches.razor \
                --caret 24:9 --key F9 --click-name DebugButton --wait 60000 --click 456,591 --wait 3000 --hover 200,420 ;;
        usages)
            # Show Usages of the plant's watering interval, every use in the solution in a popup at the caret.
            shot "$name" "$theme" --file src/Greenhouse.Core/Plant.cs --caret 7:20 \
                --key "meta+shift+A" --type "Show Usages" --key enter --wait 3000 ;;
        resx)
            # The app's strings as a table of keys, values and comments.
            shot "$name" "$theme" --file src/Greenhouse.App/Resources/Strings.resx --wait 2000 ;;
        gdscript)
            # Passenger's own reading of a GDScript, no Godot running: a member's completion, Godot's warning in Problems.
            more_ready
            solution="$more_game" shot "$name" "$theme" --file rain_gauge.gd --caret 21:27 --click 319,59 --key "meta+6" \
                --wait 1000 --drag 700,444,700,630 --wait 500 --key "meta+6" --key "control+SPACE" --wait 1500 ;;
        godot-shader)
            # A shader checked as Godot's compiler checks it, no Godot running: its error in Problems, TIME explained.
            more_ready
            solution="$more_game" shot "$name" "$theme" --file leaf.gdshader --caret 8:2 --click 319,59 --key "meta+6" \
                --wait 1000 --drag 700,444,700,606 --wait 500 --key "meta+6" --hover 280,235 --wait 1500 ;;
        godot-tests)
            # GUT's tests of the plants run in the Unit Tests window, the failed one chosen beside its code and its line.
            more_ready
            solution="$more_game" shot "$name" "$theme" --file test/test_plant.gd --caret 1:1 --click 319,59 \
                --key "meta+shift+A" --type "Unit Tests" --wait 800 --key enter --wait 3000 \
                --key "meta+shift+A" --type "Run All Tests" --wait 800 --key enter --wait 30000 \
                --double-click 300,645 --wait 1500 ;;
        aspire-metrics)
            # The AppHost run, the sensors asking the API each second: the Aspire window's Stats charting the API's metrics.
            services_ready
            solution="$services/GreenhouseServices.slnx" shot "$name" "$theme" --file src/Greenhouse.Api/Program.cs \
                --caret 31:9 --click 16,50 --click-name RunButton --wait 25000 --drag 700,444,700,200 --wait 500 \
                --drag 489,500,330,500 --wait 500 --click 82,281 --click 415,285 --wait 80000 --hover 200,600 ;;
        requests)
            # The AppHost run, the sensors asking the API each second: the Requests window, thirsty-plants marked N+1.
            services_ready
            solution="$services/GreenhouseServices.slnx" shot "$name" "$theme" --file src/Greenhouse.Api/Program.cs \
                --caret 31:9 --click 16,50 --click-name RunButton --wait 25000 --key "meta+shift+A" --type "Requests" \
                --wait 1000 --key enter --click 578,463 --drag 700,444,700,290 --wait 8000 --hover 900,120 ;;
    esac
}

# The godot-game scene, every theme in one sitting: Squash the Creeps playing in the Game window, Player.cs's members'
# values at their lines; of each theme's pictures, the one SITE_SHOTS_FRAME names (the seventh by default) is the site's.
windowed_game() {
    creeps_ready
    local state=/private/tmp/pss-game theme
    rm -rf "$state"
    python3 -I "$repo/tools/site-shots/game.py" "$passenger" "$creeps" "$state" "Squash the Creeps (3D)" Player.cs 11:5 \
        "$work/png/godot-game" "$@"
    # At the site's width: the window opens at its own size, which the accessibility API cannot change.
    for theme in "$@"; do
        sips --resampleWidth 2400 "$work/png/godot-game-$theme-${SITE_SHOTS_FRAME:-6}.png" \
            --out "$work/png/godot-game-$theme.png" > /dev/null
        encode "$work/png/godot-game-$theme.png" "$images/godot-game-$theme.webp"
    done
}

read -r -a themes <<< "${SITE_SHOTS_THEMES:-dark light}"
for scene_name in "${scenes[@]}"; do
    if [[ $scene_name == godot-game ]]; then
        windowed_game "${themes[@]}"
        continue
    fi
    if [[ $scene_name == html-preview ]]; then
        SITE_SHOTS_THEMES="${themes[*]}" "$repo/tools/site-shots/vm-shots.sh" "$images"
        continue
    fi
    for theme in "${themes[@]}"; do
        scene "$scene_name" "$theme"
    done
done
