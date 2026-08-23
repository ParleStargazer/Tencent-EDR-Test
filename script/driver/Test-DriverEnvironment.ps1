#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$EwdkRoot = "F:\EWDK",
    [string]$CertificatePath,
    [string]$DriverPackagePath,
    [switch]$AsJson
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
}
if ([string]::IsNullOrWhiteSpace($DriverPackagePath)) {
    $DriverPackagePath = Join-Path $repositoryRoot "drivers\EdrTestDriver\prebuilt\x64"
}

$isAdministrator = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$bcdOutput = & (Join-Path $env:SystemRoot "System32\bcdedit.exe") /enum "{current}" 2>&1
$testSigning = $LASTEXITCODE -eq 0 -and ($bcdOutput -join "`n") `
    -match '(?im)^\s*testsigning\s+(Yes|On|是|开启)\s*$'
$secureBoot = $null
try { $secureBoot = Confirm-SecureBootUEFI -ErrorAction Stop } catch { }

$certificate = $null
if (Test-Path -LiteralPath $CertificatePath -PathType Leaf) {
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
}
$thumbprint = if ($null -eq $certificate) { $null } else { $certificate.Thumbprint }
function Test-Store([string]$StoreName, [string]$Thumbprint) {
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) { return $false }
    return $null -ne (Get-ChildItem "Cert:\LocalMachine\$StoreName" -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $Thumbprint | Select-Object -First 1)
}

$driverPath = Join-Path $DriverPackagePath "EdrTestDriver.sys"
$infPath = Join-Path $DriverPackagePath "EdrTestDriver.inf"
$catPath = Join-Path $DriverPackagePath "EdrTestDriver.cat"
$metadataPath = Join-Path $DriverPackagePath "driver-package.json"
$metadata = if (Test-Path -LiteralPath $metadataPath) {
    Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
} else { $null }
$metadataSha256 = if ($null -eq $metadata) { $null } else { [string]$metadata.sha256 }
$metadataSignatureValid = if ($null -eq $metadata) { $false } else { $metadata.signature_valid -eq $true }
$actualHash = if (Test-Path -LiteralPath $driverPath) {
    (Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash.ToLowerInvariant()
} else { $null }

$result = [ordered]@{
    checked_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    administrator = $isAdministrator
    testsigning_enabled = $testSigning
    secure_boot_enabled = $secureBoot
    ewdk_root = [IO.Path]::GetFullPath($EwdkRoot)
    ewdk_setup_exists = Test-Path -LiteralPath (Join-Path $EwdkRoot "BuildEnv\SetupBuildEnv.cmd")
    ewdk_version = if (Test-Path -LiteralPath (Join-Path $EwdkRoot "Version")) {
        (Get-Content -LiteralPath (Join-Path $EwdkRoot "Version") -Raw).Trim()
    } else { $null }
    certificate_path = [IO.Path]::GetFullPath($CertificatePath)
    certificate_thumbprint = $thumbprint
    certificate_in_local_machine_root = Test-Store "Root" $thumbprint
    certificate_in_local_machine_trusted_publisher = Test-Store "TrustedPublisher" $thumbprint
    driver_package_path = [IO.Path]::GetFullPath($DriverPackagePath)
    driver_exists = Test-Path -LiteralPath $driverPath
    inf_exists = Test-Path -LiteralPath $infPath
    catalog_exists = Test-Path -LiteralPath $catPath
    metadata_exists = Test-Path -LiteralPath $metadataPath
    driver_sha256 = $actualHash
    metadata_sha256 = $metadataSha256
    hash_matches_metadata = $null -ne $actualHash -and $actualHash -eq $metadataSha256
    package_signature_valid = $metadataSignatureValid
    ready_for_load = $isAdministrator -and $testSigning `
        -and (Test-Store "Root" $thumbprint) -and (Test-Store "TrustedPublisher" $thumbprint) `
        -and (Test-Path -LiteralPath $driverPath) -and (Test-Path -LiteralPath $catPath) `
        -and $null -ne $actualHash -and $actualHash -eq $metadataSha256 -and $metadataSignatureValid
}

if ($AsJson) { $result | ConvertTo-Json -Depth 10 } else { [pscustomobject]$result | Format-List }
