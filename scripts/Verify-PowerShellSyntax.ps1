[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
$sourceRoots = @(
    (Join-Path $RepositoryRoot "scripts"),
    (Join-Path $RepositoryRoot "services\TableStructureRec\scripts"),
    (Join-Path $RepositoryRoot "services\TableStructureRec\tests")
)
$failures = [System.Collections.Generic.List[string]]::new()
$files = @(
    $sourceRoots |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter "*.ps1" }
)

foreach ($file in $files) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName,
        [ref]$tokens,
        [ref]$errors
    ) | Out-Null
    foreach ($parseError in @($errors)) {
        $failures.Add("$($file.FullName):$($parseError.Extent.StartLineNumber): $($parseError.Message)")
    }
}

if ($failures.Count -ne 0) {
    throw "PowerShell syntax validation failed:`n$($failures -join [Environment]::NewLine)"
}
Write-Host "PowerShell syntax validation passed for $($files.Count) script(s) under PowerShell $($PSVersionTable.PSVersion)."
