[CmdletBinding()]
param(
    [string]$WorkerDnsName = "snowshot-latex-rec",
    [string]$WorkerLanIp = "192.168.0.203",
    [ValidateRange(1, 65535)]
    [int]$WorkerPort = 18081,
    [ValidateRange(30, 3650)]
    [int]$LeafCertificateDays = 825,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
    $OutputDirectory = Join-Path $repositoryRoot ".secrets\deployment\latex-communication"
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Output directory already exists; refusing to overwrite communication keys: $resolvedOutput"
}

$parsedIp = $null
if (-not [Net.IPAddress]::TryParse($WorkerLanIp, [ref]$parsedIp)) {
    throw "WorkerLanIp must be an IPv4 or IPv6 address; received '$WorkerLanIp'."
}
if ([string]::IsNullOrWhiteSpace($WorkerDnsName) -or $WorkerDnsName -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$') {
    throw "WorkerDnsName is not a valid DNS name: '$WorkerDnsName'."
}

function Find-OpenSsl {
    $command = Get-Command openssl -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $candidates = @(
        (Join-Path $env:ProgramFiles "Git\usr\bin\openssl.exe"),
        (Join-Path $env:ProgramFiles "OpenSSL-Win64\bin\openssl.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "OpenSSL 3.x is required. Install Git for Windows or OpenSSL and retry."
}

function Invoke-OpenSsl {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & $script:OpenSsl @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "OpenSSL failed with exit code $LASTEXITCODE while running: openssl $($Arguments -join ' ')"
    }
}

function New-RandomHex {
    param([ValidateRange(16, 128)][int]$Bytes = 32)
    $buffer = New-Object byte[] $Bytes
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($buffer) } finally { $generator.Dispose() }
    return ([BitConverter]::ToString($buffer)).Replace("-", "").ToLowerInvariant()
}

function Write-AsciiFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )
    [IO.File]::WriteAllText($Path, $Value, [Text.Encoding]::ASCII)
}

$script:OpenSsl = Find-OpenSsl
$authorityDirectory = Join-Path $resolvedOutput "authority"
$apiDirectory = Join-Path $resolvedOutput "api"
$workerDirectory = Join-Path $resolvedOutput "worker"
$stagingDirectory = Join-Path $resolvedOutput ".staging"
foreach ($directory in @($authorityDirectory, $apiDirectory, $workerDirectory, $stagingDirectory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$serverCaPasswordFile = Join-Path $authorityDirectory "worker-server-ca-password.txt"
$clientCaPasswordFile = Join-Path $authorityDirectory "api-client-ca-password.txt"
$clientPfxPasswordFile = Join-Path $apiDirectory "api-client-pfx-password.txt"
Write-AsciiFile $serverCaPasswordFile (New-RandomHex)
Write-AsciiFile $clientCaPasswordFile (New-RandomHex)
Write-AsciiFile $clientPfxPasswordFile (New-RandomHex)

$serverCaKey = Join-Path $authorityDirectory "worker-server-ca-key.pem"
$serverCaCertificate = Join-Path $authorityDirectory "worker-server-ca.pem"
$clientCaKey = Join-Path $authorityDirectory "api-client-ca-key.pem"
$clientCaCertificate = Join-Path $authorityDirectory "api-client-ca.pem"
$workerKey = Join-Path $workerDirectory "worker-server-key.pem"
$workerCertificate = Join-Path $workerDirectory "worker-server.pem"
$clientPfx = Join-Path $apiDirectory "api-client.pfx"

$workerCsr = Join-Path $stagingDirectory "worker-server.csr"
$clientKey = Join-Path $stagingDirectory "api-client-key.pem"
$clientCsr = Join-Path $stagingDirectory "api-client.csr"
$clientCertificate = Join-Path $stagingDirectory "api-client.pem"
$workerExtensions = Join-Path $stagingDirectory "worker-server.ext"
$clientExtensions = Join-Path $stagingDirectory "api-client.ext"

try {
    Invoke-OpenSsl @("genpkey", "-algorithm", "RSA", "-aes-256-cbc", "-pass", "file:$serverCaPasswordFile", "-pkeyopt", "rsa_keygen_bits:3072", "-out", $serverCaKey)
    Invoke-OpenSsl @("req", "-x509", "-new", "-sha256", "-days", "3650", "-key", $serverCaKey, "-passin", "file:$serverCaPasswordFile", "-subj", "/CN=SnowShot Latex Worker Server CA", "-addext", "basicConstraints=critical,CA:TRUE,pathlen:0", "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-out", $serverCaCertificate)

    Invoke-OpenSsl @("genpkey", "-algorithm", "RSA", "-aes-256-cbc", "-pass", "file:$clientCaPasswordFile", "-pkeyopt", "rsa_keygen_bits:3072", "-out", $clientCaKey)
    Invoke-OpenSsl @("req", "-x509", "-new", "-sha256", "-days", "3650", "-key", $clientCaKey, "-passin", "file:$clientCaPasswordFile", "-subj", "/CN=SnowShot API Client CA", "-addext", "basicConstraints=critical,CA:TRUE,pathlen:0", "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-out", $clientCaCertificate)

    Write-AsciiFile $workerExtensions @"
authorityKeyIdentifier=keyid,issuer
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:$WorkerDnsName,IP:$WorkerLanIp
"@
    Invoke-OpenSsl @("genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:3072", "-out", $workerKey)
    Invoke-OpenSsl @("req", "-new", "-sha256", "-key", $workerKey, "-subj", "/CN=$WorkerDnsName", "-out", $workerCsr)
    Invoke-OpenSsl @("x509", "-req", "-sha256", "-days", "$LeafCertificateDays", "-in", $workerCsr, "-CA", $serverCaCertificate, "-CAkey", $serverCaKey, "-passin", "file:$serverCaPasswordFile", "-CAcreateserial", "-extfile", $workerExtensions, "-out", $workerCertificate)

    Write-AsciiFile $clientExtensions @"
authorityKeyIdentifier=keyid,issuer
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature
extendedKeyUsage=clientAuth
"@
    Invoke-OpenSsl @("genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:3072", "-out", $clientKey)
    Invoke-OpenSsl @("req", "-new", "-sha256", "-key", $clientKey, "-subj", "/CN=snowshot-api@120.79.232.67", "-out", $clientCsr)
    Invoke-OpenSsl @("x509", "-req", "-sha256", "-days", "$LeafCertificateDays", "-in", $clientCsr, "-CA", $clientCaCertificate, "-CAkey", $clientCaKey, "-passin", "file:$clientCaPasswordFile", "-CAcreateserial", "-extfile", $clientExtensions, "-out", $clientCertificate)
    Invoke-OpenSsl @("pkcs12", "-export", "-out", $clientPfx, "-inkey", $clientKey, "-in", $clientCertificate, "-certfile", $clientCaCertificate, "-name", "snowshot-api-latex-client", "-passout", "file:$clientPfxPasswordFile")

    Copy-Item -LiteralPath $serverCaCertificate -Destination (Join-Path $apiDirectory "worker-server-ca.pem")
    Copy-Item -LiteralPath $clientCaCertificate -Destination (Join-Path $workerDirectory "api-client-ca.pem")

    $apiEnvironment = @"
Providers__Latex__BaseUrl=https://${WorkerDnsName}:$WorkerPort/
Providers__Latex__ClientCertificatePath=/run/secrets/latex-communication/api-client.pfx
Providers__Latex__ClientCertificatePassword=$([IO.File]::ReadAllText($clientPfxPasswordFile))
Providers__Latex__ServerCaCertificatePath=/run/secrets/latex-communication/worker-server-ca.pem
Providers__Latex__MaximumUploadBytes=819200
Providers__Latex__MaximumResponseBytes=2097152
"@
    Write-AsciiFile (Join-Path $apiDirectory "snowshot-api-latex.env") $apiEnvironment

    $workerEnvironment = @"
LATEX_REC_HOST=127.0.0.1
LATEX_REC_PORT=$WorkerPort
LATEX_REC_ENVIRONMENT=production
LATEX_REC_TLS_CERTIFICATE=C:\ProgramData\SnowShot\pki-latex\worker-server.pem
LATEX_REC_TLS_PRIVATE_KEY=C:\ProgramData\SnowShot\pki-latex\worker-server-key.pem
LATEX_REC_TLS_CLIENT_CA=C:\ProgramData\SnowShot\pki-latex\api-client-ca.pem
"@
    Write-AsciiFile (Join-Path $workerDirectory "latex-worker.env") $workerEnvironment

    $manifest = [ordered]@{
        apiHost = "120.79.232.67"
        workerLanIp = $WorkerLanIp
        workerDnsName = $WorkerDnsName
        workerPort = $WorkerPort
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        leafCertificateDays = $LeafCertificateDays
        apiFiles = @("api-client.pfx", "api-client-pfx-password.txt", "worker-server-ca.pem", "snowshot-api-latex.env")
        workerFiles = @("worker-server.pem", "worker-server-key.pem", "api-client-ca.pem", "latex-worker.env")
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resolvedOutput "manifest.json") -Encoding UTF8
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

Write-Host "Latex communication material created under $resolvedOutput"
Write-Host "Keep the authority directory offline. Copy only api/ to 120.79.232.67 and worker/ to 192.168.0.203."
