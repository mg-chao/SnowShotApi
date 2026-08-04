param(
    [string]$ApplicationSettingsPath = "src/SnowShotApi/appsettings.json",
    [string]$NginxConfigurationPath = "deployment/nginx/snowshot.top.conf",
    [string]$RestorePolicyPath = "deployment/policy/policy-revision-6.json"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ApplicationSettingsPath -PathType Leaf)) {
    throw "Application settings file not found: $ApplicationSettingsPath"
}
if (-not (Test-Path -LiteralPath $NginxConfigurationPath -PathType Leaf)) {
    throw "nginx configuration file not found: $NginxConfigurationPath"
}
if (-not (Test-Path -LiteralPath $RestorePolicyPath -PathType Leaf)) {
    throw "Restore policy file not found: $RestorePolicyPath"
}

$settings = Get-Content -LiteralPath $ApplicationSettingsPath -Raw | ConvertFrom-Json
$nginx = Get-Content -LiteralPath $NginxConfigurationPath -Raw
$restore = Get-Content -LiteralPath $RestorePolicyPath -Raw | ConvertFrom-Json

if ([long]$settings.Policy.PrincipalDailyAllowanceNanoYuan -ne 6000000000) {
    throw "The temporary per-user daily allowance must be 6000000000 NanoYuan (6 yuan)."
}
if ([long]$settings.Policy.DailyOperatorBudgetNanoYuan -ne 100000000000) {
    throw "The temporary daily operator budget must be 100000000000 NanoYuan (100 yuan)."
}
$operatorMaximums = @($settings.Policy.Resources.PSObject.Properties | ForEach-Object {
    [long]$_.Value.OperatorMaximumNanoYuan
})
if ($operatorMaximums.Count -eq 0 -or @($operatorMaximums | Where-Object { $_ -ne 30000000 }).Count -ne 0) {
    throw "Every resource OperatorMaximumNanoYuan must be exactly 30000000 (0.03 yuan)."
}
if ([long]$restore.Policy.Revision -ne 6 -or
    [long]$restore.Policy.PrincipalDailyAllowanceNanoYuan -ne 3000000000 -or
    [long]$restore.Policy.DailyOperatorBudgetNanoYuan -ne 50000000000 -or
    [long]$restore.Policy.MonthlyOperatorBudgetNanoYuan -ne 500000000000) {
    throw "Revision 6 must restore the 3 yuan user allowance and 50 yuan daily operator budget while preserving the 500 yuan monthly budget."
}
$restoreMaximums = @($restore.Policy.Resources.PSObject.Properties | ForEach-Object {
    [long]$_.Value.OperatorMaximumNanoYuan
})
if ($restoreMaximums.Count -ne $operatorMaximums.Count -or
    @($restoreMaximums | Where-Object { $_ -ne 30000000 }).Count -ne 0) {
    throw "Revision 6 must preserve every resource OperatorMaximumNanoYuan at 30000000."
}

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
if (-not [regex]::IsMatch($nginx, '(?m)^\s*client_max_body_size\s+1m;\s*$')) {
    throw "nginx table extraction requests must be limited to 1m."
}
if ($nginx.Contains('$proxy_add_x_forwarded_for')) {
    throw "nginx must not trust or append a client-supplied X-Forwarded-For chain."
}
if (-not $nginx.Contains('proxy_set_header X-Forwarded-For $remote_addr;')) {
    throw "nginx must replace X-Forwarded-For with the directly observed client address."
}
foreach ($healthPath in @("live", "ready")) {
    if (-not [regex]::IsMatch($nginx, "(?m)^\s*location\s+=\s+/health/$healthPath\s*\{")) {
        throw "nginx must expose an exact /health/$healthPath proxy location."
    }
}
if ([regex]::IsMatch($nginx, "(?m)^\s*location\s+[^\r\n]*health/components")) {
    throw "nginx must not expose /health/components publicly."
}

Write-Host "Deployment configuration verified: models=$($models -join ','), app=${maximumDeadline}s, nginx=${readTimeout}s."
