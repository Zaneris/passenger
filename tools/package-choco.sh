#!/bin/sh
# Passenger's Chocolatey package: `just package-choco [VERSION] [SHA256SUMS]`, on Windows with choco, makes
# artifacts/package/passenger-ide.<version>.nupkg from tools/chocolatey. The package downloads the release's
# Passenger-windows-x64.zip or -arm64.zip (tools/package-win.sh) when installed, so it is made for a release: the
# version is the tag's (or Directory.Build.props'), and the zips' SHA-256s come from that release's SHA256SUMS on GitHub,
# or from the file given (a Release dry run's). Pushing it is the owner's:
#   choco push artifacts/package/passenger-ide.<version>.nupkg --source https://push.chocolatey.org/
set -eu
. "$(dirname "$0")/release-common.sh"
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
root=$(cd "$(dirname "$0")/.." && pwd)
if command -v cygpath >/dev/null 2>&1; then
    root=$(cygpath -m "$root")
fi
command -v choco >/dev/null 2>&1 || fail "choco is not on the PATH: the package is made on Windows with Chocolatey installed"

version=${1:-$(release_version "$root")}
echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || fail "the version '$version' is not X.Y.Z in numbers"
out="$root/artifacts/package"
work="$out/chocolatey"
rm -rf "$work"
mkdir -p "$work"

sums="$work/SHA256SUMS"
if [ $# -ge 2 ]; then
    [ -f "$2" ] || fail "'$2' is not a file"
    cp "$2" "$sums"
else
    curl -fsSL -o "$sums" "https://github.com/Zaneris/passenger/releases/download/v$version/SHA256SUMS" \
        || fail "the release v$version on GitHub gives no SHA256SUMS: publish it first, or give a dry run's SHA256SUMS"
fi
sha256_of() {
    sum=$(awk -v name="$1" '$2 == name || $2 == "*" name { print $1 }' "$sums")
    echo "$sum" | grep -Eq '^[0-9a-f]{64}$' || fail "SHA256SUMS gives no SHA-256 for $1"
    echo "$sum"
}
x64=$(sha256_of Passenger-windows-x64.zip)
arm64=$(sha256_of Passenger-windows-arm64.zip)

cp -R "$root/tools/chocolatey/." "$work/"
for file in "$work/passenger-ide.nuspec" "$work"/tools/*.ps1; do
    sed -e "s/@VERSION@/$version/g" -e "s/@SHA256_X64@/$x64/g" -e "s/@SHA256_ARM64@/$arm64/g" "$file" > "$file.filled"
    mv "$file.filled" "$file"
done
rm "$sums"
if grep -rq '@[A-Z0-9_]*@' "$work"; then
    fail "a placeholder is left in $work: $(grep -rl '@[A-Z0-9_]*@' "$work" | tr '\n' ' ')"
fi

rm -f "$out/passenger-ide.$version.nupkg"
(cd "$work" && choco pack passenger-ide.nuspec --out "$out" --limit-output)
[ -f "$out/passenger-ide.$version.nupkg" ] || fail "choco pack wrote no $out/passenger-ide.$version.nupkg"
echo "package: $out/passenger-ide.$version.nupkg"
