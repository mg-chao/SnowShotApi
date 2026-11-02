# Copy to Run-ApiTestReverseTunnel.ps1 and configure your SSH host and account.
[CmdletBinding()]
param(
    [string]$SshHost = "api.example.test",
    [string]$SshUser = "test-tunnel",
    [string]$PrivateKey = "",
    [string]$LogPath = "",
    [ValidateRange(1, 65535)][int]$RemotePort = 15101,
    [ValidateRange(1, 65535)][int]$LocalPort = 5101
)

$ErrorActionPreference = "Stop"
$sshExecutable = (Get-Command ssh.exe -ErrorAction Stop).Source
if (-not $PrivateKey) { $PrivateKey = Join-Path $PSScriptRoot "../.secrets/test-api-tunnel/id_ed25519" }
if (-not $LogPath) { $LogPath = Join-Path $PSScriptRoot "../.runlogs/test-api-tunnel.log" }
$PrivateKey = [IO.Path]::GetFullPath($PrivateKey)
$LogPath = [IO.Path]::GetFullPath($LogPath)
if (-not (Test-Path -LiteralPath $PrivateKey -PathType Leaf)) {
    throw "The restricted test tunnel key has not been provisioned: $PrivateKey"
}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($LogPath))

while ($true) {
    if ((Test-Path -LiteralPath $LogPath) -and (Get-Item -LiteralPath $LogPath).Length -ge 10MB) {
        Move-Item -LiteralPath $LogPath -Destination "$LogPath.1" -Force
    }
    Add-Content -LiteralPath $LogPath -Value "$([DateTimeOffset]::Now.ToString('O')) connecting $SshHost port $RemotePort to local port $LocalPort"
    try {
        & $sshExecutable -N -T -i $PrivateKey -o IdentitiesOnly=yes -o BatchMode=yes `
            -o StrictHostKeyChecking=yes -o ExitOnForwardFailure=yes -o ConnectTimeout=15 `
            -o ServerAliveInterval=15 -o ServerAliveCountMax=3 `
            -R "127.0.0.1:${RemotePort}:127.0.0.1:${LocalPort}" "${SshUser}@${SshHost}" *>> $LogPath
        Add-Content -LiteralPath $LogPath -Value "$([DateTimeOffset]::Now.ToString('O')) ssh exited with code $LASTEXITCODE; retrying in 5 seconds"
    }
    catch {
        Add-Content -LiteralPath $LogPath -Value "$([DateTimeOffset]::Now.ToString('O')) tunnel error: $($_.Exception.Message); retrying in 5 seconds"
    }
    Start-Sleep -Seconds 5
}
