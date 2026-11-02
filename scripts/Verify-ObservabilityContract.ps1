[CmdletBinding()]
param(
    [string]$ContractPath = "deployment/observability/alerts.json",
    [string]$TelemetrySourcePath = "src/SnowShot.Infrastructure/Production/Telemetry/Telemetry.cs",
    [string]$OwnershipPath = "docs/ownership.md",
    [string]$RunbookPath = "docs/runbook.md",
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

foreach ($path in @($ContractPath, $TelemetrySourcePath, $OwnershipPath, $RunbookPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required observability input '$path' does not exist."
    }
}

$contract = Get-Content -LiteralPath $ContractPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($contract.version -ne 1) { throw "Observability contract version must be 1." }
if ([string]::IsNullOrWhiteSpace($contract.service)) { throw "Observability contract service is required." }

$telemetrySource = Get-Content -LiteralPath $TelemetrySourcePath -Raw -Encoding UTF8
$metricMatches = [regex]::Matches(
    $telemetrySource,
    'Meter\.Create(?:Counter|Histogram|UpDownCounter)<[^>]+>\("([^"]+)"')
$metrics = @($metricMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($metrics.Count -eq 0) { throw "No OpenTelemetry metric instruments were found in '$TelemetrySourcePath'." }

$ownership = Get-Content -LiteralPath $OwnershipPath -Raw -Encoding UTF8
$owners = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($ownership, '(?m)^\|\s*([^|]+?)\s*\|')) {
    $candidate = $match.Groups[1].Value.Trim()
    if ($candidate -notin @("Boundary", "---")) { [void]$owners.Add($candidate) }
}

$runbook = Get-Content -LiteralPath $RunbookPath -Raw -Encoding UTF8
$knownMetrics = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($metric in $metrics) { [void]$knownMetrics.Add($metric) }
$classified = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$ruleIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$alerted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$allowedSeverities = @("page", "ticket")
$rules = @($contract.rules)
if ($rules.Count -eq 0) { throw "At least one observability alert rule is required." }

foreach ($rule in $rules) {
    if ($rule.id -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or -not $ruleIds.Add([string]$rule.id)) {
        throw "Alert rule ids must be unique kebab-case values; invalid id '$($rule.id)'."
    }
    if ($rule.severity -notin $allowedSeverities) {
        throw "Alert rule '$($rule.id)' has invalid severity '$($rule.severity)'."
    }
    if ([int]$rule.windowSeconds -lt 1 -or [int]$rule.windowSeconds -gt 86400) {
        throw "Alert rule '$($rule.id)' has an invalid evaluation window."
    }
    if ([string]::IsNullOrWhiteSpace($rule.condition)) {
        throw "Alert rule '$($rule.id)' must define a condition."
    }
    if (-not $owners.Contains([string]$rule.owner)) {
        throw "Alert rule '$($rule.id)' references unknown owner '$($rule.owner)'."
    }
    $heading = "## $($rule.runbookSection)"
    if ($runbook -notmatch "(?m)^$([regex]::Escape($heading))\s*$") {
        throw "Alert rule '$($rule.id)' references missing runbook section '$($rule.runbookSection)'."
    }
    $signals = @($rule.signals)
    if ($signals.Count -eq 0) { throw "Alert rule '$($rule.id)' must classify at least one metric." }
    foreach ($signal in $signals) {
        if (-not $knownMetrics.Contains([string]$signal)) {
            throw "Alert rule '$($rule.id)' references unknown metric '$signal'."
        }
        if (-not $classified.Add([string]$signal)) {
            throw "Metric '$signal' is classified more than once."
        }
        [void]$alerted.Add([string]$signal)
    }
}

foreach ($entry in @($contract.dashboardOnly)) {
    if (-not $knownMetrics.Contains([string]$entry.signal)) {
        throw "Dashboard-only entry references unknown metric '$($entry.signal)'."
    }
    if ([string]::IsNullOrWhiteSpace($entry.reason)) {
        throw "Dashboard-only metric '$($entry.signal)' requires a rationale."
    }
    if (-not $classified.Add([string]$entry.signal)) {
        throw "Metric '$($entry.signal)' is classified more than once."
    }
}

$unclassified = @($metrics | Where-Object { -not $classified.Contains($_) })
if ($unclassified.Count -gt 0) {
    throw "Unclassified OpenTelemetry metrics: $($unclassified -join ', ')."
}

$mandatoryPages = @(
    "snowshot.admission.dependency_failures",
    "snowshot.leases.lost",
    "snowshot.operations.renewal_failures",
    "snowshot.provider.attempt_checkpoint_failures",
    "snowshot.operations.lifecycle_failures",
    "snowshot.cost.unknown.operations",
    "snowshot.cost.overage.nanoyuan",
    "snowshot.identity.integrity_conflicts"
)
foreach ($metric in $mandatoryPages) {
    if (-not $alerted.Contains($metric)) { throw "Mandatory page-class metric '$metric' is not covered by an alert rule." }
    $coveringRule = @($rules | Where-Object { $_.signals -contains $metric })
    if ($coveringRule.Count -ne 1 -or $coveringRule[0].severity -ne "page") {
        throw "Mandatory page-class metric '$metric' must be covered by exactly one page rule."
    }
}

Write-Host "Observability contract validation passed for $($metrics.Count) metric(s), $($rules.Count) alert rule(s), and $(@($contract.dashboardOnly).Count) dashboard-only classification(s)."

if ($SelfTest) {
    $temporaryContract = Join-Path ([IO.Path]::GetTempPath()) "snowshot-alerts-$([Guid]::NewGuid().ToString('N')).json"
    try {
        $mutated = Get-Content -LiteralPath $ContractPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $mutated.dashboardOnly = @($mutated.dashboardOnly | Select-Object -Skip 1)
        $mutated | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryContract -Encoding UTF8

        $executable = if ($PSVersionTable.PSEdition -eq "Core") {
            Join-Path $PSHOME "pwsh.exe"
        }
        else {
            Join-Path $PSHOME "powershell.exe"
        }
        $previousErrorAction = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & $executable -NoProfile -File $PSCommandPath -ContractPath $temporaryContract `
                -TelemetrySourcePath $TelemetrySourcePath -OwnershipPath $OwnershipPath -RunbookPath $RunbookPath 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorAction
        }
        if ($exitCode -eq 0) { throw "Observability verifier self-test accepted an incomplete contract." }
        if (($output | Out-String) -notmatch "Unclassified OpenTelemetry metrics") {
            throw "Observability verifier self-test failed for an unexpected reason: $($output | Out-String)"
        }
        Write-Host "Observability contract negative self-test passed."
        $global:LASTEXITCODE = 0
    }
    finally {
        if (Test-Path -LiteralPath $temporaryContract) { Remove-Item -LiteralPath $temporaryContract -Force }
    }
}
