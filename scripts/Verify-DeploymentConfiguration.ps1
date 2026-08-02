param(
    [string]$ApplicationSettingsPath = "src/SnowShotApi/appsettings.json",
    [string]$NginxConfigurationPath = "deployment/nginx/snowshot.top.conf"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ApplicationSettingsPath -PathType Leaf)) {
    throw "Application settings file not found: $ApplicationSettingsPath"
}
if (-not (Test-Path -LiteralPath $NginxConfigurationPath -PathType Leaf)) {
    throw "nginx configuration file not found: $NginxConfigurationPath"
}

$settings = Get-Content -LiteralPath $ApplicationSettingsPath -Raw | ConvertFrom-Json
$nginx = Get-Content -LiteralPath $NginxConfigurationPath -Raw

$models = @($settings.Providers.Translation.LogicalModels)
$expectedModels = @("deepseek-v4-flash", "qwen-plus")
if (($models -join "`n") -ne ($expectedModels -join "`n")) {
    throw "Translation logical models must be ordered as deepseek-v4-flash, qwen-plus."
}

$translationDeadline = [int]$settings.Policy.Resources.translation.ExecutionDeadlineSeconds
$attemptTimeout = [int]$settings.Providers.Translation.AttemptTimeoutSeconds
if ($attemptTimeout -ge $translationDeadline) {
    throw "Translation attempt timeout must be less than the translation execution deadline."
}

$deadlines = @($settings.Policy.Resources.PSObject.Properties | ForEach-Object {
    [int]$_.Value.ExecutionDeadlineSeconds
})
$maximumDeadline = ($deadlines | Measure-Object -Maximum).Maximum

function Get-NginxTimeoutSeconds {
    param([Parameter(Mandatory)][string]$Directive)

    $match = [regex]::Match($nginx, "(?m)^\s*$([regex]::Escape($Directive))\s+(?<seconds>\d+)s;\s*$")
    if (-not $match.Success) {
        throw "nginx directive is missing or is not expressed in seconds: $Directive"
    }
    return [int]$match.Groups["seconds"].Value
}

$readTimeout = Get-NginxTimeoutSeconds "proxy_read_timeout"
$proxySendTimeout = Get-NginxTimeoutSeconds "proxy_send_timeout"
$clientSendTimeout = Get-NginxTimeoutSeconds "send_timeout"
$connectTimeout = Get-NginxTimeoutSeconds "proxy_connect_timeout"

if ($readTimeout -ne 310 -or $proxySendTimeout -ne 310 -or $clientSendTimeout -ne 310) {
    throw "nginx API read and send timeouts must all be 310 seconds."
}
if ($readTimeout -le $maximumDeadline) {
    throw "nginx proxy_read_timeout must exceed every application execution deadline."
}
if ($connectTimeout -ne 5) {
    throw "nginx proxy_connect_timeout must remain 5 seconds for the loopback API."
}
if ($nginx.Contains('$proxy_add_x_forwarded_for')) {
    throw "nginx must not trust or append a client-supplied X-Forwarded-For chain."
}
if (-not $nginx.Contains('proxy_set_header X-Forwarded-For $remote_addr;')) {
    throw "nginx must replace X-Forwarded-For with the directly observed client address."
}

Write-Host "Deployment configuration verified: models=$($models -join ','), app=${maximumDeadline}s, nginx=${readTimeout}s."
