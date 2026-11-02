[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramData "SnowShot\TableStructureRec")
)

$ErrorActionPreference = "Stop"
$serviceExecutable = Join-Path ([System.IO.Path]::GetFullPath($InstallRoot)) "TableStructureRecService.exe"
if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    throw "WinSW executable not found: $serviceExecutable"
}
& $serviceExecutable stop
if ($LASTEXITCODE -ne 0) {
    throw "Failed to stop TableStructureRecService."
}
