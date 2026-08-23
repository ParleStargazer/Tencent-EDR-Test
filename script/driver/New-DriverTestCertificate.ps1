#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Subject = "CN=Tencent EDR Test Driver",
    [string]$FriendlyName = "Tencent EDR Test Driver Code Signing",
    [ValidateRange(1, 10)]
    [int]$ValidYears = 5,
    [string]$OutputCer
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputCer)) {
    $OutputCer = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
}
$OutputCer = [IO.Path]::GetFullPath($OutputCer)
[IO.Directory]::CreateDirectory((Split-Path -Parent $OutputCer)) | Out-Null

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $certificate) {
    if (-not $PSCmdlet.ShouldProcess("Cert:\CurrentUser\My", "创建不可导出的驱动代码签名测试证书 $Subject")) { return }
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName $FriendlyName `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears($ValidYears)
}

if ($PSCmdlet.ShouldProcess($OutputCer, "导出公开证书（不含私钥）")) {
    Export-Certificate -Cert $certificate -FilePath $OutputCer -Type CERT -Force | Out-Null
}

[pscustomobject]@{
    Subject = $certificate.Subject
    Thumbprint = $certificate.Thumbprint
    NotAfter = $certificate.NotAfter.ToUniversalTime().ToString("O")
    HasPrivateKey = $certificate.HasPrivateKey
    PrivateKeyExported = $false
    PublicCertificatePath = $OutputCer
}
