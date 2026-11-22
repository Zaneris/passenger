#!/bin/sh
# The Vim that VimTests compares Vim mode with, built from Vim's own release at the tag below into
# artifacts/vim/<version>/, where the test finds it: plain features, no GUI, no X, nothing installed outside the checkout.
# It needs a C compiler, make, curl and ncurses' headers.
#   tools/vim.sh
set -eu
. "$(dirname "$0")/release-common.sh"
cd "$(checkout_root)"

version=9.2.1382
out="$PWD/artifacts/vim/$version"
if [ -x "$out/bin/vim" ]; then
    echo "$out/bin/vim is built"
    exit 0
fi
source="$PWD/artifacts/vim/source-$version"
rm -rf "$source"
mkdir -p "$source"
curl -fsSL "https://github.com/vim/vim/archive/refs/tags/v$version.tar.gz" | tar -xz -C "$source" --strip-components=1
(
    cd "$source"
    ./configure --prefix="$out" --with-features=normal --disable-gui --without-x --disable-nls --with-tlib=ncurses >/dev/null
    make -j"$(getconf _NPROCESSORS_ONLN)" >/dev/null
    make install >/dev/null
)
rm -rf "$source"
"$out/bin/vim" --version | head -3
