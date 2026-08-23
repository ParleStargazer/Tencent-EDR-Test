#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$EwdkRoot = "F:\EWDK",
    [string]$CertificatePath,
    [string]$DriverPackagePath,
    [switch]$Apply,
    [switch]$InstallPackage,
    [switch]$SkipTestSigning,
    [switch]$Restart
)

$entry = Join-Path $PSScriptRoot "script\driver\Initialize-DriverTestEnvironment.ps1"
& $entry -EwdkRoot $EwdkRoot -CertificatePath $CertificatePath `
    -DriverPackagePath $DriverPackagePath -Apply:$Apply -InstallPackage:$InstallPackage `
    -SkipTestSigning:$SkipTestSigning -Restart:$Restart
exit $LASTEXITCODE
