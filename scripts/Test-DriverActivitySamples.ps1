[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$EwdkRoot = "F:\EWDK",
    [switch]$SkipNativeRebuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$prebuilt = Join-Path $repositoryRoot "drivers\EdrTestDriver\prebuilt\x64"

$bcdBefore = & (Join-Path $env:SystemRoot "System32\bcdedit.exe") /enum "{current}" 2>&1
$trustedBefore = @(
    Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
        Where-Object Subject -eq "CN=Tencent EDR Test Driver"
).Count + @(
    Get-ChildItem Cert:\LocalMachine\TrustedPublisher -ErrorAction SilentlyContinue |
        Where-Object Subject -eq "CN=Tencent EDR Test Driver"
).Count

if (-not $SkipNativeRebuild) {
    & (Join-Path $repositoryRoot "script\driver\Build-DriverPackage.ps1") `
        -EwdkRoot $EwdkRoot -Configuration $Configuration -UpdatePrebuilt
    if ($LASTEXITCODE -ne 0) { throw "EWDK 原生驱动理论构建失败。" }
}

& (Join-Path $PSScriptRoot "Build-DriverActivitySamples.ps1") `
    -Configuration $Configuration -DriverPackagePath $prebuilt -SuppressPrivilegeWarning
if ($LASTEXITCODE -ne 0) { throw "驱动三项能力包构建失败。" }

$metadata = Get-Content (Join-Path $prebuilt "driver-package.json") -Raw | ConvertFrom-Json
$driverHash = (Get-FileHash (Join-Path $prebuilt "EdrTestDriver.sys") -Algorithm SHA256).Hash.ToLowerInvariant()
if ($driverHash -ne $metadata.sha256) { throw "理论包 SHA256 与元数据不一致。" }
if ($metadata.private_key_in_package) { throw "驱动包元数据错误地声明包含私钥。" }

$driverSource = Get-Content (Join-Path $repositoryRoot "drivers\EdrTestDriver\src\driver.c") -Raw
if ($driverSource -notmatch 'DriverEntry' -or $driverSource -notmatch 'DriverUnload') {
    throw "最小驱动缺少 DriverEntry 或 DriverUnload。"
}
if ($driverSource -match 'IoCreateDevice|IRP_MJ_|IOCTL|PsSet|ObRegister|CmRegister') {
    throw "最小驱动包含超出理论测试边界的设备、IOCTL 或回调逻辑。"
}

$scripts = @(
    "script\driver\New-DriverTestCertificate.ps1",
    "script\driver\Build-DriverPackage.ps1",
    "script\driver\Test-DriverEnvironment.ps1",
    "script\driver\Initialize-DriverTestEnvironment.ps1",
    "初始化驱动测试环境.ps1",
    "scripts\Build-DriverActivitySamples.ps1",
    "scripts\Test-DriverActivitySamples.ps1"
)
foreach ($relative in $scripts) {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repositoryRoot $relative), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "PowerShell 语法检查失败：$relative：$($parseErrors.Message -join ' | ')"
    }
}

dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw ".NET 解决方案构建失败。" }
dotnet run --project (Join-Path $repositoryRoot "tests\EdrTest.Tests\EdrTest.Tests.csproj") `
    --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "驱动映射/比较框架测试失败。" }
node --test (Join-Path $repositoryRoot "tests\contract\driver-activity-contract.test.mjs")
if ($LASTEXITCODE -ne 0) { throw "驱动静态契约测试失败。" }

$bcdAfter = & (Join-Path $env:SystemRoot "System32\bcdedit.exe") /enum "{current}" 2>&1
$trustedAfter = @(
    Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
        Where-Object Subject -eq "CN=Tencent EDR Test Driver"
).Count + @(
    Get-ChildItem Cert:\LocalMachine\TrustedPublisher -ErrorAction SilentlyContinue |
        Where-Object Subject -eq "CN=Tencent EDR Test Driver"
).Count
if (($bcdBefore -join "`n") -ne ($bcdAfter -join "`n") -or $trustedBefore -ne $trustedAfter) {
    throw "理论测试不应改变 BCD 或 LocalMachine 证书存储。"
}

Write-Host "[PASS] 驱动三项完成 EWDK/.NET 构建、BASSLINE/映射与静态安全验证；未加载驱动，未修改证书、BCD 或 Driver Store。"
