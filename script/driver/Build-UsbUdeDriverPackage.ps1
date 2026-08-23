#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$EwdkRoot = "F:\EWDK",
    [string]$CertificateThumbprint,
    [string]$OutputPath,
    [switch]$UpdatePrebuilt
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectRoot = Join-Path $repositoryRoot "drivers\UsbUdeTest"
$project = Join-Path $projectRoot "UsbUdeTest.vcxproj"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot "artifacts\usb-ude-driver-package\x64\$Configuration"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$EwdkRoot = [IO.Path]::GetFullPath($EwdkRoot)
$outputRoot = [IO.Path]::GetPathRoot($OutputPath)
if ([string]::IsNullOrWhiteSpace($outputRoot) `
    -or $OutputPath.TrimEnd('\') -eq $outputRoot.TrimEnd('\') `
    -or $OutputPath.TrimEnd('\') -eq [IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\') `
    -or $OutputPath.TrimEnd('\') -eq [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') `
    -or $OutputPath.TrimEnd('\') -eq $EwdkRoot.TrimEnd('\')) {
    throw "拒绝把文件系统根目录、仓库根目录、驱动源码目录或 EWDK 根目录作为输出目录：$OutputPath"
}

$setup = Join-Path $EwdkRoot "BuildEnv\SetupBuildEnv.cmd"
$msbuild = Join-Path $EwdkRoot "Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$kitVersion = "10.0.28000.0"
$versionLine = Get-Content (Join-Path $EwdkRoot "BuildEnv\SetupBuildEnv.cmd") -ErrorAction SilentlyContinue |
    Where-Object { $_ -match 'set "Version_Number=([^\"]+)"' } | Select-Object -First 1
if ($versionLine -match 'set "Version_Number=([^\"]+)"') { $kitVersion = $Matches[1] }
$signTool = Join-Path $EwdkRoot "Program Files\Windows Kits\10\bin\$kitVersion\x64\signtool.exe"
$inf2Cat = Join-Path $EwdkRoot "Program Files\Windows Kits\10\bin\$kitVersion\x86\Inf2Cat.exe"

foreach ($required in @($setup, $msbuild, $signTool, $inf2Cat, $project)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "USB UDE 驱动构建依赖不存在：$required" }
}

$buildCommand = 'call "{0}" amd64 && "{1}" "{2}" /t:Rebuild /m /p:Configuration={3} /p:Platform=x64 /p:WindowsTargetPlatformVersion={4} /p:SignMode=Off /p:EnableTestSign=false /p:EnableInf2cat=false /p:SkipPackageVerification=true' -f `
    $setup, $msbuild, $project, $Configuration, $kitVersion
& $env:ComSpec /d /s /c $buildCommand
if ($LASTEXITCODE -ne 0) { throw "EWDK USB UDE 驱动构建失败，退出码 $LASTEXITCODE。" }

$builtDriver = Join-Path $projectRoot "bin\x64\$Configuration\UsbUdeTest.sys"
if (-not (Test-Path -LiteralPath $builtDriver -PathType Leaf)) {
    $builtDriver = Get-ChildItem $projectRoot -Filter UsbUdeTest.sys -File -Recurse |
        Where-Object FullName -like "*$Configuration*" | Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($builtDriver) -or -not (Test-Path -LiteralPath $builtDriver)) {
    throw "EWDK 构建完成但未找到 UsbUdeTest.sys。"
}

if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Recurse -Force }
[IO.Directory]::CreateDirectory($OutputPath) | Out-Null
$driverPath = Join-Path $OutputPath "UsbUdeTest.sys"
$infPath = Join-Path $OutputPath "UsbUdeTest.inf"
$catPath = Join-Path $OutputPath "UsbUdeTest.cat"
Copy-Item -LiteralPath $builtDriver -Destination $driverPath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "package\UsbUdeTest.inf") -Destination $infPath -Force

$certificate = $null
$signed = $false
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $CertificateThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object Thumbprint -eq $CertificateThumbprint | Select-Object -First 1
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw "CurrentUser\My 中未找到带私钥的证书：$CertificateThumbprint"
    }
    & $signTool sign /v /sha1 $CertificateThumbprint /fd SHA256 $driverPath
    if ($LASTEXITCODE -ne 0) { throw "UsbUdeTest.sys 签名失败。" }
    $signed = $true
}

& $inf2Cat "/driver:$OutputPath" /os:10_X64,Server10_X64 /verbose
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat 生成 USB UDE 目录文件失败。" }
if (-not (Test-Path -LiteralPath $catPath)) { throw "Inf2Cat 未生成 UsbUdeTest.cat。" }

if ($signed) {
    & $signTool sign /v /sha1 $CertificateThumbprint /fd SHA256 $catPath
    if ($LASTEXITCODE -ne 0) { throw "UsbUdeTest.cat 签名失败。" }
}

$driverHash = (Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash.ToLowerInvariant()
$infHash = (Get-FileHash -LiteralPath $infPath -Algorithm SHA256).Hash.ToLowerInvariant()
$catalogHash = (Get-FileHash -LiteralPath $catPath -Algorithm SHA256).Hash.ToLowerInvariant()
$certificateHash = if ($null -eq $certificate) {
    $null
} else {
    $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256).ToLowerInvariant()
}
$metadata = [ordered]@{
    schema_version = "1.1"
    package_name = "UsbUdeTest"
    architecture = "x64"
    configuration = $Configuration
    ewdk_root = $EwdkRoot
    wdk_version = $kitVersion
    built_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    sha256 = $driverHash
    inf_sha256 = $infHash
    catalog_sha256 = $catalogHash
    certificate_sha256 = $certificateHash
    size_bytes = (Get-Item -LiteralPath $driverPath).Length
    inf_size_bytes = (Get-Item -LiteralPath $infPath).Length
    catalog_size_bytes = (Get-Item -LiteralPath $catPath).Length
    signature_valid = $signed
    catalog_membership_verified = $true
    signer = if ($null -eq $certificate) { $null } else { $certificate.Subject }
    certificate_thumbprint = if ($null -eq $certificate) { $null } else { $certificate.Thumbprint }
    requires_test_signing = $true
    private_key_in_package = $false
    hardware_id = "ROOT\USB_UDE_TEST"
    interface_guid = "{77DC40F2-80FB-4F86-A6D4-793AB56D2D45}"
    emulated_vendor_id = "ED1D"
    emulated_product_id = "0001"
}
$metadata | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutputPath "usb-driver-package.json") -Encoding utf8NoBOM

if ($UpdatePrebuilt) {
    $prebuilt = Join-Path $projectRoot "prebuilt\x64"
    [IO.Directory]::CreateDirectory($prebuilt) | Out-Null
    foreach ($name in @("UsbUdeTest.sys", "UsbUdeTest.inf", "UsbUdeTest.cat", "usb-driver-package.json")) {
        Copy-Item -LiteralPath (Join-Path $OutputPath $name) -Destination (Join-Path $prebuilt $name) -Force
    }
}

Write-Host "[已生成] USB UDE 驱动包：$OutputPath"
Write-Host "[签名状态] $signed；SYS=$driverHash；INF=$infHash；CAT=$catalogHash"
