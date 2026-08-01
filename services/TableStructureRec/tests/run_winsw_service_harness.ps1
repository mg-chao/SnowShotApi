[CmdletBinding()]
param(
    [string]$WinSWVersion = "2.12.0",
    [string]$WinSWExpectedSha256 = "05B82D46AD331CC16BDC00DE5C6332C1EF818DF8CEEFCD49C726553209B3A0DA"
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "The WinSW service harness requires an elevated Windows runner."
}

$serviceId = "SnowShotWorkerHarness$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
$root = Join-Path ([System.IO.Path]::GetTempPath()) $serviceId
$state = Join-Path $root "state"
$serviceExecutable = Join-Path $root "$serviceId.exe"
$serviceConfiguration = Join-Path $root "$serviceId.xml"

try {
    New-Item -ItemType Directory -Force -Path $root, $state | Out-Null
    $certificate = Join-Path $root "server.pem"
    $privateKey = Join-Path $root "server-key.pem"
    $clientCa = Join-Path $root "client-ca.pem"
    Set-Content -LiteralPath $certificate -Value "service-harness" -Encoding ascii
    Set-Content -LiteralPath $privateKey -Value "service-harness" -Encoding ascii
    Set-Content -LiteralPath $clientCa -Value "service-harness" -Encoding ascii

    & (Join-Path $PSScriptRoot "..\scripts\Render-ServiceConfiguration.ps1") `
        -TemplatePath (Join-Path $PSScriptRoot "..\deployment\TableStructureRecService.xml") `
        -OutputPath $serviceConfiguration `
        -ListenHost "127.0.0.1" `
        -ServiceEnvironment "production" `
        -ServiceAccount "NT AUTHORITY\LocalService" `
        -TlsCertificate $certificate `
        -TlsPrivateKey $privateKey `
        -TlsClientCa $clientCa

    [xml]$configuration = Get-Content -LiteralPath $serviceConfiguration -Raw
    $configuration.service.id = $serviceId
    $configuration.service.name = $serviceId
    $configuration.service.description = "Ephemeral SnowShot WinSW lifecycle verification"
    $configuration.service.executable = "powershell.exe"
    $child = Join-Path $root "windows_service_child.ps1"
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "windows_service_child.ps1") -Destination $child
    $configuration.service.arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$child`" -StateDirectory `"$state`""
    $configuration.service.workingdirectory = $root
    $configuration.service.onfailure[0].delay = "1 sec"
    $configuration.service.onfailure[1].delay = "1 sec"
    $configuration.service.logpath = (Join-Path $root "logs")
    $configuration.Save($serviceConfiguration)
    New-Item -ItemType Directory -Force -Path (Join-Path $root "logs") | Out-Null

    Invoke-WebRequest `
        -Uri "https://github.com/winsw/winsw/releases/download/v$WinSWVersion/WinSW-x64.exe" `
        -OutFile $serviceExecutable
    $actualHash = (Get-FileHash -LiteralPath $serviceExecutable -Algorithm SHA256).Hash
    if ($actualHash -ne $WinSWExpectedSha256) {
        throw "WinSW SHA-256 mismatch. Expected $WinSWExpectedSha256, received $actualHash."
    }

    & icacls.exe $root "/grant:r" "NT AUTHORITY\LocalService:(OI)(CI)RX" "/T" "/C" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant LocalService read access." }
    & icacls.exe $state "/grant:r" "NT AUTHORITY\LocalService:(OI)(CI)M" "/T" "/C" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant LocalService state access." }
    & icacls.exe (Join-Path $root "logs") "/grant:r" "NT AUTHORITY\LocalService:(OI)(CI)M" "/T" "/C" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant LocalService log access." }

    & $serviceExecutable install
    if ($LASTEXITCODE -ne 0) { throw "WinSW service registration failed." }
    & $serviceExecutable start
    if ($LASTEXITCODE -ne 0) { throw "WinSW service start failed." }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    $readyPath = Join-Path $state "ready.txt"
    while (-not (Test-Path -LiteralPath $readyPath) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $readyPath)) {
        throw "WinSW did not restart the child after exit code 70."
    }
    $starts = [int](Get-Content -LiteralPath (Join-Path $state "starts.txt") -Raw)
    if ($starts -lt 2) { throw "Expected at least two child starts; received $starts." }
    $serviceSid = (Get-Content -LiteralPath (Join-Path $state "identity.txt") -Raw).Trim()
    if ($serviceSid -ne "S-1-5-19") {
        throw "WinSW child ran under unexpected SID: $serviceSid"
    }
    Write-Host "WinSW LocalService registration and exit-70 restart harness passed."
}
finally {
    $cleanupFailures = [System.Collections.Generic.List[string]]::new()
    $registeredService = Get-Service -Name $serviceId -ErrorAction SilentlyContinue
    if ($null -ne $registeredService) {
        & $serviceExecutable stop 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add("WinSW service stop failed.") }
        & $serviceExecutable uninstall 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add("WinSW service uninstall failed.") }

        $removalDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ($null -ne (Get-Service -Name $serviceId -ErrorAction SilentlyContinue) -and
            [DateTimeOffset]::UtcNow -lt $removalDeadline) {
            Start-Sleep -Milliseconds 250
        }
        if ($null -ne (Get-Service -Name $serviceId -ErrorAction SilentlyContinue)) {
            $cleanupFailures.Add("WinSW service remained registered after uninstall.")
        }
    }
    if (Test-Path -LiteralPath $root) {
        try {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
        }
        catch {
            $cleanupFailures.Add("Failed to remove WinSW harness directory '$root': $($_.Exception.Message)")
        }
    }
    if ($cleanupFailures.Count -ne 0) {
        throw "WinSW harness cleanup failed:`n$($cleanupFailures -join [Environment]::NewLine)"
    }
}
