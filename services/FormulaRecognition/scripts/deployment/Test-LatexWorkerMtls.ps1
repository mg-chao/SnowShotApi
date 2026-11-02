[CmdletBinding()]
param(
    [string]$BaseUrl = "https://snowshot-latex-rec:18081/",
    [string]$PfxPath,
    [string]$PfxPasswordFile,
    [string]$ServerCaPath,
    [ValidateRange(1, 60)]
    [int]$ConnectTimeoutSeconds = 5
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$apiDirectory = Join-Path $repositoryRoot ".secrets\deployment\latex-communication\api"
if ([string]::IsNullOrWhiteSpace($PfxPath)) { $PfxPath = Join-Path $apiDirectory "api-client.pfx" }
if ([string]::IsNullOrWhiteSpace($PfxPasswordFile)) { $PfxPasswordFile = Join-Path $apiDirectory "api-client-pfx-password.txt" }
if ([string]::IsNullOrWhiteSpace($ServerCaPath)) { $ServerCaPath = Join-Path $apiDirectory "worker-server-ca.pem" }

foreach ($path in @($PfxPath, $PfxPasswordFile, $ServerCaPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required mTLS file does not exist: $path" }
}

$openSsl = Get-Command openssl -ErrorAction SilentlyContinue
if ($null -eq $openSsl) {
    $candidate = Join-Path $env:ProgramFiles "Git\usr\bin\openssl.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "OpenSSL is required for the mTLS probe." }
    $openSslPath = $candidate
}
else { $openSslPath = $openSsl.Source }

$uri = [Uri]::new($BaseUrl)
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "https") { throw "BaseUrl must be an absolute HTTPS URL." }
$port = if ($uri.IsDefaultPort) { 443 } else { $uri.Port }
$tcpClient = [Net.Sockets.TcpClient]::new()
try {
    $connectTask = $tcpClient.ConnectAsync($uri.Host, $port)
    if (-not $connectTask.Wait([TimeSpan]::FromSeconds($ConnectTimeoutSeconds)) -or -not $tcpClient.Connected) {
        throw "TCP connection to $($uri.Host):$port timed out after $ConnectTimeoutSeconds seconds."
    }
}
finally { $tcpClient.Dispose() }

$parsedHost = $null
$verificationArgument = if ([Net.IPAddress]::TryParse($uri.Host, [ref]$parsedHost)) { "-verify_ip" } else { "-verify_hostname" }
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("snowshot-mtls-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
$clientCertificate = Join-Path $temporaryDirectory "client.pem"
$clientKey = Join-Path $temporaryDirectory "client-key.pem"

try {
    & $openSslPath pkcs12 -in $PfxPath -clcerts -nokeys -out $clientCertificate -passin "file:$PfxPasswordFile"
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract the client certificate from the PFX." }
    & $openSslPath pkcs12 -in $PfxPath -nocerts -nodes -out $clientKey -passin "file:$PfxPasswordFile"
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract the client key from the PFX." }

    $httpRequest = "GET /health/ready HTTP/1.1`r`nHost: $($uri.Host)`r`nConnection: close`r`n`r`n"
    $probeOutput = $httpRequest | & $openSslPath s_client `
        -connect "$($uri.Host):$port" `
        -servername $uri.Host `
        -cert $clientCertificate `
        -key $clientKey `
        -CAfile $ServerCaPath `
        $verificationArgument $uri.Host `
        -verify_return_error `
        -quiet 2>&1
    $probeText = $probeOutput -join [Environment]::NewLine
    if ($probeText -notmatch 'HTTP/1\.[01] 200' -or $probeText -notmatch '"status"\s*:\s*"ready"') {
        throw "The latex worker mTLS probe did not return ready.`n$probeText"
    }
    Write-Host "Latex worker mTLS and readiness probe succeeded: $BaseUrl"
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
