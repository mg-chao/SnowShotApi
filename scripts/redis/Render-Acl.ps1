[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PasswordFile,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$UserName = "snowshot-api"
)

$ErrorActionPreference = "Stop"
if ($UserName -notmatch '^[A-Za-z0-9._-]{1,64}$') {
    throw "Redis ACL user names may contain only letters, digits, dot, underscore, and hyphen."
}
$resolvedPassword = [System.IO.Path]::GetFullPath($PasswordFile)
if (-not (Test-Path -LiteralPath $resolvedPassword -PathType Leaf)) {
    throw "Redis ACL password file does not exist: $resolvedPassword"
}
$password = (Get-Content -LiteralPath $resolvedPassword -Raw).Trim()
if ($password.Length -lt 32) {
    throw "Redis ACL passwords must contain at least 32 characters."
}
$bytes = [System.Text.Encoding]::UTF8.GetBytes($password)
$algorithm = [Security.Cryptography.SHA256]::Create()
try { $hashBytes = $algorithm.ComputeHash($bytes) }
finally { $algorithm.Dispose() }
$hash = ($hashBytes | ForEach-Object { $_.ToString("x2", [Globalization.CultureInfo]::InvariantCulture) }) -join ""
$line = "user $UserName on #$hash ~{snowshot:*}:* resetchannels +@connection +@scripting +@read +@write -@dangerous"
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$directory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$temporary = "$resolvedOutput.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [System.IO.File]::WriteAllText($temporary, "$line`n", [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $resolvedOutput -Force
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
