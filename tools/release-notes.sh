#!/bin/sh
# A release's notes, for `gh release create --notes-file`: `tools/release-notes.sh [vX.Y.Z]` writes to standard output
# the version's own notes, docs/release-notes/X.Y.Z.md, so the release and the app's What's New say the same; the
# version is the tag's, else VERSION's (a dry run from a branch). A version with none takes an annotated tag's message
# when it says more than its name, else the first line of each commit since the previous vX.Y.Z tag (every commit, for
# the first release), cut to GitHub's 125,000 characters with a link to the rest. Notes still the stand-in (their
# "<!-- Stand-in" line) fail a release PUBLISH=true publishes, and only warn otherwise. A security release's notes carry
# the line "<!-- Security release -->" and a Security section ("## Security"), whose first line, what it fixes, every
# Passenger's update notice says (ReleaseNotes.SecurityOf); notes so marked without that line fail.
set -eu
fail() {
    echo "release-notes: $*" >&2
    exit 1
}
tag=${1:-}
root=$(cd "$(dirname "$0")/.." && pwd)
repository=${GITHUB_REPOSITORY:-Zaneris/passenger}

if [ -n "$tag" ]; then
    git -C "$root" rev-parse -q --verify "refs/tags/$tag" > /dev/null || fail "there is no tag $tag"
    # The tag, even where a branch has the same name.
    head=refs/tags/$tag
else
    head=HEAD
fi

version=${tag#v}
[ -n "$version" ] || version=${VERSION:-}
notes=$root/docs/release-notes/$version.md
if [ -n "$version" ] && [ -f "$notes" ]; then
    if grep -q '^<!-- Stand-in' "$notes"; then
        [ "${PUBLISH:-}" != true ] || fail "docs/release-notes/$version.md is still the stand-in: write the notes before publishing $version"
        echo "release-notes: docs/release-notes/$version.md is still the stand-in, which a published release refuses" >&2
    fi
    if grep -q '^[[:space:]]*<!-- Security release -->[[:space:]]*$' "$notes"; then
        awk '/^#+[[:space:]]+[Ss]ecurity[[:space:]]*$/ { inside = 1; next } inside && NF { found = !/^#/; exit } END { exit !found }' "$notes" \
            || fail "docs/release-notes/$version.md is marked a security release, but no line follows its Security section's heading to say what it fixes"
    fi
    cat "$notes"
    exit 0
fi

if [ "$(git -C "$root" rev-parse --is-shallow-repository)" = true ]; then
    fail "a shallow clone has neither every tag nor every commit since the last; fetch the whole history"
fi

# A signed tag's signature is not its message, and "v1.2.0" or "1.2.0" alone says nothing the release does not.
if [ -n "$tag" ] && [ "$(git -C "$root" cat-file -t "$head")" = tag ]; then
    message=$(git -C "$root" for-each-ref --format='%(contents:subject)%0a%0a%(contents:body)' "$head")
    if [ -n "$message" ] && [ "$message" != "$tag" ] && [ "$message" != "${tag#v}" ]; then
        printf '%s\n' "$message"
        exit 0
    fi
fi

previous=$(git -C "$root" describe --tags --abbrev=0 --match 'v[0-9]*' "$head^" 2> /dev/null || true)
if [ -n "$previous" ]; then
    range=$previous..$head
    rest=https://github.com/$repository/compare/$previous...${tag:-$(git -C "$root" rev-parse HEAD)}
    echo "Changes since $previous:"
else
    range=$head
    rest=https://github.com/$repository/commits/${tag:-$(git -C "$root" rev-parse HEAD)}
    echo "Changes in the first release:"
fi
echo
git -C "$root" log --no-merges --format='- %s' "$range" | awk -v max=100000 -v rest="$rest" '
    { total += length($0) + 1 }
    total <= max { print; next }
    { left++ }
    END { if (left) printf "- and %d more: %s\n", left, rest }'
