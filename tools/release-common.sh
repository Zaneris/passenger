# What the release scripts share, sourced by each (package-mac.sh, package-win.sh, package-linux.sh, container-tools.sh)
# and by `just publish` after their `set -eu`: a failure said under the script's name, the version a release is, and the
# App's Release publish. A function given the checkout's root takes it first.

# Says why on stderr, under the name of the script that sourced this ($0 without .sh), and ends it.
fail() {
    echo "$(basename "$0" .sh): $*" >&2
    exit 1
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

# The App self-contained and ReadyToRun: publish_app ROOT RID VERSION [more of dotnet publish's arguments]. Only the
# packages the lock files pin, and no MSBuild node left running once it is done.
publish_app() {
    publish_root=$1
    publish_rid=$2
    publish_version=$3
    shift 3
    dotnet publish "$publish_root/src/Passenger.App" -c Release -r "$publish_rid" --self-contained -p:PublishReadyToRun=true \
        -p:Version="$publish_version" -p:RestoreLockedMode=true -nodeReuse:false "$@"
}
