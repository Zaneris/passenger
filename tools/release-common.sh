# What the release scripts and the recipes beside them share, sourced by each (package-mac.sh, package-win.sh,
# package-linux.sh, container-tools.sh, debug-relay.sh, release-notes.sh, app-icon.sh) and by `just publish` after their
# `set -eu`: a failure said under the script's name, the checkout's root, the version a release is, a project's Release
# publish, and the check of a portable copy. A function given the checkout's root takes it first.

# Says why on stderr, under the name of the script that sourced this ($0 without .sh), and ends it.
fail() {
    echo "$(basename "$0" .sh): $*" >&2
    exit 1
}

# Prints the checkout's root, the folder above the sourcing script's own, taken as root=$(checkout_root).
checkout_root() {
    cd "$(dirname "$0")/.." && pwd
}

# Prints the version, taken as version=$(release_version ROOT): a vX.Y.Z tag on the commit, else Directory.Build.props'
# Version; anything but X.Y.Z in numbers fails.
release_version() {
    version=$(git -C "$1" describe --tags --exact-match --match 'v[0-9]*' 2>/dev/null | sed 's/^v//' || true)
    [ -n "$version" ] || version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$1/Directory.Build.props" | head -n 1)
    if ! echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
        fail "the version '$version' is not X.Y.Z in numbers (a vX.Y.Z tag on the commit, or Version in Directory.Build.props)"
    fi
    echo "$version"
}

# A project of src/ self-contained for one system: publish_project ROOT PROJECT RID VERSION [more of dotnet publish's
# arguments]. Only the packages the lock files pin, and no MSBuild node left running once it is done.
publish_project() {
    publish_root=$1
    publish_path=$2
    publish_rid=$3
    publish_version=$4
    shift 4
    AVALONIA_TELEMETRY_OPTOUT=1 dotnet publish "$publish_root/src/$publish_path" -c Release -r "$publish_rid" --self-contained \
        -p:Version="$publish_version" -p:RestoreLockedMode=true -nodeReuse:false "$@"
}

# DuckDB's engine's file for a system, as its driver's package names it: duckdb_engine=$(duckdb_engine_of RID).
duckdb_engine_of() {
    case $1 in
        win-*) echo duckdb.dll ;;
        osx-*) echo libduckdb.dylib ;;
        *) echo libduckdb.so ;;
    esac
}

# DuckDB's engine taken out of an app's folder of files (APP), which no release carries, into the folder TO, for a release
# file of its own that Passenger downloads the first time a DuckDB connection opens: take_duckdb APP ENGINE TO.
take_duckdb() {
    [ -f "$1/$2" ] || fail "the app has no $2, DuckDB's engine, in $1"
    mkdir -p "$3"
    mv "$1/$2" "$3/$2"
}

# The App, ReadyToRun: publish_app ROOT RID VERSION [more of dotnet publish's arguments].
publish_app() {
    publish_root=$1
    shift
    publish_project "$publish_root" Passenger.App "$@" -p:PublishReadyToRun=true
}

# A portable copy as a person makes one of a build unpacked: check_portable PROGRAM COPY runs the copy's PROGRAM as a
# person's window with nobody at it (--headless) on an empty folder, a data folder made in the copy's folder COPY and its
# HOME a fresh one, until its log names the folder it opened, two minutes at most, then ends it with its input. It fails
# unless that log is in data, the HOME is still empty, and on Windows, whose folders follow no HOME, the local
# application data's Passenger is as it was. What it made goes after, its data folder too.
check_portable() {
    portable_program=$1
    portable_data="$2/data"
    portable_work=$(mktemp -d "${TMPDIR:-/tmp}/passenger-portable.XXXXXX")
    portable_installed="$portable_work/none/Passenger"
    # Git's sh on Windows: paths a Windows program reads as given.
    if command -v cygpath > /dev/null 2>&1; then
        portable_work=$(cygpath -m "$portable_work")
        portable_installed="$(cygpath -m "$LOCALAPPDATA")/Passenger"
    fi
    portable_log="$portable_data/logs/passenger.log"
    [ ! -e "$portable_data" ] || fail "$portable_data is there before the portable copy's check"
    mkdir -p "$portable_data" "$portable_work/home" "$portable_work/opened"
    portable_before=$(find "$portable_installed" 2>/dev/null | LC_ALL=C sort || true)
    {
        tries=0
        until grep -q '] Opened ' "$portable_log" 2>/dev/null || [ "$tries" -ge 240 ]; do
            tries=$((tries + 1))
            sleep 0.5
        done
    } | env -u PASSENGER_SETTINGS -u XDG_CONFIG_HOME -u XDG_DATA_HOME -u XDG_CACHE_HOME HOME="$portable_work/home" \
        "$portable_program" --headless "$portable_work/opened" > "$portable_work/run.log" 2>&1 \
        || { cat "$portable_work/run.log" >&2; fail "the portable copy $portable_program failed its run"; }
    grep -q '] Opened ' "$portable_log" 2>/dev/null \
        || { cat "$portable_work/run.log" >&2; fail "the portable copy $portable_program kept no log in its data folder, $portable_data"; }
    portable_written=$(find "$portable_work/home" -mindepth 1 | head -n 5)
    [ -z "$portable_written" ] || fail "the portable copy wrote in its HOME: $portable_written"
    [ "$(find "$portable_installed" 2>/dev/null | LC_ALL=C sort || true)" = "$portable_before" ] \
        || fail "the portable copy wrote in the local application data's state, $portable_installed"
    rm -rf "$portable_data" "$portable_work"
    echo "$(basename "$0" .sh): $portable_program ran as a portable copy, its state in its data folder and nothing written outside"
}
