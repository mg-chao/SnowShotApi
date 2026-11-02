[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,

    [string]$PythonExecutable = "py"
)

$ErrorActionPreference = "Stop"
$resolvedRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$venvPath = Join-Path $resolvedRoot "venv"
$venvPython = Join-Path $venvPath "Scripts\python.exe"
$requirements = Join-Path $resolvedRoot "requirements-windows.lock"

if (-not (Test-Path -LiteralPath $requirements -PathType Leaf)) {
    throw "Requirements file not found: $requirements"
}

if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    if ($PythonExecutable -eq "py") {
        & py -3.12 -m venv $venvPath
    }
    else {
        & $PythonExecutable -m venv $venvPath
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create the Python 3.12 virtual environment."
    }
}

$pythonVersion = & $venvPython -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
if ($LASTEXITCODE -ne 0 -or $pythonVersion.Trim() -ne "3.12") {
    throw "TableStructureRec requires Python 3.12; found $pythonVersion."
}

& $venvPython -m pip install --disable-pip-version-check --require-hashes -r $requirements
if ($LASTEXITCODE -ne 0) {
    throw "Failed to install TableStructureRec requirements."
}

$installedPackages = & $venvPython -m pip list --format=json | ConvertFrom-Json
$ortPackages = $installedPackages |
    Where-Object { $_.name -in @("onnxruntime", "onnxruntime-directml") }
$baseOrt = $ortPackages | Where-Object { $_.name -eq "onnxruntime" }
$directMlOrt = $ortPackages | Where-Object { $_.name -eq "onnxruntime-directml" }
if ($baseOrt -or -not $directMlOrt) {
    throw "The environment must contain only onnxruntime-directml, never base onnxruntime."
}

Write-Host "Python environment ready at $venvPath"
