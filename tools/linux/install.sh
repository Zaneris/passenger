#!/bin/sh
# Installs Passenger from the passenger/ folder this script is in (an unpacked Passenger-linux-<arch>.tar.gz), or
# removes it again:
#   ./install.sh                         for you alone: ~/.local/opt/passenger, ~/.local/bin/passenger, and Passenger in
#                                        your applications menu with its icon and file types (~/.local/share)
#   sudo ./install.sh --system           for everyone: /opt/passenger, /usr/local/bin/passenger, /usr/share/applications
#   ./install.sh --uninstall [--system]  removes what that install added, and leaves the settings (~/.local/share/Passenger)
# Installing a newer release's folder the same way replaces the old files and keeps the settings.
set -eu

app=io.github.zaneris.passenger

fail() {
    echo "install.sh: $*" >&2
    exit 1
}

usage() {
    sed -n '2,8s/^# \{0,1\}//p' "$0"
}

system=
uninstall=
for arg in "$@"; do
    case $arg in
        --system) system=1 ;;
        --uninstall) uninstall=1 ;;
        -h | --help)
            usage
            exit 0
            ;;
        *) fail "'$arg' is neither --system, --uninstall nor --help" ;;
    esac
done

here=$(dirname "$(readlink -f "$0")")
if [ -n "$system" ]; then
    [ "$(id -u)" = 0 ] || fail "--system installs for everyone, under /opt and /usr: run it with sudo"
    dest=/opt/passenger
    bin=/usr/local/bin
    data=/usr/share
else
    if [ "$(id -u)" = 0 ] && [ -n "${SUDO_USER:-}" ]; then
        fail "under sudo this would install for root alone: run it as yourself, or add --system to install for everyone"
    fi
    [ -n "${HOME:-}" ] || fail "HOME is not set, so there is no home to install into"
    dest=$HOME/.local/opt/passenger
    bin=$HOME/.local/bin
    data=${XDG_DATA_HOME:-$HOME/.local/share}
fi
link=$bin/passenger
entry=$data/applications/$app.desktop
icon_png=$data/icons/hicolor/256x256/apps/$app.png
icon_svg=$data/icons/hicolor/scalable/apps/$app.svg
types=$data/mime/packages/$app.xml

# A folder Passenger was installed to, or unpacked as: never anything else of the same name, which is left alone.
is_passenger() {
    [ -x "$1/app/Passenger" ] && [ -f "$1/install.sh" ] && [ -f "$1/$app.desktop" ]
}

# The desktop's caches of entries, file types and icons, where the system has the tools: a cache it has no tool for
# is one it does not keep.
refresh() {
    if command -v update-desktop-database > /dev/null 2>&1 && [ -d "$data/applications" ]; then
        update-desktop-database -q "$data/applications" || true
    fi
    if command -v update-mime-database > /dev/null 2>&1 && [ -d "$data/mime/packages" ]; then
        update-mime-database "$data/mime" > /dev/null 2>&1 || true
    fi
    if command -v gtk-update-icon-cache > /dev/null 2>&1 && [ -f "$data/icons/hicolor/icon-theme.cache" ]; then
        gtk-update-icon-cache -q -t -f "$data/icons/hicolor" || true
    fi
}

# On the way out of the swap below: the installed folder put back until the new one is in place, only then removed.
settle() {
    if [ -z "$swapped" ] && [ -e "$old" ]; then
        if [ -e "$dest" ] || ! mv "$old" "$dest"; then
            echo "install.sh: the Passenger installed before is left in $old" >&2
        fi
    fi
    rm -rf "$new" || true
    if [ -n "$swapped" ]; then
        rm -rf "$old" || true
    fi
}

if [ -n "$uninstall" ]; then
    if [ -e "$dest" ] && ! is_passenger "$dest"; then
        fail "$dest is not a Passenger install, so it is left as it is"
    fi
    # The link only when it leads to this install, the menu entry and the rest by their names, which are Passenger's.
    if [ -L "$link" ] && [ "$(readlink "$link")" = "$dest/passenger" ]; then
        rm -f "$link"
    fi
    rm -f "$entry" "$icon_png" "$icon_svg" "$types"
    refresh
    # The folder last: this script may be the installed copy, which the shell goes on reading once it is gone.
    rm -rf "$dest"
    if [ -n "$system" ]; then
        echo "Passenger is uninstalled. Each person's settings stay in their ~/.local/share/Passenger."
    else
        echo "Passenger is uninstalled. Your settings stay in $data/Passenger; delete that folder to remove them too."
    fi
    exit 0
fi

is_passenger "$here" || fail "$here holds no Passenger: run the install.sh of an unpacked Passenger-linux-*.tar.gz"
# The menu entry names the launcher in a quoted string, in which the desktop entry spec gives these characters meanings.
case $dest in
    *[\"\`\$\\%]*) fail "$dest has a character a desktop entry cannot name (\" \` \$ \\ %)" ;;
esac
# The command's link goes only where nothing is or a link leads to a Passenger: another program of its name stays.
if [ -L "$link" ]; then
    to=$(readlink "$link")
    led=$(readlink -f "$link" || true)
    if [ "$to" != "$dest/passenger" ] && ! { [ "${led##*/}" = passenger ] && is_passenger "${led%/*}"; }; then
        fail "$link is there, a link to $to, which is not Passenger: move it away first"
    fi
elif [ -d "$link" ]; then
    fail "$link is there, a folder: move it away first"
elif [ -x "$link" ]; then
    fail "$link is there, another program of that name: move it away first"
elif [ -e "$link" ]; then
    fail "$link is there, a file: move it away first"
fi

# The new files beside the old, then swapped in, so a Passenger still running keeps the files it has open.
if [ "$here" != "$dest" ]; then
    if [ -e "$dest" ] && ! is_passenger "$dest"; then
        fail "$dest is there and is not a Passenger install: move it away first"
    fi
    mkdir -p "$(dirname "$dest")"
    new=$dest.new-$$
    old=$dest.old-$$
    swapped=
    trap settle EXIT
    trap 'exit 1' HUP INT TERM
    rm -rf "$new"
    cp -R "$here" "$new"
    if [ -n "$system" ]; then
        chmod -R u+rwX,go+rX,go-w "$new"
    fi
    if [ -e "$dest" ]; then
        mv "$dest" "$old"
    fi
    mv "$new" "$dest"
    swapped=1
    rm -rf "$old"
fi

mkdir -p "$bin" "$(dirname "$entry")" "$(dirname "$icon_png")" "$(dirname "$icon_svg")" "$(dirname "$types")"
ln -sfn "$dest/passenger" "$link"
# The template's Exec and TryExec name the launcher on the PATH; the installed entry names this install's.
while IFS= read -r line; do
    case $line in
        Exec=*) printf 'Exec="%s" %%f\n' "$dest/passenger" ;;
        TryExec=*) printf 'TryExec=%s\n' "$dest/passenger" ;;
        *) printf '%s\n' "$line" ;;
    esac
done < "$dest/$app.desktop" > "$entry"
cp "$dest/$app.png" "$icon_png"
cp "$dest/$app.svg" "$icon_svg"
cp "$dest/$app.xml" "$types"
if [ -n "$system" ]; then
    chmod 644 "$entry" "$icon_png" "$icon_svg" "$types"
fi
refresh

echo "Passenger is installed in $dest, and is in the applications menu."
case ":${PATH:-}:" in
    *":$bin:"*) echo "Start it from a shell with: passenger [folder or solution]" ;;
    *) echo "$bin is not on your PATH yet: add it, then start Passenger from a shell with: passenger [folder or solution]" ;;
esac

# What Passenger needs and this system lacks, said now, not as a window that never opens (ldconfig may be in /sbin).
missing=
ldconfig=$(command -v ldconfig || echo /sbin/ldconfig)
if [ -x "$ldconfig" ]; then
    known=$("$ldconfig" -p 2> /dev/null || true)
    for library in libX11.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1; do
        case $known in
            *"$library "*) ;;
            *) missing="$missing $library" ;;
        esac
    done
fi
if [ -n "$missing" ]; then
    echo "Passenger's window needs$missing, which this system lacks: see https://passenger-ide.net/linux" >&2
fi
if ! command -v dotnet > /dev/null 2>&1 && [ -z "${DOTNET_ROOT:-}" ]; then
    echo "No .NET SDK on the PATH: Passenger needs version 10 or later to build your projects and run its language server." >&2
fi
if ! command -v git > /dev/null 2>&1; then
    echo "Git is not installed: Passenger's version control needs it." >&2
fi
