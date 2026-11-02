[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SshExecutable,
    [Parameter(Mandatory = $true)]
    [string]$PrivateKey,
    [Parameter(Mandatory = $true)]
    [string]$KnownHosts,
    [Parameter(Mandatory = $true)]
    [string]$PublicHost,
    [Parameter(Mandatory = $true)]
    [string]$PublicUser,
    [ValidateRange(1, 65535)]
    [int]$RemotePort = 18080,
    [ValidateRange(1, 65535)]
    [int]$LocalPort = 18080,
    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = "Stop"
$retrySeconds = 1
$maximumLogBytes = 10MB

while ($true) {
    try {
        if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            $log = Get-Item -LiteralPath $LogPath
            if ($log.Length -ge $maximumLogBytes) {
                $previous = "$LogPath.1"
                Remove-Item -LiteralPath $previous -Force -ErrorAction SilentlyContinue
                Move-Item -LiteralPath $LogPath -Destination $previous
            }
        }

        $listener = Get-NetTCPConnection -LocalAddress "127.0.0.1" -LocalPort $LocalPort `
            -State Listen -ErrorAction SilentlyContinue
        if ($null -eq $listener) {
            Add-Content -LiteralPath $LogPath -Encoding UTF8 `
                -Value "$([DateTimeOffset]::Now.ToString('O')) local worker 127.0.0.1:$LocalPort is not listening; retrying"
        }
        else {
            Add-Content -LiteralPath $LogPath -Encoding UTF8 `
                -Value "$([DateTimeOffset]::Now.ToString('O')) starting reverse SSH tunnel"
            $sshArguments = @(
                "-N", "-T",
                "-i", $PrivateKey,
                "-o", "IdentitiesOnly=yes",
                "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=yes",
                "-o", "UserKnownHostsFile=$KnownHosts",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "ConnectTimeout=15",
                "-o", "ServerAliveInterval=30",
                "-o", "ServerAliveCountMax=3",
                "-R", "127.0.0.1:${RemotePort}:127.0.0.1:${LocalPort}",
                "${PublicUser}@${PublicHost}"
            )
            & $SshExecutable @sshArguments *>> $LogPath
            $exitCode = $LASTEXITCODE
            Add-Content -LiteralPath $LogPath -Encoding UTF8 `
                -Value "$([DateTimeOffset]::Now.ToString('O')) ssh exited with code $exitCode; retrying in $retrySeconds second(s)"
        }
    }
    catch {
        Add-Content -LiteralPath $LogPath -Encoding UTF8 `
            -Value "$([DateTimeOffset]::Now.ToString('O')) tunnel runner error: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds $retrySeconds
    $retrySeconds = [Math]::Min(60, $retrySeconds * 2)
}
