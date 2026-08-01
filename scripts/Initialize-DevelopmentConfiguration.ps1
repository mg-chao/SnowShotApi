[CmdletBinding()]
param(
    [string]$SecretsRoot = (Join-Path $PSScriptRoot "..\.secrets\development"),
    [Security.SecureString]$ProviderApiKey,
    [Security.SecureString]$DeepSeekApiKey,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$resolvedRoot = [System.IO.Path]::GetFullPath($SecretsRoot)
$apiDirectory = Join-Path $resolvedRoot "api"
$migratorDirectory = Join-Path $resolvedRoot "migrator"
$targets = @(
    (Join-Path $resolvedRoot "postgres-password"),
    (Join-Path $apiDirectory "ConnectionStrings__SnowShot"),
    (Join-Path $apiDirectory "Identity__HmacKeyBase64"),
    (Join-Path $apiDirectory "Providers__CloudProviders__aliyun__ApiKey"),
    (Join-Path $apiDirectory "Providers__CloudProviders__deepseek__ApiKey"),
    (Join-Path $migratorDirectory "ConnectionStrings__SnowShot")
)

$existing = @($targets | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
if ($existing.Count -gt 0 -and -not $Force) {
    throw "Development secrets already exist. Use -Force only when replacing every generated credential intentionally."
}

function Get-RequiredSecretText {
    param(
        [Security.SecureString]$Value,
        [Parameter(Mandatory = $true)][string]$Prompt
    )

    while ($true) {
        if ($null -eq $Value) {
            $Value = Read-Host $Prompt -AsSecureString
        }
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
        try {
            $plainValue = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
            if (-not [string]::IsNullOrWhiteSpace($plainValue)) {
                return $plainValue
            }
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
        Write-Warning "A non-empty value is required."
        $Value = $null
    }
}

function Write-SecretFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    [System.IO.File]::WriteAllText($Path, $Value, [System.Text.UTF8Encoding]::new($false))
}

function New-RandomBytes {
    param([Parameter(Mandatory = $true)][int]$Count)

    $bytes = New-Object byte[] $Count
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    return $bytes
}

$providerApiKeyText = Get-RequiredSecretText -Value $ProviderApiKey -Prompt "Aliyun/DashScope provider API key"
$deepSeekApiKeyText = Get-RequiredSecretText -Value $DeepSeekApiKey -Prompt "DeepSeek provider API key"
$postgresPassword = -join ((New-RandomBytes 24) | ForEach-Object { $_.ToString("x2") })
$identityKey = [Convert]::ToBase64String((New-RandomBytes 32))
$connectionString = "Host=postgres;Port=5432;Database=snowshot;Username=snowshot;Password=$postgresPassword"

New-Item -ItemType Directory -Force -Path $apiDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $migratorDirectory | Out-Null
Write-SecretFile (Join-Path $resolvedRoot "postgres-password") $postgresPassword
Write-SecretFile (Join-Path $apiDirectory "ConnectionStrings__SnowShot") $connectionString
Write-SecretFile (Join-Path $apiDirectory "Identity__HmacKeyBase64") $identityKey
Write-SecretFile (Join-Path $apiDirectory "Providers__CloudProviders__aliyun__ApiKey") $providerApiKeyText
Write-SecretFile (Join-Path $apiDirectory "Providers__CloudProviders__deepseek__ApiKey") $deepSeekApiKeyText
Write-SecretFile (Join-Path $migratorDirectory "ConnectionStrings__SnowShot") $connectionString

Write-Host "Development configuration initialized under $resolvedRoot"
Write-Host "Run: docker compose up -d --build"
