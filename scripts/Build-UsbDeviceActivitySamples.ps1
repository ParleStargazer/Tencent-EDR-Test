[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SamplesRoot,
    [string]$UsbDriverPackagePath,
    [string]$DriverCertificatePath,
    [string]$EwdkRoot = "F:\EWDK",
    [switch]$SuppressPrivilegeWarning
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) { $SamplesRoot = Join-Path $repositoryRoot "samples" }
if ([string]::IsNullOrWhiteSpace($UsbDriverPackagePath)) {
    $UsbDriverPackagePath = Join-Path $repositoryRoot "drivers\UsbUdeTest\prebuilt\x64"
}
if ([string]::IsNullOrWhiteSpace($DriverCertificatePath)) {
    $DriverCertificatePath = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
}
$SamplesRoot = [IO.Path]::GetFullPath($SamplesRoot)
$UsbDriverPackagePath = [IO.Path]::GetFullPath($UsbDriverPackagePath)
$DriverCertificatePath = [IO.Path]::GetFullPath($DriverCertificatePath)
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\usb-device-activity-publish"))
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

$isAdministrator = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator -and -not $SuppressPrivilegeWarning) {
    Write-Warning "当前 PowerShell 未以管理员身份运行。USB UDE 能力包可以构建，但实际安装驱动、挂载和卸载均需要管理员权限、testsigning 与显式 L3 确认。"
}

$requiredPackageFiles = @("UsbUdeTest.sys", "UsbUdeTest.inf", "UsbUdeTest.cat", "usb-driver-package.json")
$missing = @($requiredPackageFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $UsbDriverPackagePath $_) -PathType Leaf)
})
if ($missing.Count -gt 0) {
    throw "预构建 USB UDE 驱动包不完整：$UsbDriverPackagePath；缺少 $($missing -join ', ')。可在开发机运行 script\driver\Build-UsbUdeDriverPackage.ps1 -EwdkRoot '$EwdkRoot' -CertificateThumbprint <thumbprint> -UpdatePrebuilt。"
}
if (-not (Test-Path -LiteralPath $DriverCertificatePath -PathType Leaf)) {
    throw "USB UDE 驱动包缺少仅含公钥的测试证书：$DriverCertificatePath"
}

$metadata = Get-Content -LiteralPath (Join-Path $UsbDriverPackagePath "usb-driver-package.json") -Raw |
    ConvertFrom-Json
$driverPath = Join-Path $UsbDriverPackagePath "UsbUdeTest.sys"
$actualHash = (Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $metadata.sha256) { throw "UsbUdeTest.sys SHA256 与 usb-driver-package.json 不一致。" }
if ($metadata.signature_valid -ne $true) { throw "USB UDE 能力包拒绝使用未签名的 SYS/CAT。" }
if ($metadata.private_key_in_package -ne $false) { throw "USB UDE 包元数据必须明确 private_key_in_package=false。" }
if ($metadata.hardware_id -ne "ROOT\USB_UDE_TEST") { throw "USB UDE 包硬件 ID 与样本协议不一致。" }

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($DriverCertificatePath)
try {
    if ($certificate.HasPrivateKey) { throw "仓库能力包只能包含公开证书，不能包含私钥。" }
    $expectedThumbprint = ([string]$metadata.certificate_thumbprint).Replace(" ", "").ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($expectedThumbprint) -or $certificate.Thumbprint -ne $expectedThumbprint) {
        throw "公开证书指纹与 USB UDE 包元数据不一致。"
    }
    foreach ($name in @("UsbUdeTest.sys", "UsbUdeTest.cat")) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $UsbDriverPackagePath $name)
        if ($null -eq $signature.SignerCertificate `
            -or $signature.SignerCertificate.Thumbprint -ne $expectedThumbprint) {
            throw "$name 未包含预期测试证书签名。"
        }
    }
} finally {
    $certificate.Dispose()
}

dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\UsbDeviceActivity\UsbDeviceActivity.Controller\UsbDeviceActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "UsbDeviceActivity Controller 发布失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\UsbDeviceActivity\UsbDeviceActivity.Behavior\UsbDeviceActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "UsbDeviceActivity Actor 发布失败。" }

$packages = @(
    @{ Id = "win.device.usb.mount"; Prefix = "UsbDeviceMount" },
    @{ Id = "win.device.usb.unmount"; Prefix = "UsbDeviceUnmount" }
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
    Copy-Item (Join-Path $controllerPublish "UsbDevice.Controller.exe") `
        (Join-Path $destination "$($package.Prefix).Controller.exe") -Force
    Copy-Item (Join-Path $behaviorPublish "UsbDevice.Actor.exe") `
        (Join-Path $destination "$($package.Prefix).Actor.exe") -Force
    foreach ($name in $requiredPackageFiles) {
        Copy-Item -LiteralPath (Join-Path $UsbDriverPackagePath $name) `
            -Destination (Join-Path $destination $name) -Force
    }
    Copy-Item -LiteralPath $DriverCertificatePath `
        -Destination (Join-Path $destination "EdrTestDriverTest.cer") -Force

    $manifestPath = Join-Path $repositoryRoot "sample-src\UsbDeviceActivity\manifests\$($package.Id)\capability.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 30
    $controllerHash = (Get-FileHash -LiteralPath (Join-Path $destination $manifest.controller.executable) -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest.controller | Add-Member -NotePropertyName sha256 -NotePropertyValue $controllerHash -Force
    foreach ($participant in $manifest.participants) {
        $participantHash = (Get-FileHash -LiteralPath (Join-Path $destination $participant.executable) -Algorithm SHA256).Hash.ToLowerInvariant()
        $participant | Add-Member -NotePropertyName sha256 -NotePropertyValue $participantHash -Force
    }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $destination "capability.json") -Encoding utf8NoBOM
    Write-Host "[已生成] $($package.Id) -> $destination"
}

Write-Host "USB Device Activity 两个能力包构建完成：$SamplesRoot"
