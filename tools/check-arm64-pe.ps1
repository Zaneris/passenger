# The Windows apphost and libraries actually shipped, rather than the RID their publish was asked for.
param([Parameter(Mandatory)][string[]] $Paths)
$ErrorActionPreference = 'Stop'
foreach ($path in $Paths) {
    $file = [IO.File]::OpenRead((Resolve-Path -LiteralPath $path).Path)
    $reader = [IO.BinaryReader]::new($file)
    try {
        $magic = $reader.ReadUInt32()
        if (($magic -band 0xffff) -eq 0x5a4d) {
            $file.Position = 0x3c
            $header = $reader.ReadUInt32()
            $file.Position = $header
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0xaa64) {
                throw "$path is not an ARM64 PE image"
            }
            "ARM64 PE: $path"
        } else {
            throw "$path is not a PE image"
        }
    } finally {
        $reader.Dispose()
    }
}
