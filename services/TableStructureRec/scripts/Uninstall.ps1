[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramData "SnowShot\TableStructureRec")
)

$ErrorActionPreference = "Stop"
$resolvedRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$serviceExecutable = Join-Path $resolvedRoot "TableStructureRecService.exe"
if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    throw "WinSW executable not found: $serviceExecutable"
}

& $serviceExecutable stop
& $serviceExecutable uninstall
if ($LASTEXITCODE -ne 0) {
    throw "Failed to unregister TableStructureRecService."
}

Write-Host "Service registration removed. Models, venv, logs, and service files remain in $resolvedRoot"
