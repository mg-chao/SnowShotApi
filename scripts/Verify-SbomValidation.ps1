[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$ReportPath
)

$ErrorActionPreference = "Stop"
$paths = @($ReportPath | ForEach-Object { $_ -split "," })
foreach ($path in $paths) {
    $resolved = [System.IO.Path]::GetFullPath($path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "The SBOM validation report does not exist: $resolved"
    }

    $report = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    $errorCount = [int]$report.ValidationErrors.Count
    $packageCount = [int]$report.Summary.ValidationTelemetery.TotalPackagesInManifest
    if ($report.Result -ne "Success" -or $errorCount -ne 0) {
        throw "SBOM validation failed for '$resolved': result=$($report.Result), errors=$errorCount"
    }
    if ($packageCount -le 1) {
        throw "SBOM validation found no dependency packages for '$resolved'."
    }

    Write-Host "SBOM validation passed for '$resolved' with $($packageCount - 1) dependency package(s)."
}
