#Requires -Version 7.0
[CmdletBinding()]
param(
    [string[]]$CapabilityKey = @()
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$fingerprintRoot = Join-Path $repositoryRoot "build-fingerprints\capabilities"
. (Join-Path $PSScriptRoot "Build-Cache.ps1")

$sharedFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot -InputPaths @(
    "Directory.Build.props",
    "sample-src\Common",
    "src\EdrTest",
    "schemas\run-db.sql"
) -Properties @{
    cache_contract = "repository-capability-source-v1"
    configuration = "Release"
}

$certificatePath = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
$definitions = @(
    [pscustomobject]@{ Key = "process"; Source = "ProcessActivity"; Script = "Build-ProcessActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "file"; Source = "FileManipulation"; Script = "Build-FileManipulationSamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "hash"; Source = "HashAlgorithms"; Script = "Build-HashAlgorithmsSamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "account"; Source = "UserAccountActivity"; Script = "Build-UserAccountActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "network"; Source = "NetworkActivity"; Script = "Build-NetworkActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "registry"; Source = "RegistryActivity"; Script = "Build-RegistryActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "scheduled-task"; Source = "ScheduledTaskActivity"; Script = "Build-ScheduledTaskActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "service"; Source = "ServiceActivity"; Script = "Build-ServiceActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "group-policy"; Source = "GroupPolicyActivity"; Script = "Build-GroupPolicyActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "named-pipe"; Source = "NamedPipeActivity"; Script = "Build-NamedPipeActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "powershell"; Source = "PowerShellActivity"; Script = "Build-PowerShellActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "bits"; Source = "BitsActivity"; Script = "Build-BitsActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "wmi"; Source = "WmiActivity"; Script = "Build-WmiActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{ Key = "virtual-disk"; Source = "VirtualDiskActivity"; Script = "Build-VirtualDiskActivitySamples.ps1"; ExtraInputs = @() },
    [pscustomobject]@{
        Key = "driver"; Source = "DriverActivity"; Script = "Build-DriverActivitySamples.ps1"
        ExtraInputs = @((Join-Path $repositoryRoot "drivers\EdrTestDriver\prebuilt\x64"), $certificatePath)
    },
    [pscustomobject]@{
        Key = "usb-device"; Source = "UsbDeviceActivity"; Script = "Build-UsbDeviceActivitySamples.ps1"
        ExtraInputs = @((Join-Path $repositoryRoot "drivers\UsbUdeTest\prebuilt\x64"), $certificatePath)
    }
)

if ($CapabilityKey.Count -gt 0) {
    $unknown = @($CapabilityKey | Where-Object { $_ -notin $definitions.Key })
    if ($unknown.Count -gt 0) { throw "未知能力域：$($unknown -join ', ')" }
    $definitions = @($definitions | Where-Object Key -in $CapabilityKey)
}

foreach ($definition in $definitions) {
    $sourceDirectory = Join-Path $repositoryRoot "sample-src\$($definition.Source)"
    $manifestRoot = Join-Path $sourceDirectory "manifests"
    $packagePaths = @(Get-ChildItem -LiteralPath $manifestRoot -Directory | Sort-Object Name | ForEach-Object {
        Join-Path $repositoryRoot "samples\$($_.Name)"
    })
    if ($packagePaths.Count -eq 0) { throw "能力域没有 manifest：$($definition.Key)" }

    $sourceFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot -InputPaths (@(
        $sourceDirectory,
        (Join-Path $PSScriptRoot $definition.Script)
    ) + @($definition.ExtraInputs)) -Properties @{
        cache_contract = "repository-capability-source-v1"
        configuration = "Release"
        shared = $sharedFingerprint
    }
    $fingerprintPath = Join-Path $fingerprintRoot "$($definition.Key).json"
    Set-EdrRepositoryCapabilityFingerprint -FingerprintPath $fingerprintPath `
        -CapabilityKey $definition.Key -SourceFingerprint $sourceFingerprint `
        -RepositoryRoot $repositoryRoot -CapabilityPackagePaths $packagePaths
    if (-not (Test-EdrRepositoryCapabilityFingerprint -FingerprintPath $fingerprintPath `
            -SourceFingerprint $sourceFingerprint -RepositoryRoot $repositoryRoot `
            -CapabilityPackagePaths $packagePaths)) {
        throw "能力包指纹回读验证失败：$($definition.Key)"
    }
    Write-Host "[指纹已更新] $($definition.Key) -> $fingerprintPath" -ForegroundColor Green
}
