[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TemplatePath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [Parameter(Mandatory = $true)]
    [string]$ListenHost,
    [ValidateRange(1, 65535)]
    [int]$ListenPort = 18080,
    [ValidateSet("development", "staging", "production")]
    [string]$ServiceEnvironment = "production",
    [string]$ServiceAccount = "NT AUTHORITY\LocalService",
    [string]$TlsCertificate,
    [string]$TlsPrivateKey,
    [string]$TlsClientCa
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ListenHost)) {
    throw "ListenHost must not be empty."
}
if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
    throw "ServiceAccount must not be empty."
}
$privilegedAccounts = @("LocalSystem", "SYSTEM", "NT AUTHORITY\SYSTEM")
if ($ServiceEnvironment -ne "development" -and $privilegedAccounts -contains $ServiceAccount) {
    throw "Staging and production services must not run as LocalSystem."
}

$tlsValues = @($TlsCertificate, $TlsPrivateKey, $TlsClientCa)
$configuredTlsValues = @($tlsValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($configuredTlsValues.Count -ne 0 -and $configuredTlsValues.Count -ne 3) {
    throw "TlsCertificate, TlsPrivateKey, and TlsClientCa must be configured together."
}
if ($ServiceEnvironment -ne "development" -and $configuredTlsValues.Count -ne 3) {
    throw "Staging and production service configurations require mutual TLS files."
}
foreach ($tlsPath in $configuredTlsValues) {
    if (-not (Test-Path -LiteralPath $tlsPath -PathType Leaf)) {
        throw "TLS path does not exist or is not a file: $tlsPath"
    }
}

$resolvedTemplate = [System.IO.Path]::GetFullPath($TemplatePath)
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
[xml]$configuration = Get-Content -LiteralPath $resolvedTemplate -Raw
$serviceAccount = $ServiceAccount.Trim()
$separatorIndex = $serviceAccount.LastIndexOf('\')
if ($separatorIndex -gt 0) {
    $serviceAccountDomain = $serviceAccount.Substring(0, $separatorIndex)
    $serviceAccountUser = $serviceAccount.Substring($separatorIndex + 1)
}
else {
    $serviceAccountDomain = $null
    $serviceAccountUser = $serviceAccount
}
if ([string]::IsNullOrWhiteSpace($serviceAccountUser)) {
    throw "ServiceAccount must include a user name."
}
$serviceAccountNode = $configuration.SelectSingleNode("/service/serviceaccount")
if ($null -eq $serviceAccountNode) {
    throw "Service configuration template is missing the WinSW service account nodes."
}
$serviceAccountDomainNode = $serviceAccountNode.SelectSingleNode("domain")
$serviceAccountUserNode = $serviceAccountNode.SelectSingleNode("user")
if ($null -eq $serviceAccountDomainNode -or $null -eq $serviceAccountUserNode) {
    throw "Service configuration template is missing the WinSW service account nodes."
}
$serviceAccountUserNode.InnerText = $serviceAccountUser
if ($null -eq $serviceAccountDomain) {
    [void]$serviceAccountNode.RemoveChild($serviceAccountDomainNode)
}
else {
    $serviceAccountDomainNode.InnerText = $serviceAccountDomain
}
$values = @{
    TABLE_REC_HOST = $ListenHost
    TABLE_REC_PORT = $ListenPort.ToString([Globalization.CultureInfo]::InvariantCulture)
    TABLE_REC_ENVIRONMENT = $ServiceEnvironment
    TABLE_REC_TLS_CERTIFICATE = if ($TlsCertificate) { [System.IO.Path]::GetFullPath($TlsCertificate) } else { "" }
    TABLE_REC_TLS_PRIVATE_KEY = if ($TlsPrivateKey) { [System.IO.Path]::GetFullPath($TlsPrivateKey) } else { "" }
    TABLE_REC_TLS_CLIENT_CA = if ($TlsClientCa) { [System.IO.Path]::GetFullPath($TlsClientCa) } else { "" }
}

foreach ($entry in $values.GetEnumerator()) {
    $node = $configuration.service.env | Where-Object { $_.name -eq $entry.Key }
    if ($null -eq $node) {
        throw "Service configuration template is missing environment node $($entry.Key)."
    }
    $node.SetAttribute("value", [string]$entry.Value)
}

$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [string]::IsNullOrEmpty($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}
$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$writer = [System.Xml.XmlWriter]::Create($resolvedOutput, $settings)
try {
    $configuration.Save($writer)
}
finally {
    $writer.Dispose()
}
