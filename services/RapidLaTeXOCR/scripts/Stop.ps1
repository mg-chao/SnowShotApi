[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramData "SnowShot\RapidLaTeXOCR")
)

$ErrorActionPreference = "Stop"
$serviceExecutable = Join-Path ([System.IO.Path]::GetFullPath($InstallRoot)) "RapidLaTeXOCRService.exe"
if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    throw "WinSW executable not found: $serviceExecutable"
}
& $serviceExecutable stop
if ($LASTEXITCODE -ne 0) {
    throw "Failed to stop RapidLaTeXOCRService."
}
