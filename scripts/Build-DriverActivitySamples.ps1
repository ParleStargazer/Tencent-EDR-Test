[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SamplesRoot,
    [string]$DriverPackagePath,
    [string]$DriverCertificatePath,
    [string]$EwdkRoot = "F:\EWDK",
    [switch]$SuppressPrivilegeWarning,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) { $SamplesRoot = Join-Path $repositoryRoot "samples" }
if ([string]::IsNullOrWhiteSpace($DriverPackagePath)) {
    $DriverPackagePath = Join-Path $repositoryRoot "drivers\EdrTestDriver\prebuilt\x64"
}
if ([string]::IsNullOrWhiteSpace($DriverCertificatePath)) {
    $DriverCertificatePath = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
}
$SamplesRoot = [IO.Path]::GetFullPath($SamplesRoot)
$DriverPackagePath = [IO.Path]::GetFullPath($DriverPackagePath)
$DriverCertificatePath = [IO.Path]::GetFullPath($DriverCertificatePath)
$publishRoot = Join-Path $repositoryRoot "artifacts\driver-activity-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

$isAdministrator = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator -and -not $SuppressPrivilegeWarning) {
    Write-Warning "当前 PowerShell 未以管理员身份运行。能力包可以构建，但驱动三项均为 L3；实际加载/修改/卸载需要管理员权限和显式高风险确认。"
}

$requiredPackageFiles = @("EdrTestDriver.sys", "EdrTestDriver.inf", "EdrTestDriver.cat", "driver-package.json")
$missing = @($requiredPackageFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $DriverPackagePath $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "预构建驱动包不完整：$DriverPackagePath；缺少 $($missing -join ', ')。请在开发机运行 script\driver\Build-DriverPackage.ps1 -EwdkRoot '$EwdkRoot' -CertificateThumbprint <thumbprint> -UpdatePrebuilt。"
}
if (-not (Test-Path -LiteralPath $DriverCertificatePath -PathType Leaf)) {
    throw "驱动包缺少仅含公钥的测试证书：$DriverCertificatePath"
}
$metadata = Get-Content (Join-Path $DriverPackagePath "driver-package.json") -Raw | ConvertFrom-Json
$actualHash = (Get-FileHash (Join-Path $DriverPackagePath "EdrTestDriver.sys") -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $metadata.sha256) { throw "预构建驱动包 SHA256 与 driver-package.json 不一致。" }
if ($metadata.signature_valid -ne $true) { throw "驱动能力包拒绝使用未签名的 SYS/CAT。" }
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($DriverCertificatePath)
try {
    $expectedThumbprint = ([string]$metadata.certificate_thumbprint).Replace(" ", "").ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($expectedThumbprint) -or $certificate.Thumbprint -ne $expectedThumbprint) {
        throw "公开证书指纹与驱动包元数据不一致。"
    }
    foreach ($name in @("EdrTestDriver.sys", "EdrTestDriver.cat")) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $DriverPackagePath $name)
        if ($null -eq $signature.SignerCertificate `
            -or $signature.SignerCertificate.Thumbprint -ne $expectedThumbprint) {
            throw "$name 未包含预期测试证书签名。"
        }
    }
} finally {
    $certificate.Dispose()
}

if (-not $SkipRestore) {
    dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
}
dotnet publish (Join-Path $repositoryRoot "sample-src\DriverActivity\DriverActivity.Controller\DriverActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "DriverActivity Controller 发布失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\DriverActivity\DriverActivity.Behavior\DriverActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "DriverActivity Behavior 发布失败。" }

$packages = @(
    @{ Id = "win.driver.load"; Prefix = "DriverLoad" },
    @{ Id = "win.driver.modify"; Prefix = "DriverModify" },
    @{ Id = "win.driver.unload"; Prefix = "DriverUnload" }
)
foreach ($package in $packages) {
    $destination = [IO.Path]::GetFullPath((Join-Path $SamplesRoot $package.Id))
    $relative = [IO.Path]::GetRelativePath($SamplesRoot, $destination)
    if ($relative.StartsWith("..", [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
        throw "能力包目标越出 samples 根目录：$destination"
    }
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    Copy-Item (Join-Path $controllerPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $behaviorPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $controllerPublish "DriverActivity.Controller.exe") `
        (Join-Path $destination "$($package.Prefix).Controller.exe") -Force
    Copy-Item (Join-Path $behaviorPublish "DriverActivity.Behavior.exe") `
        (Join-Path $destination "$($package.Prefix).Actor.exe") -Force
    foreach ($name in $requiredPackageFiles) {
        Copy-Item -LiteralPath (Join-Path $DriverPackagePath $name) -Destination (Join-Path $destination $name) -Force
    }
    Copy-Item -LiteralPath $DriverCertificatePath `
        -Destination (Join-Path $destination "EdrTestDriverTest.cer") -Force

    $manifestPath = Join-Path $repositoryRoot "sample-src\DriverActivity\manifests\$($package.Id)\capability.json"
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -Depth 30
    $controllerHash = (Get-FileHash (Join-Path $destination $manifest.controller.executable) -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest.controller | Add-Member -NotePropertyName sha256 -NotePropertyValue $controllerHash -Force
    foreach ($participant in $manifest.participants) {
        $participantHash = (Get-FileHash (Join-Path $destination $participant.executable) -Algorithm SHA256).Hash.ToLowerInvariant()
        $participant | Add-Member -NotePropertyName sha256 -NotePropertyValue $participantHash -Force
    }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $destination "capability.json") -Encoding utf8NoBOM
    Write-Host "[已生成] $($package.Id) -> $destination"
}

Write-Host "Driver Activity 三个能力包构建完成：$SamplesRoot"
