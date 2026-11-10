# Passenger's Chocolatey install (made by tools/package-choco.sh, which fills in the version and the SHA-256s): the
# release's zip for this machine's processor, checked against the SHA-256 the release's SHA256SUMS gives it, unpacked
# into the package's folder with the marker that tells Passenger Chocolatey updates it (Updater.PackageMarker).
$ErrorActionPreference = 'Stop'

$version = '@VERSION@'
$checksums = @{ x64 = '@SHA256_X64@'; arm64 = '@SHA256_ARM64@' }
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$app = Join-Path $toolsDir 'Passenger'
$exe = Join-Path $app 'Passenger.exe'

# choco runs as an x64 process on Arm64 Windows too, so the processor is the system's word, not the process's.
$native = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
$arch = switch ($native) {
    'AMD64' { 'x64' }
    'ARM64' { 'arm64' }
    default { throw "Passenger is built for x64 and Arm64 Windows, not $native" }
}

# What an earlier version unpacked goes first, so no file it had and this one lacks stays behind.
if (Test-Path $app) {
    Remove-Item $app -Recurse -Force
}

$zip = @{
    PackageName    = $env:ChocolateyPackageName
    UnzipLocation  = $app
    Url64bit       = "https://github.com/Zaneris/passenger/releases/download/v$version/Passenger-windows-$arch.zip"
    Checksum64     = $checksums[$arch]
    ChecksumType64 = 'sha256'
}
Install-ChocolateyZipPackage @zip

if (-not (Test-Path $exe)) {
    throw "Passenger-windows-$arch.zip holds no Passenger.exe at its root"
}

# Passenger reads its first line as the manager, its second as the command that updates it.
[IO.File]::WriteAllText((Join-Path $app 'package-manager'), "Chocolatey`nchoco upgrade $env:ChocolateyPackageName`n")

# Only Passenger.exe gets a command, as passenger-ide (the Linux one's name), started without waiting for it.
Get-ChildItem $app -Recurse -Filter '*.exe' | ForEach-Object {
    New-Item -ItemType File -Force -Path "$($_.FullName).ignore" | Out-Null
}
Install-BinFile -Name 'passenger-ide' -Path $exe -UseStart

$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Passenger.lnk'
Install-ChocolateyShortcut -ShortcutFilePath $shortcut -TargetPath $exe -IconLocation $exe -WorkingDirectory $app
