[CmdletBinding()]
param(
    [string]$PublicHost = "120.79.232.67",
    [string]$PublicUser = "root",
    [ValidateRange(1, 65535)]
    [int]$RemotePort = 18080,
    [ValidateRange(1, 65535)]
    [int]$LocalPort = 18080,
    [string]$SshDirectory = "C:\ProgramData\SnowShot\ssh",
    [string]$PublicHostKeyFile,
    [string]$TaskName = "SnowShotTableTunnel"
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Install-TableReverseTunnel.ps1 requires an elevated PowerShell session."
}
if ([string]::IsNullOrWhiteSpace($PublicHostKeyFile) -or
    -not (Test-Path -LiteralPath $PublicHostKeyFile -PathType Leaf)) {
    throw "PublicHostKeyFile must contain the trusted SSH host public key from the API server."
}

$sshExecutable = (Get-Command ssh.exe -ErrorAction Stop).Source
New-Item -ItemType Directory -Force -Path $SshDirectory | Out-Null
$privateKey = Join-Path $SshDirectory "table-tunnel-ed25519"
$publicKey = "$privateKey.pub"
$knownHosts = Join-Path $SshDirectory "known_hosts"
$runner = Join-Path $SshDirectory "Run-TableReverseTunnel.ps1"
$logPath = Join-Path $SshDirectory "table-tunnel.log"
if (-not (Test-Path -LiteralPath $privateKey -PathType Leaf)) {
    & ssh-keygen.exe -q -t ed25519 -N "" -C "snowshot-table-tunnel" -f $privateKey
    if ($LASTEXITCODE -ne 0) { throw "Failed to generate the table tunnel SSH key." }
}

$hostKeyParts = ([IO.File]::ReadAllText($PublicHostKeyFile).Trim() -split '\s+')
if ($hostKeyParts.Length -lt 2 -or $hostKeyParts[0] -notmatch '^ssh-') {
    throw "The API server SSH host public key is invalid."
}
[IO.File]::WriteAllText($knownHosts, "$PublicHost $($hostKeyParts[0]) $($hostKeyParts[1])`n", [Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Run-TableReverseTunnel.ps1") -Destination $runner -Force

foreach ($path in @($privateKey, $publicKey, $knownHosts, $runner)) {
    & icacls.exe $path /setowner "NT AUTHORITY\SYSTEM" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set the SSH tunnel file owner: $path" }
    & icacls.exe $path /inheritance:r | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to disable inherited SSH tunnel file permissions: $path" }
    & icacls.exe $path /remove:g $identity.Name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to remove the deployment user from the SSH tunnel file: $path" }
    & icacls.exe $path /grant:r "NT AUTHORITY\SYSTEM:F" "BUILTIN\Administrators:F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to secure SSH tunnel file permissions: $path" }
}

$powerShell = (Get-Command powershell.exe -ErrorAction Stop).Source
$arguments = @(
    "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
    "-File", ('"' + $runner + '"'),
    "-SshExecutable", ('"' + $sshExecutable + '"'),
    "-PrivateKey", ('"' + $privateKey + '"'),
    "-KnownHosts", ('"' + $knownHosts + '"'),
    "-PublicHost", ('"' + $PublicHost + '"'),
    "-PublicUser", ('"' + $PublicUser + '"'),
    "-RemotePort", $RemotePort,
    "-LocalPort", $LocalPort,
    "-LogPath", ('"' + $logPath + '"')
) -join " "
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 `
    -RestartInterval ([TimeSpan]::FromMinutes(1)) `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew
$principalSettings = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principalSettings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 3
$task = Get-ScheduledTask -TaskName $TaskName
if ($task.State -ne "Running") {
    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    throw "The SSH tunnel runner is not persistent (state '$($task.State)', result '$($info.LastTaskResult)'). See $logPath."
}

Write-Host "Reverse SSH tunnel task installed. Public key to authorize: $publicKey. Log: $logPath"
