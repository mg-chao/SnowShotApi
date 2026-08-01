[CmdletBinding()]
param([switch]$Update)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$snapshot = Join-Path $root "src\SnowShot.ApiAdapter\openapi.yaml"
$generated = Join-Path ([System.IO.Path]::GetTempPath()) "snowshot-openapi-$([Guid]::NewGuid().ToString('N')).yaml"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5197"
$env:ConnectionStrings__SnowShot = "Host=127.0.0.1;Database=unused;Username=unused;Password=unused"
$env:ConnectionStrings__Redis = " "
$env:Identity__HmacKeyBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
${env:Providers__CloudProviders__aliyun__ApiKey} = "openapi"
${env:Providers__CloudProviders__deepseek__ApiKey} = "openapi"
$env:Providers__Table__BaseUrl = "http://127.0.0.1:18080/"
$env:ContractGeneration = "true"

$process = Start-Process dotnet -ArgumentList @(
    "run", "--configuration", "Release", "--no-build", "--no-launch-profile", "--project", "src\SnowShotApi\SnowShotApi.csproj"
) -WorkingDirectory $root -WindowStyle Hidden -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:5197/openapi/v1.json" | Out-Null
            $ready = $true
            break
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw "OpenAPI host did not become ready." }
    & python (Join-Path $PSScriptRoot "openapi_snapshot.py") "http://127.0.0.1:5197/openapi/v1.json" $generated
    if ($LASTEXITCODE -ne 0) { throw "OpenAPI generation failed." }
    if ($Update) {
        Copy-Item -LiteralPath $generated -Destination $snapshot -Force
    }
    elseif ((Get-FileHash $generated).Hash -ne (Get-FileHash $snapshot).Hash) {
        throw "OpenAPI snapshot drifted. Run scripts/Verify-OpenApi.ps1 -Update."
    }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item -LiteralPath $generated -Force -ErrorAction SilentlyContinue
}
