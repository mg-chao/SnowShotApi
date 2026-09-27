[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StateDirectory
)

$ErrorActionPreference = "Stop"
$countPath = Join-Path $StateDirectory "starts.txt"
$identityPath = Join-Path $StateDirectory "identity.txt"
$readyPath = Join-Path $StateDirectory "ready.txt"
$count = if (Test-Path -LiteralPath $countPath) {
    [int](Get-Content -LiteralPath $countPath -Raw)
}
else { 0 }
$count++
Set-Content -LiteralPath $countPath -Value $count -Encoding ascii
Set-Content -LiteralPath $identityPath -Value ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -Encoding ascii

if ($count -eq 1) {
    exit 70
}

Set-Content -LiteralPath $readyPath -Value "ready" -Encoding ascii
while ($true) { Start-Sleep -Seconds 1 }
