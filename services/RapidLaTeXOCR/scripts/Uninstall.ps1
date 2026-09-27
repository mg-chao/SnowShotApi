[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramData "SnowShot\RapidLaTeXOCR")
)

$ErrorActionPreference = "Stop"
$resolvedRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$serviceExecutable = Join-Path $resolvedRoot "RapidLaTeXOCRService.exe"
if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    throw "WinSW executable not found: $serviceExecutable"
}

& $serviceExecutable stop
& $serviceExecutable uninstall
if ($LASTEXITCODE -ne 0) {
    throw "Failed to unregister RapidLaTeXOCRService."
}

Write-Host "Service registration removed. Models, venv, logs, and service files remain in $resolvedRoot"
