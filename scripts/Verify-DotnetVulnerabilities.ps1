[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath
)

$ErrorActionPreference = "Stop"
$resolved = [System.IO.Path]::GetFullPath($ReportPath)
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "The .NET vulnerability report does not exist: $resolved"
}

$report = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
$findings = [System.Collections.Generic.List[string]]::new()

function Visit-Node {
    param([object]$Node)
    if ($null -eq $Node -or $Node -is [string] -or $Node.GetType().IsPrimitive) { return }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [System.Management.Automation.PSCustomObject]) {
        foreach ($item in $Node) { Visit-Node $item }
        return
    }

    $properties = $Node.PSObject.Properties
    $id = ($properties | Where-Object Name -eq "id" | Select-Object -First 1).Value
    $vulnerabilities = ($properties | Where-Object Name -eq "vulnerabilities" | Select-Object -First 1).Value
    foreach ($vulnerability in @($vulnerabilities)) {
        if ($null -eq $vulnerability) { continue }
        $severity = if ($vulnerability.severity) { $vulnerability.severity } else { "unknown" }
        $advisory = if ($vulnerability.advisoryurl) { $vulnerability.advisoryurl } elseif ($vulnerability.advisoryUrl) { $vulnerability.advisoryUrl } else { "unknown advisory" }
        $package = if ($id) { $id } else { "unknown package" }
        $findings.Add("$package [$severity] $advisory")
    }
    foreach ($property in $properties) {
        if ($property.Name -ne "vulnerabilities") { Visit-Node $property.Value }
    }
}

Visit-Node $report
if ($findings.Count -ne 0) {
    throw "Known vulnerable .NET packages are forbidden:`n$($findings -join [Environment]::NewLine)"
}
Write-Host "No known vulnerable .NET packages found."
