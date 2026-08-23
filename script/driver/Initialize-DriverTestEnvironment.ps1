#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [string]$EwdkRoot = "F:\EWDK",
    [string]$CertificatePath,
    [string]$DriverPackagePath,
    [switch]$Apply,
    [switch]$InstallPackage,
    [switch]$SkipTestSigning,
    [switch]$Restart
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
}
if ([string]::IsNullOrWhiteSpace($DriverPackagePath)) {
    $DriverPackagePath = Join-Path $repositoryRoot "drivers\EdrTestDriver\prebuilt\x64"
}

Write-Host "[只读检查] 当前驱动测试环境" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Test-DriverEnvironment.ps1") `
    -EwdkRoot $EwdkRoot -CertificatePath $CertificatePath -DriverPackagePath $DriverPackagePath

if (-not $Apply) {
    Write-Host "未指定 -Apply：没有修改证书存储、BCD、驱动存储或重启状态。" -ForegroundColor Yellow
    Write-Host "准备好签名包后，以管理员身份使用 -Apply；如需 pnputil 预装 INF，再加 -InstallPackage。"
    return
}

$isAdministrator = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) { throw "-Apply 必须在管理员 PowerShell 7 中执行。" }
if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "公开证书不存在：$CertificatePath。先运行 New-DriverTestCertificate.ps1。"
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
foreach ($store in @("Root", "TrustedPublisher")) {
    $target = "Cert:\LocalMachine\$store"
    $present = Get-ChildItem $target | Where-Object Thumbprint -eq $certificate.Thumbprint | Select-Object -First 1
    if ($null -eq $present -and $PSCmdlet.ShouldProcess($target, "导入公开测试证书 $($certificate.Thumbprint)")) {
        Import-Certificate -FilePath $CertificatePath -CertStoreLocation $target | Out-Null
    }
}

if (-not $SkipTestSigning -and $PSCmdlet.ShouldProcess("当前 Windows 启动项", "执行 bcdedit /set testsigning on")) {
    & (Join-Path $env:SystemRoot "System32\bcdedit.exe") /set testsigning on
    if ($LASTEXITCODE -ne 0) { throw "bcdedit /set testsigning on 失败。Secure Boot 或组织策略可能阻止测试签名模式。" }
}

if ($InstallPackage) {
    $infPath = Join-Path $DriverPackagePath "EdrTestDriver.inf"
    if (-not (Test-Path -LiteralPath $infPath -PathType Leaf)) { throw "驱动 INF 不存在：$infPath" }
    if ($PSCmdlet.ShouldProcess($infPath, "使用 pnputil 添加驱动包到 Driver Store")) {
        & (Join-Path $env:SystemRoot "System32\pnputil.exe") /add-driver $infPath /install
        if ($LASTEXITCODE -ne 0) { throw "pnputil 添加驱动包失败。" }
    }
}

Write-Host "初始化操作已完成。testsigning 的变更通常需要重启后生效。" -ForegroundColor Green
if ($Restart -and $PSCmdlet.ShouldProcess("本机", "立即重启以应用测试签名设置")) {
    Restart-Computer -Force
}
