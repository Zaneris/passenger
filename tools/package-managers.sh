#!/bin/sh
# What each system's package manager is told of a release: `tools/package-managers.sh VERSION SHA256SUMS OUT` writes
# Homebrew's cask (OUT/homebrew/passenger-ide.rb), WinGet's manifests (OUT/winget/) and the AUR's recipe
# (OUT/aur/PKGBUILD and .SRCINFO) for the release's own files, by the names and sums the release carries: nothing is
# built again. Homebrew's and WinGet's install the disk image and the installer, so that copy updates itself; the AUR's
# is pacman's, said by app/package-manager. The release workflow publishes them (tools/package-pr.sh, the AUR by git).
set -eu
. "$(dirname "$0")/release-common.sh"
[ $# = 3 ] || fail "usage: package-managers.sh VERSION SHA256SUMS OUT"
version=$1
sums=$2
out=$3
echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || fail "the version '$version' is not X.Y.Z in numbers"
[ -f "$sums" ] || fail "no $sums"

repository=https://github.com/Zaneris/passenger
files=$repository/releases/download/v$version
site=https://passenger-ide.net
described='IDE for C# and .NET'
winget=Zaneris.Passenger

# Prints a release file's SHA-256 as SHA256SUMS says it.
sum_of() {
    sum=$(awk -v file="$1" '$2 == file || $2 == "*" file { print $1 }' "$sums")
    echo "$sum" | grep -Eq '^[0-9a-f]{64}$' || fail "$sums has no SHA-256 for $1"
    echo "$sum"
}
upper() {
    echo "$1" | tr 'a-f' 'A-F'
}

rm -rf "$out"
mkdir -p "$out/homebrew" "$out/winget" "$out/aur"

# Homebrew's `passenger` is Phusion Passenger, so the cask is passenger-ide; macOS 13 is the app's LSMinimumSystemVersion.
cat > "$out/homebrew/passenger-ide.rb" << END
cask "passenger-ide" do
  arch arm: "arm64", intel: "x64"

  version "$version"
  sha256 arm:   "$(sum_of Passenger-macos-arm64.dmg)",
         intel: "$(sum_of Passenger-macos-x64.dmg)"

  url "$repository/releases/download/v#{version}/Passenger-macos-#{arch}.dmg",
      verified: "github.com/Zaneris/passenger/"
  name "Passenger"
  desc "$described"
  homepage "$site/"

  livecheck do
    url :url
    strategy :github_latest
  end

  auto_updates true
  depends_on macos: :ventura

  app "Passenger.app"

  zap trash: "~/Library/Application Support/Passenger"
end
END

# Velopack's Setup.exe: for the person alone, quiet by --silent, listed in Apps under its pack id (package-win.sh).
manifest=1.9.0
cat > "$out/winget/$winget.yaml" << END
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.$manifest.schema.json
PackageIdentifier: $winget
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifest
END
cat > "$out/winget/$winget.installer.yaml" << END
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.$manifest.schema.json
PackageIdentifier: $winget
PackageVersion: $version
InstallerType: exe
Scope: user
InstallModes:
- interactive
- silent
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
UpgradeBehavior: install
ProductCode: PassengerIDE
Installers:
- Architecture: x64
  InstallerUrl: $files/Passenger-windows-x64.exe
  InstallerSha256: $(upper "$(sum_of Passenger-windows-x64.exe)")
- Architecture: arm64
  InstallerUrl: $files/Passenger-windows-arm64.exe
  InstallerSha256: $(upper "$(sum_of Passenger-windows-arm64.exe)")
ManifestType: installer
ManifestVersion: $manifest
END
cat > "$out/winget/$winget.locale.en-US.yaml" << END
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.$manifest.schema.json
PackageIdentifier: $winget
PackageVersion: $version
PackageLocale: en-US
Publisher: Passenger
PublisherUrl: $site
PublisherSupportUrl: $repository/issues
PackageName: Passenger
PackageUrl: $site
License: MIT
LicenseUrl: $repository/blob/v$version/LICENSE
ShortDescription: An open-source IDE for C# and .NET
Moniker: passenger-ide
Tags:
- csharp
- debugger
- dotnet
- ide
- roslyn
ReleaseNotesUrl: $repository/releases/tag/v$version
ManifestType: defaultLocale
ManifestVersion: $manifest
END

# The AUR's recipe and what makepkg --printsrcinfo would say of it, written from one list each so they agree. It lays
# the archive out as site/linux.html's Packaging Passenger says, the libraries those the page names.
name=passenger-ide-bin
depends='fontconfig git libice libsm libx11 ttf-font'
optional='dotnet-sdk: build and run .NET projects (one installed by hand works too)
nodejs: the web languages
webkit2gtk-4.1: the HTML preview'
x64=$(sum_of Passenger-linux-x64.tar.gz)
arm64=$(sum_of Passenger-linux-arm64.tar.gz)
# The words of a list quoted for a PKGBUILD's array, one line to a word (depends) or to a line (optional).
quoted() {
    sed "s/.*/'&'/" | tr '\n' ' ' | sed 's/ $//'
}
cat > "$out/aur/PKGBUILD" << END
# Written by Passenger's release ($repository, tools/package-managers.sh): change it there.
pkgname=$name
pkgver=$version
pkgrel=1
pkgdesc='$described'
arch=('x86_64' 'aarch64')
url='$site'
license=('MIT')
depends=($(echo "$depends" | tr ' ' '\n' | quoted))
optdepends=($(echo "$optional" | quoted))
provides=('passenger-ide')
conflicts=('passenger-ide')
options=('!strip' '!debug')
source_x86_64=("Passenger-\$pkgver-linux-x64.tar.gz::$repository/releases/download/v\$pkgver/Passenger-linux-x64.tar.gz")
source_aarch64=("Passenger-\$pkgver-linux-arm64.tar.gz::$repository/releases/download/v\$pkgver/Passenger-linux-arm64.tar.gz")
sha256sums_x86_64=('$x64')
sha256sums_aarch64=('$arm64')

package() {
    local shipped="\$srcdir/passenger" app=io.github.zaneris.passenger
    install -d "\$pkgdir/opt/passenger-ide" "\$pkgdir/usr/bin"
    cp -a "\$shipped/app" "\$pkgdir/opt/passenger-ide/"
    install -m755 "\$shipped/passenger" "\$pkgdir/opt/passenger-ide/passenger"
    echo pacman > "\$pkgdir/opt/passenger-ide/app/package-manager"
    ln -s /opt/passenger-ide/passenger "\$pkgdir/usr/bin/passenger-ide"
    install -Dm644 "\$shipped/\$app.desktop" "\$pkgdir/usr/share/applications/\$app.desktop"
    install -Dm644 "\$shipped/\$app.png" "\$pkgdir/usr/share/icons/hicolor/256x256/apps/\$app.png"
    install -Dm644 "\$shipped/\$app.svg" "\$pkgdir/usr/share/icons/hicolor/scalable/apps/\$app.svg"
    install -Dm644 "\$shipped/\$app.xml" "\$pkgdir/usr/share/mime/packages/\$app.xml"
    install -Dm644 "\$shipped/app/LICENSE.txt" "\$pkgdir/usr/share/licenses/\$pkgname/LICENSE.txt"
    install -Dm644 "\$shipped/app/THIRD-PARTY-NOTICES.txt" "\$pkgdir/usr/share/licenses/\$pkgname/THIRD-PARTY-NOTICES.txt"
}
END
{
    echo "pkgbase = $name"
    {
        echo "pkgdesc = $described"
        echo "pkgver = $version"
        echo "pkgrel = 1"
        echo "url = $site"
        echo "arch = x86_64"
        echo "arch = aarch64"
        echo "license = MIT"
        echo "$depends" | tr ' ' '\n' | sed 's/^/depends = /'
        echo "$optional" | sed 's/^/optdepends = /'
        echo "provides = passenger-ide"
        echo "conflicts = passenger-ide"
        echo "options = !strip"
        echo "options = !debug"
        echo "source_x86_64 = Passenger-$version-linux-x64.tar.gz::$files/Passenger-linux-x64.tar.gz"
        echo "sha256sums_x86_64 = $x64"
        echo "source_aarch64 = Passenger-$version-linux-arm64.tar.gz::$files/Passenger-linux-arm64.tar.gz"
        echo "sha256sums_aarch64 = $arm64"
    } | sed 's/^/	/'
    echo
    echo "pkgname = $name"
} > "$out/aur/.SRCINFO"

# A recipe bash can't read fails here, not at the AUR.
bash -n "$out/aur/PKGBUILD" || fail "the PKGBUILD written is not bash"
find "$out" -type f | LC_ALL=C sort
