# What chocolateyInstall.ps1 put outside the package's folder; the folder itself goes with the package. The person's
# settings and state, in %LocalAppData%\Passenger, stay.
$ErrorActionPreference = 'Stop'

Uninstall-BinFile -Name 'passenger-ide'

$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Passenger.lnk'
if (Test-Path $shortcut) {
    Remove-Item $shortcut -Force
}
