param(
    [string]$ApplicationSettingsPath = "src/SnowShotApi/appsettings.json",
    [string]$NginxConfigurationPath = "deployment/nginx/snowshot.top.conf",
    [string]$RestorePolicyPath = "deployment/policy/policy-revision-15.json",
    [Nullable[long]]$ExpectedPolicyRevision,
    [string]$PolicyReferencePath = "$PSScriptRoot/../deployment/policy/policy-revision-17.json",
    [string]$RestorePolicyReferencePath = "$PSScriptRoot/../deployment/policy/policy-revision-15.json"
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
foreach ($path in @($PolicyReferencePath, $RestorePolicyReferencePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Policy reference file not found: $path"
    }
}

$settings = Get-Content -LiteralPath $ApplicationSettingsPath -Raw | ConvertFrom-Json
$nginx = Get-Content -LiteralPath $NginxConfigurationPath -Raw
$restore = Get-Content -LiteralPath $RestorePolicyPath -Raw | ConvertFrom-Json
$policyReference = Get-Content -LiteralPath $PolicyReferencePath -Raw | ConvertFrom-Json
$restoreReference = Get-Content -LiteralPath $RestorePolicyReferencePath -Raw | ConvertFrom-Json

$referenceRevision = [long]$policyReference.Policy.Revision
if ($null -eq $ExpectedPolicyRevision) { $ExpectedPolicyRevision = $referenceRevision }
if ($ExpectedPolicyRevision -lt $referenceRevision) {
    throw "The expected revision must be at least the policy reference revision ($referenceRevision)."
}
if ([long]$settings.Policy.Revision -ne $ExpectedPolicyRevision) {
    throw "The candidate must use the expected revision ($ExpectedPolicyRevision)."
}

function Get-PrincipalQueueLength {
    param($Admission)

    if ($null -ne $Admission.PerPrincipalQueueLength) { return [int]$Admission.PerPrincipalQueueLength }
    return [Math]::Min([int]$Admission.PerPrincipalConcurrency, [int]$Admission.GlobalQueueLength)
}

# Candidate and retained recovery settings have independent, reviewed contracts.
# Raising a revision never selects or changes an admission profile implicitly.
$profiles = @(
    @{ Document = $settings; Reference = $policyReference; Path = $PolicyReferencePath },
    @{ Document = $restore; Reference = $restoreReference; Path = $RestorePolicyReferencePath }
)
foreach ($profile in $profiles) {
    $document = $profile.Document
    if ([long]$document.Policy.Revision -lt 15) { throw "The policy must use revision 15 or higher." }
    if ([long]$document.Policy.Revision -lt [long]$profile.Reference.Policy.Revision) {
        throw "The policy revision must be at least its reference revision ($($profile.Reference.Policy.Revision))."
    }
    if ([long]$document.Policy.PrincipalDailyAllowanceNanoYuan -ne 3000000000 -or
        [long]$document.Policy.DailyOperatorBudgetNanoYuan -ne 50000000000 -or
        [long]$document.Policy.MonthlyOperatorBudgetNanoYuan -ne 1000000000000) {
        throw "The policy must preserve the 3/50/1000 yuan allowances and budgets."
    }
    $expectedResources = @('ai-model', 'latex-extraction', 'table-extraction', 'translation')
    foreach ($section in @('Resources', 'Pricing')) {
        $names = @($document.Policy.$section.PSObject.Properties.Name | Sort-Object)
        if (($names -join ',') -ne ($expectedResources -join ',')) {
            throw "Policy:$section must contain only the four public resources. Remove obsolete layered keys."
        }
    }
    foreach ($resource in $document.Policy.Resources.PSObject.Properties) {
        if ([long]$resource.Value.OperatorMaximumNanoYuan -ne 30000000) {
            throw "Every resource operator reservation maximum must remain 0.03 yuan."
        }
    }
    foreach ($resource in @('table-extraction', 'latex-extraction')) {
        if ([int]$document.Policy.Resources.$resource.GlobalConcurrency -ne 1 -or
            [int]$document.Policy.Resources.$resource.PerPrincipalConcurrency -ne 1) {
            throw "CPU recognition requires one global and per-principal slot for $resource."
        }
    }
    $admission = $document.Policy.Resources.'ai-model'
    $expectedAdmission = $profile.Reference.Policy.Resources.'ai-model'
    foreach ($key in @('RequestsPerMinute', 'PerPrincipalConcurrency', 'GlobalConcurrency',
            'GlobalQueueLength', 'QueueWaitSeconds', 'ExecutionDeadlineSeconds')) {
        if ($admission.$key -ne $expectedAdmission.$key) {
            throw "AI Model admission '$key' must match policy reference '$($profile.Path)' ($($expectedAdmission.$key))."
        }
    }
    if ((Get-PrincipalQueueLength $admission) -ne (Get-PrincipalQueueLength $expectedAdmission)) {
        throw "AI Model admission 'PerPrincipalQueueLength' must match policy reference '$($profile.Path)'."
    }
    if ($document.Policy.Estimation.BytesPerInputToken -ne 3 -or
        $document.Policy.Estimation.CharsPerOutputToken -ne 2) { throw "Estimation ratios must remain 3 and 2." }
    if ($document.Policy.Pricing.translation.InputRateNanoYuan -ne 2000 -or
        $document.Policy.Pricing.translation.OutputRateNanoYuan -ne 8000) {
        throw "Dedicated translation character deductions must remain unchanged."
    }
    if ([long]$document.Policy.Pricing.'latex-extraction'.InputRateNanoYuan -ne 15000000 -or
        [long]$document.Policy.Pricing.'latex-extraction'.OutputRateNanoYuan -ne 0) {
        throw "LaTeX extraction must cost 0.015 yuan per successful request."
    }
    $expectedTariffs = @{
        'qwen3.7-flash' = @(@(32000, 200, 800, 40), @(256000, 600, 2400, 120), @(1000000, 1200, 4800, 240))
        'qwen3.8-flash' = ,@(1000000, 800, 2700, 100)
    }
    if ((@($document.Policy.ModelPricing.PSObject.Properties.Name | Sort-Object) -join ',') -ne
        'qwen3.7-flash,qwen3.8-flash') { throw "Both execution tariffs are required." }
    foreach ($model in $expectedTariffs.Keys) {
        $tiers = @($document.Policy.ModelPricing.$model)
        $expected = $expectedTariffs[$model]
        if ($tiers.Count -ne $expected.Count) { throw "Unexpected tariff tier count for $model." }
        for ($i = 0; $i -lt $tiers.Count; $i++) {
            $actual = @($tiers[$i].MaximumInputTokens, $tiers[$i].InputRateNanoYuan,
                $tiers[$i].OutputRateNanoYuan, $tiers[$i].CachedInputRateNanoYuan)
            if (($actual -join ',') -ne ($expected[$i] -join ',')) { throw "Incorrect Beijing tariff for $model tier $i." }
        }
    }
    if ((@($document.Providers.Models.PSObject.Properties.Name | Sort-Object) -join ',') -ne
        'qwen3.7-flash,qwen3.8-flash') { throw "Only the text and vision execution models may remain." }
    if ((@($document.Providers.PublicModels.PSObject.Properties.Name) -join ',') -ne 'ai-model') {
        throw "AI Model must be the only public model."
    }
    $public = $document.Providers.PublicModels.'ai-model'
    if ($public.TextModel -ne 'qwen3.7-flash' -or $public.VisionModel -ne 'qwen3.8-flash' -or
        -not $public.Thinking -or (@($public.Aliases | Sort-Object) -join ',') -ne 'qwen-mt-flash,qwen3.8-flash') {
        throw "AI Model routes, aliases, or reasoning capability are incorrect."
    }
    foreach ($model in $expectedTariffs.Keys) {
        $execution = $document.Providers.Models.$model
        if ($execution.NativeTranslationOptions -or $execution.MergesSystemIntoUser -or
            $execution.Accesses.aliyun.UpstreamModel -ne $model) { throw "Invalid execution configuration for $model." }
    }
    if (-not $document.Providers.Models.'qwen3.8-flash'.SupportVision -or
        $document.Providers.Models.'qwen3.7-flash'.SupportVision) { throw "Incorrect execution modalities." }
    if ((@($document.Providers.Translation.LogicalModels) -join ',') -ne 'qwen3.7-flash') {
        throw "Dedicated translation must use only Qwen3.7-Flash."
    }
}
$models = @($settings.Providers.Translation.LogicalModels)

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
$proxyLocations = [regex]::Matches($nginx, '(?m)^\s*proxy_pass\s+http://snowshot_api;\s*$').Count
$disabledRetries = [regex]::Matches($nginx, '(?m)^\s*proxy_next_upstream\s+off;\s*$').Count
if ($proxyLocations -ne 4 -or $disabledRetries -ne $proxyLocations -or
    [regex]::IsMatch($nginx, 'proxy_pass\s+http://(?:localhost|127\.0\.0\.1):')) {
    throw "Every API/health proxy must use the managed upstream with retries disabled."
}

Write-Host "Deployment configuration verified: models=$($models -join ','), app=${maximumDeadline}s, nginx=${readTimeout}s."
