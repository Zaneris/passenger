#!/bin/sh
# A pull request giving a package manager's repository a release's files, as the release workflow opens for Homebrew
# and WinGet: `tools/package-pr.sh UPSTREAM BRANCH TITLE FOLDER FILE...` puts each FILE into FOLDER on BRANCH of the
# fork of UPSTREAM (Homebrew/homebrew-cask) that GH_TOKEN's account has, made if it has none, and opens the request
# from it. All through GitHub's API by gh, cloning neither repository; run again, it writes the branch anew.
set -eu
. "$(dirname "$0")/release-common.sh"
[ $# -ge 5 ] || fail "usage: package-pr.sh UPSTREAM BRANCH TITLE FOLDER FILE..."
upstream=$1
branch=$2
title=$3
folder=$4
shift 4

user=$(gh api user --jq .login)
fork=$user/${upstream#*/}
gh api "repos/$fork" --silent 2> /dev/null || gh repo fork "$upstream" --clone=false
# A fork just made answers a moment later.
tries=0
until gh api "repos/$fork" --silent 2> /dev/null; do
    tries=$((tries + 1))
    [ "$tries" -lt 20 ] || fail "$fork, the fork of $upstream, never came"
    sleep 3
done

# The branch starts at the upstream's newest commit, which a fork can name without being brought up to date.
base=$(gh api "repos/$upstream" --jq .default_branch)
commit=$(gh api "repos/$upstream/git/ref/heads/$base" --jq .object.sha)
gh api "repos/$fork/git/refs" -f "ref=refs/heads/$branch" -f "sha=$commit" --silent 2> /dev/null ||
    gh api -X PATCH "repos/$fork/git/refs/heads/$branch" -f "sha=$commit" -F force=true --silent

for file in "$@"; do
    [ -f "$file" ] || fail "no $file"
    path=$folder/$(basename "$file")
    # A file already there (a cask's earlier version) is replaced by naming what it holds.
    held=$(gh api "repos/$fork/contents/$path?ref=$branch" --jq .sha 2> /dev/null || true)
    # shellcheck disable=SC2086 # the one optional argument
    base64 < "$file" | tr -d '\n' | gh api -X PUT "repos/$fork/contents/$path" -f "message=$title" -f "branch=$branch" \
        -F content=@- ${held:+-f "sha=$held"} --silent
    echo "$fork@$branch: $path"
done

open=$(gh pr list --repo "$upstream" --head "$branch" --author "$user" --state open --json url --jq '.[0].url // empty')
if [ -n "$open" ]; then
    echo "Already open: $open"
else
    gh pr create --repo "$upstream" --base "$base" --head "$user:$branch" --title "$title" \
        --body "Opened by Passenger's release workflow from the release's own files: https://github.com/${GITHUB_REPOSITORY:-Zaneris/passenger}/releases"
fi
