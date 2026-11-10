# Before an upgrade or uninstall: Windows refuses to replace or delete the files of a Passenger still running.
$ErrorActionPreference = 'Stop'

$app = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) 'Passenger'
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith("$app\", [StringComparison]::OrdinalIgnoreCase)
}
if ($running) {
    $names = ($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', '
    throw "Passenger is running from $app ($names): close it, then run choco again"
}
