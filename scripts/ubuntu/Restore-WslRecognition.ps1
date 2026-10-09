[CmdletBinding()]
param(
    [string]$UbuntuWorkerHost = 'chao@192.168.0.203',
    [string]$Distribution = 'SnowShot-Ubuntu-LTS'
)
$ErrorActionPreference = 'Stop'
# Confirm the rollback workers can start before releasing production forwarding.
& wsl.exe -d $Distribution -u root --exec systemctl start snowshot-table-cpu snowshot-latex-cpu
if ($LASTEXITCODE -ne 0) { throw 'Could not start the retained WSL workers; Ubuntu Server forwarding remains active.' }
foreach ($workerService in @('snowshot-table-cpu', 'snowshot-latex-cpu')) {
    & wsl.exe -d $Distribution -u root --exec systemctl is-active --quiet $workerService
    if ($LASTEXITCODE -ne 0) { throw "The retained $workerService worker is not active; Ubuntu Server forwarding remains active." }
}
& ssh.exe -t $UbuntuWorkerHost sudo systemctl disable --now snowshot-cpu-tunnel@table snowshot-cpu-tunnel@latex
if ($LASTEXITCODE -ne 0) { throw 'Could not release the Ubuntu Server tunnels.' }
& wsl.exe -d $Distribution -u root --exec systemctl enable --now snowshot-table-cpu snowshot-latex-cpu snowshot-recognition-tunnel@table snowshot-recognition-tunnel@latex
if ($LASTEXITCODE -ne 0) { throw 'Could not restore the WSL workers and tunnels.' }
$startupTask = Get-ScheduledTask -TaskName SnowShotUbuntuWorkers -ErrorAction SilentlyContinue
if ($null -ne $startupTask) {
    Enable-ScheduledTask -TaskName SnowShotUbuntuWorkers | Out-Null
    Start-ScheduledTask -TaskName SnowShotUbuntuWorkers
}
Write-Host 'WSL production forwarding restored. Verify both worker components on the API host.'
