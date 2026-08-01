[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramData "SnowShot\TableStructureRec"),
    [string]$PythonExecutable = "py",
    [string]$WinSWVersion = "2.12.0",
    [string]$WinSWExpectedSha256 = "05B82D46AD331CC16BDC00DE5C6332C1EF818DF8CEEFCD49C726553209B3A0DA",
    [Parameter(Mandatory = $true)]
    [string]$ListenHost,
    [ValidateRange(1, 65535)]
    [int]$ListenPort = 18080,
    [ValidateSet("development", "staging", "production")]
    [string]$ServiceEnvironment = "production",
    [string]$ServiceAccount = "NT AUTHORITY\LocalService",
    [string]$TlsCertificate,
    [string]$TlsPrivateKey,
    [string]$TlsClientCa
)

$ErrorActionPreference = "Stop"
$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$serviceId = "TableStructureRecService"
$serviceExecutable = Join-Path $resolvedRoot "$serviceId.exe"
$serviceConfig = Join-Path $resolvedRoot "$serviceId.xml"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run Install.ps1 from an elevated PowerShell session."
}

New-Item -ItemType Directory -Force -Path $resolvedRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedRoot "models") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedRoot "logs") | Out-Null

$sourceDirectories = @(
    "table_rec_service",
    "table_cls",
    "wired_table_rec",
    "lineless_table_rec"
)
foreach ($sourceDirectory in $sourceDirectories) {
    $sourcePath = Join-Path $sourceRoot $sourceDirectory
    $destinationPath = Join-Path $resolvedRoot $sourceDirectory
    New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
    Get-ChildItem -LiteralPath $sourcePath -Force | Copy-Item `
        -Destination $destinationPath -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $sourceRoot "requirements-windows.txt") `
    -Destination $resolvedRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "requirements-windows.lock") `
    -Destination $resolvedRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "model-manifest.json") `
    -Destination $resolvedRoot -Force
& (Join-Path $PSScriptRoot "Render-ServiceConfiguration.ps1") `
    -TemplatePath (Join-Path $sourceRoot "deployment\TableStructureRecService.xml") `
    -OutputPath $serviceConfig `
    -ListenHost $ListenHost `
    -ListenPort $ListenPort `
    -ServiceEnvironment $ServiceEnvironment `
    -ServiceAccount $ServiceAccount `
    -TlsCertificate $TlsCertificate `
    -TlsPrivateKey $TlsPrivateKey `
    -TlsClientCa $TlsClientCa

if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    $downloadPath = "$serviceExecutable.download"
    $winSwUrl = "https://github.com/winsw/winsw/releases/download/v$WinSWVersion/WinSW-x64.exe"
    Invoke-WebRequest -Uri $winSwUrl -OutFile $downloadPath
    Move-Item -LiteralPath $downloadPath -Destination $serviceExecutable -Force
}
$actualWinSWHash = (Get-FileHash -LiteralPath $serviceExecutable -Algorithm SHA256).Hash
if ($actualWinSWHash -ne $WinSWExpectedSha256) {
    throw "WinSW SHA-256 mismatch. Expected $WinSWExpectedSha256, received $actualWinSWHash."
}

& (Join-Path $PSScriptRoot "Bootstrap.ps1") `
    -InstallRoot $resolvedRoot `
    -PythonExecutable $PythonExecutable

$venvPython = Join-Path $resolvedRoot "venv\Scripts\python.exe"
Push-Location $resolvedRoot
try {
    & $venvPython -m table_rec_service.prefetch `
        --model-dir (Join-Path $resolvedRoot "models")
    $prefetchExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($prefetchExitCode -ne 0) {
    throw "Model prefetch or inference preflight failed."
}

function Grant-ServiceAccess {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Permission,
        [switch]$Recursive
    )

    $arguments = @($Path, "/grant:r", "${ServiceAccount}:$Permission")
    if ($Recursive) { $arguments += @("/T", "/C") }
    & icacls.exe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to grant $Permission access on $Path to $ServiceAccount."
    }
}

Grant-ServiceAccess -Path $resolvedRoot -Permission "(OI)(CI)RX" -Recursive
Grant-ServiceAccess -Path (Join-Path $resolvedRoot "logs") -Permission "(OI)(CI)M" -Recursive
foreach ($tlsPath in @($TlsCertificate, $TlsPrivateKey, $TlsClientCa)) {
    if (-not [string]::IsNullOrWhiteSpace($tlsPath)) {
        Grant-ServiceAccess -Path ([System.IO.Path]::GetFullPath($tlsPath)) -Permission "R"
    }
}

& $serviceExecutable install
if ($LASTEXITCODE -ne 0) {
    throw "WinSW service registration failed."
}
& $serviceExecutable start
if ($LASTEXITCODE -ne 0) {
    throw "WinSW service start failed."
}

Write-Host "TableStructureRec installed and started from $resolvedRoot"
