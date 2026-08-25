[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$ApiPort = 4317,
    [ValidateRange(1024, 65535)]
    [int]$WebPort = 3000,
    [switch]$SkipBuild,
    [ValidateSet("Incremental", "Full")]
    [string]$BuildMode = "Incremental",
    [switch]$PromptBuildMode,
    [switch]$NoBrowser,
    [string]$EwdkRoot = "F:\EWDK",
    [ValidateSet("Prompt", "Always", "Never")]
    [string]$DriverCertificateImportMode = "Prompt"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stateRoot = Join-Path $repositoryRoot ".edr-test"
$logRoot = Join-Path $stateRoot "logs"
$statePath = Join-Path $stateRoot "services.json"
$webRoot = Join-Path $repositoryRoot "web"
$runnerDll = Join-Path $repositoryRoot "src\EdrTest\bin\Release\net8.0-windows\EdrTest.dll"
$apiUrl = "http://127.0.0.1:$ApiPort"
$webUrl = "http://127.0.0.1:$WebPort"
$buildCacheRoot = Join-Path $stateRoot "build-cache"
$repositoryCapabilityFingerprintRoot = Join-Path $repositoryRoot "build-fingerprints\capabilities"

. (Join-Path $PSScriptRoot "Build-Cache.ps1")

function Read-EdrBuildMode {
    Write-Host ""
    Write-Host "请选择启动方式：" -ForegroundColor Cyan
    Write-Host "  [1] 增量启动（默认，仅重建发生变化的框架、能力包或前端）"
    Write-Host "  [2] 全量重构启动（忽略构建指纹，重新生成全部构建产物）"
    Write-Host ""

    try {
        if ([Console]::IsInputRedirected) {
            Write-Host "输入不可交互，自动选择增量启动。" -ForegroundColor DarkGray
            return "Incremental"
        }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
        $shownSecond = -1
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $remaining = [Math]::Max(1, [Math]::Ceiling(($deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
            if ($remaining -ne $shownSecond) {
                Write-Host "`r请按 1 或 2 选择，$remaining 秒后自动选择增量启动… " -NoNewline -ForegroundColor Yellow
                $shownSecond = $remaining
            }
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true).KeyChar
                if ($key -eq '1') {
                    Write-Host "`r已选择：增量启动。                              " -ForegroundColor Green
                    return "Incremental"
                }
                if ($key -eq '2') {
                    Write-Host "`r已选择：全量重构启动。                          " -ForegroundColor Green
                    return "Full"
                }
            }
            Start-Sleep -Milliseconds 50
        }
        Write-Host "`r倒计时结束，自动选择：增量启动。                  " -ForegroundColor Green
        return "Incremental"
    } catch {
        Write-Host ""
        Write-Warning "无法读取交互按键，自动选择增量启动：$($_.Exception.Message)"
        return "Incremental"
    }
}

function Test-ProcessAlive([int]$ProcessId) {
    return $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Test-PortAvailable([int]$Port) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
        return $true
    } catch {
        return $false
    } finally {
        $listener.Stop()
    }
}

function ConvertTo-NativeArgument([AllowEmptyString()][string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }

    $result = [System.Text.StringBuilder]::new()
    [void]$result.Append([char]'"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]'\') {
            $backslashCount++
            continue
        }
        if ($character -eq [char]'"') {
            [void]$result.Append([char]'\', (($backslashCount * 2) + 1))
            [void]$result.Append([char]'"')
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$result.Append([char]'\', $backslashCount)
            $backslashCount = 0
        }
        [void]$result.Append($character)
    }
    if ($backslashCount -gt 0) {
        [void]$result.Append([char]'\', ($backslashCount * 2))
    }
    [void]$result.Append([char]'"')
    return $result.ToString()
}

function Join-NativeArguments([object[]]$Arguments) {
    return (($Arguments | ForEach-Object { ConvertTo-NativeArgument ([string]$_) }) -join " ")
}

function Test-CurrentBootTestSigning {
    $bcdedit = Join-Path $env:SystemRoot "System32\bcdedit.exe"
    if (-not (Test-Path -LiteralPath $bcdedit -PathType Leaf)) {
        Write-Warning "找不到 bcdedit.exe，无法检测 testsigning。"
        return $false
    }
    try {
        $info = [System.Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $bcdedit
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        [void]$info.ArgumentList.Add("/enum")
        [void]$info.ArgumentList.Add("{current}")
        $process = [System.Diagnostics.Process]::Start($info)
        if ($null -eq $process) { throw "无法启动 bcdedit.exe。" }
        try {
            $output = $process.StandardOutput.ReadToEnd()
            $errorOutput = $process.StandardError.ReadToEnd()
            if (-not $process.WaitForExit(10000)) {
                $process.Kill($true)
                throw "bcdedit 环境检测超时。"
            }
            if ($process.ExitCode -ne 0) { throw "bcdedit 环境检测失败：$errorOutput" }
            return $output -match '(?im)^\s*testsigning\s+(Yes|On|是|开启)\s*$'
        } finally {
            $process.Dispose()
        }
    } catch {
        Write-Warning "无法检测当前启动项的 testsigning 状态：$($_.Exception.Message)"
        return $false
    }
}

function Test-LocalMachineCertificate([string]$StoreName, [string]$Thumbprint) {
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) { return $false }
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        $StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        return $store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $Thumbprint,
            $false).Count -gt 0
    } catch {
        return $false
    } finally {
        $store.Dispose()
    }
}

function Import-DriverTestCertificate(
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
) {
    foreach ($storeName in @("Root", "TrustedPublisher")) {
        if (Test-LocalMachineCertificate $storeName $Certificate.Thumbprint) { continue }
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            $storeName,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        try {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $store.Add($Certificate)
        } finally {
            $store.Dispose()
        }
    }
}

function Get-DriverPackageValidation(
    [string]$PackagePath,
    [string]$CertificatePath,
    [string]$DriverFileName = "EdrTestDriver.sys",
    [string]$InfFileName = "EdrTestDriver.inf",
    [string]$CatalogFileName = "EdrTestDriver.cat",
    [string]$MetadataFileName = "driver-package.json",
    [switch]$RequireCatalogIntegrity
) {
    $required = @($DriverFileName, $InfFileName, $CatalogFileName, $MetadataFileName)
    $missing = @($required | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $PackagePath $_) -PathType Leaf)
    })
    if ($missing.Count -gt 0) {
        return [pscustomobject]@{ Available = $false; Reason = "缺少文件：$($missing -join ', ')" }
    }
    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        return [pscustomobject]@{ Available = $false; Reason = "缺少仅含公钥的 EdrTestDriverTest.cer" }
    }

    $certificate = $null
    try {
        $metadata = Get-Content -LiteralPath (Join-Path $PackagePath $MetadataFileName) -Raw |
            ConvertFrom-Json
        $driverPath = Join-Path $PackagePath $DriverFileName
        $infPath = Join-Path $PackagePath $InfFileName
        $catalogPath = Join-Path $PackagePath $CatalogFileName
        $metadataPath = Join-Path $PackagePath $MetadataFileName
        $actualHash = (Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($metadata.signature_valid -ne $true) {
            return [pscustomobject]@{ Available = $false; Reason = "$MetadataFileName 未声明签名包" }
        }
        if ([string]::IsNullOrWhiteSpace([string]$metadata.sha256) -or $actualHash -ne $metadata.sha256) {
            return [pscustomobject]@{ Available = $false; Reason = "SYS 的 SHA256 与元数据不一致" }
        }
        $expectedThumbprint = ([string]$metadata.certificate_thumbprint).Replace(" ", "").ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($expectedThumbprint)) {
            return [pscustomobject]@{ Available = $false; Reason = "元数据缺少签名证书指纹" }
        }

        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
        if ($certificate.HasPrivateKey) {
            return [pscustomobject]@{ Available = $false; Reason = "仓库分发证书包含私钥，已拒绝使用" }
        }
        if ($certificate.Thumbprint -ne $expectedThumbprint) {
            return [pscustomobject]@{ Available = $false; Reason = "公开证书指纹与元数据不一致" }
        }
        if ($RequireCatalogIntegrity) {
            $actualInfHash = (Get-FileHash -LiteralPath $infPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $actualCatalogHash = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $actualCertificateHash = $certificate.GetCertHashString(
                [Security.Cryptography.HashAlgorithmName]::SHA256).ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace([string]$metadata.inf_sha256) `
                -or $actualInfHash -ne [string]$metadata.inf_sha256) {
                return [pscustomobject]@{ Available = $false; Reason = "INF 的 SHA256 与签名包元数据不一致；文件可能被 Git 换行转换" }
            }
            if ([string]::IsNullOrWhiteSpace([string]$metadata.catalog_sha256) `
                -or $actualCatalogHash -ne [string]$metadata.catalog_sha256) {
                return [pscustomobject]@{ Available = $false; Reason = "CAT 的 SHA256 与签名包元数据不一致" }
            }
            if ([string]::IsNullOrWhiteSpace([string]$metadata.certificate_sha256) `
                -or $actualCertificateHash -ne [string]$metadata.certificate_sha256) {
                return [pscustomobject]@{ Available = $false; Reason = "公开 CER 的 SHA256 与签名包元数据不一致" }
            }
            if ($metadata.catalog_membership_verified -ne $true) {
                return [pscustomobject]@{ Available = $false; Reason = "元数据未声明 INF/SYS 已纳入 CAT" }
            }
        }
        $driverSignature = Get-AuthenticodeSignature -LiteralPath $driverPath
        $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalogPath
        if ($null -eq $driverSignature.SignerCertificate `
            -or $driverSignature.SignerCertificate.Thumbprint -ne $expectedThumbprint) {
            return [pscustomobject]@{ Available = $false; Reason = "SYS 未包含预期的嵌入式签名" }
        }
        if ($null -eq $catalogSignature.SignerCertificate `
            -or $catalogSignature.SignerCertificate.Thumbprint -ne $expectedThumbprint) {
            return [pscustomobject]@{ Available = $false; Reason = "CAT 未包含预期签名" }
        }
        return [pscustomobject]@{
            Available = $true
            Reason = $null
            PackagePath = [IO.Path]::GetFullPath($PackagePath)
            CertificatePath = [IO.Path]::GetFullPath($CertificatePath)
            Thumbprint = $expectedThumbprint
            DriverPath = [IO.Path]::GetFullPath($driverPath)
            InfPath = [IO.Path]::GetFullPath($infPath)
            CatalogPath = [IO.Path]::GetFullPath($catalogPath)
            MetadataPath = [IO.Path]::GetFullPath($metadataPath)
        }
    } catch {
        return [pscustomobject]@{ Available = $false; Reason = $_.Exception.Message }
    } finally {
        if ($null -ne $certificate) { $certificate.Dispose() }
    }
}

function Get-DriverPackageTrustValidation([psobject]$DriverPackage) {
    if ($null -eq $DriverPackage -or -not $DriverPackage.Available) {
        return [pscustomobject]@{ Ready = $false; DriverStatus = "Unavailable"; CatalogStatus = "Unavailable"; Reason = "驱动包不可用" }
    }
    try {
        $driverSignature = Get-AuthenticodeSignature -LiteralPath $DriverPackage.DriverPath
        $catalogSignature = Get-AuthenticodeSignature -LiteralPath $DriverPackage.CatalogPath
        $ready = $driverSignature.Status -eq [System.Management.Automation.SignatureStatus]::Valid `
            -and $catalogSignature.Status -eq [System.Management.Automation.SignatureStatus]::Valid
        $reason = if ($ready) { $null } else {
            "SYS=$($driverSignature.Status)：$($driverSignature.StatusMessage)；CAT=$($catalogSignature.Status)：$($catalogSignature.StatusMessage)"
        }
        return [pscustomobject]@{
            Ready = $ready
            DriverStatus = [string]$driverSignature.Status
            CatalogStatus = [string]$catalogSignature.Status
            Reason = $reason
        }
    } catch {
        return [pscustomobject]@{ Ready = $false; DriverStatus = "Error"; CatalogStatus = "Error"; Reason = $_.Exception.Message }
    }
}

function Resolve-DriverTestPackage([string]$DevelopmentKitRoot) {
    $repositoryPackage = Join-Path $repositoryRoot "drivers\EdrTestDriver\prebuilt\x64"
    $repositoryCertificate = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
    $repositoryResult = Get-DriverPackageValidation $repositoryPackage $repositoryCertificate
    if ($repositoryResult.Available) {
        $repositoryResult | Add-Member -NotePropertyName Source -NotePropertyValue "repository-prebuilt"
        Write-Host "[驱动包] 使用仓库预构建的已签名 SYS/CAT 和公开证书，无需 EWDK。" -ForegroundColor Green
        return $repositoryResult
    }

    Write-Warning "仓库预构建驱动包不可用：$($repositoryResult.Reason)；开始探测 EWDK。"
    $developmentKitFullPath = [IO.Path]::GetFullPath($DevelopmentKitRoot)
    $setupPath = [IO.Path]::Combine($developmentKitFullPath, "BuildEnv", "SetupBuildEnv.cmd")
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        return [pscustomobject]@{
            Available = $false
            Source = "unavailable"
            Reason = "仓库预构建包不可用，且 $DevelopmentKitRoot 不是可用的 EWDK 环境"
        }
    }

    $fallbackRoot = Join-Path $stateRoot "driver-fallback"
    $fallbackCertificate = Join-Path $fallbackRoot "cert\EdrTestDriverTest.cer"
    $fallbackPackage = Join-Path $fallbackRoot "package"
    try {
        Write-Host "[驱动包] 检测到 EWDK，尝试在本地运行目录构建签名驱动包…" -ForegroundColor Cyan
        $certificate = & (Join-Path $repositoryRoot "script\driver\New-DriverTestCertificate.ps1") `
            -OutputCer $fallbackCertificate -Confirm:$false
        if ($null -eq $certificate -or [string]::IsNullOrWhiteSpace([string]$certificate.Thumbprint)) {
            throw "未能取得带不可导出私钥的测试代码签名证书。"
        }
        & (Join-Path $repositoryRoot "script\driver\Build-DriverPackage.ps1") `
            -EwdkRoot $DevelopmentKitRoot -Configuration Release `
            -CertificateThumbprint $certificate.Thumbprint -OutputPath $fallbackPackage
        if ($LASTEXITCODE -ne 0) { throw "EWDK 驱动包构建失败，退出码 $LASTEXITCODE。" }
        $fallbackResult = Get-DriverPackageValidation $fallbackPackage $fallbackCertificate
        if (-not $fallbackResult.Available) { throw $fallbackResult.Reason }
        $fallbackResult | Add-Member -NotePropertyName Source -NotePropertyValue "ewdk-fallback"
        Write-Host "[驱动包] EWDK 后备构建成功。" -ForegroundColor Green
        return $fallbackResult
    } catch {
        return [pscustomobject]@{
            Available = $false
            Source = "unavailable"
            Reason = "仓库预构建包不可用，EWDK 后备构建也失败：$($_.Exception.Message)"
        }
    }
}

function Resolve-UsbUdeTestPackage([string]$DevelopmentKitRoot) {
    $repositoryPackage = Join-Path $repositoryRoot "drivers\UsbUdeTest\prebuilt\x64"
    $repositoryCertificate = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"
    $validationArguments = @{
        PackagePath = $repositoryPackage
        CertificatePath = $repositoryCertificate
        DriverFileName = "UsbUdeTest.sys"
        InfFileName = "UsbUdeTest.inf"
        CatalogFileName = "UsbUdeTest.cat"
        MetadataFileName = "usb-driver-package.json"
        RequireCatalogIntegrity = $true
    }
    $repositoryResult = Get-DriverPackageValidation @validationArguments
    if ($repositoryResult.Available) {
        $repositoryResult | Add-Member -NotePropertyName Source -NotePropertyValue "repository-prebuilt"
        Write-Host "[USB UDE 驱动包] 使用仓库预构建的已签名 SYS/CAT 和公开证书，无需 EWDK。" -ForegroundColor Green
        return $repositoryResult
    }

    Write-Warning "仓库预构建 USB UDE 驱动包不可用：$($repositoryResult.Reason)；开始探测 EWDK。"
    $developmentKitFullPath = [IO.Path]::GetFullPath($DevelopmentKitRoot)
    $setupPath = [IO.Path]::Combine($developmentKitFullPath, "BuildEnv", "SetupBuildEnv.cmd")
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        return [pscustomobject]@{
            Available = $false
            Source = "unavailable"
            Reason = "仓库预构建 USB UDE 包不可用，且 $DevelopmentKitRoot 不是可用的 EWDK 环境"
        }
    }

    $fallbackRoot = Join-Path $stateRoot "usb-ude-driver-fallback"
    $fallbackCertificate = Join-Path $fallbackRoot "cert\EdrTestDriverTest.cer"
    $fallbackPackage = Join-Path $fallbackRoot "package"
    try {
        Write-Host "[USB UDE 驱动包] 检测到 EWDK，尝试在本地运行目录构建签名驱动包…" -ForegroundColor Cyan
        $certificate = & (Join-Path $repositoryRoot "script\driver\New-DriverTestCertificate.ps1") `
            -OutputCer $fallbackCertificate -Confirm:$false
        if ($null -eq $certificate -or [string]::IsNullOrWhiteSpace([string]$certificate.Thumbprint)) {
            throw "未能取得带不可导出私钥的测试代码签名证书。"
        }
        & (Join-Path $repositoryRoot "script\driver\Build-UsbUdeDriverPackage.ps1") `
            -EwdkRoot $DevelopmentKitRoot -Configuration Release `
            -CertificateThumbprint $certificate.Thumbprint -OutputPath $fallbackPackage
        if ($LASTEXITCODE -ne 0) { throw "EWDK USB UDE 驱动包构建失败，退出码 $LASTEXITCODE。" }
        $validationArguments.PackagePath = $fallbackPackage
        $validationArguments.CertificatePath = $fallbackCertificate
        $fallbackResult = Get-DriverPackageValidation @validationArguments
        if (-not $fallbackResult.Available) { throw $fallbackResult.Reason }
        $fallbackResult | Add-Member -NotePropertyName Source -NotePropertyValue "ewdk-fallback"
        Write-Host "[USB UDE 驱动包] EWDK 后备构建成功。" -ForegroundColor Green
        return $fallbackResult
    } catch {
        return [pscustomobject]@{
            Available = $false
            Source = "unavailable"
            Reason = "仓库预构建 USB UDE 包不可用，EWDK 后备构建也失败：$($_.Exception.Message)"
        }
    }
}

function Remove-DriverActivitySamplePackages {
    $samplesRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "samples"))
    foreach ($capabilityId in @("win.driver.load", "win.driver.modify", "win.driver.unload")) {
        $target = [IO.Path]::GetFullPath((Join-Path $samplesRoot $capabilityId))
        $relative = [IO.Path]::GetRelativePath($samplesRoot, $target)
        if ($relative.StartsWith("..", [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
            throw "拒绝清理 samples 根目录之外的驱动能力包：$target"
        }
        if (Test-Path -LiteralPath $target -PathType Container) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}

function Remove-UsbDeviceActivitySamplePackages {
    $samplesRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "samples"))
    foreach ($capabilityId in @("win.device.usb.mount", "win.device.usb.unmount")) {
        $target = [IO.Path]::GetFullPath((Join-Path $samplesRoot $capabilityId))
        $relative = [IO.Path]::GetRelativePath($samplesRoot, $target)
        if ($relative.StartsWith("..", [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
            throw "拒绝清理 samples 根目录之外的 USB 设备能力包：$target"
        }
        if (Test-Path -LiteralPath $target -PathType Container) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}

function Test-DriverEnvironmentAtStartup(
    [bool]$IsAdministrator,
    [string]$ImportMode,
    [psobject]$DriverPackage
) {
    $testSigningEnabled = Test-CurrentBootTestSigning
    if (-not $DriverPackage.Available) {
        Write-Warning "$($DriverPackage.Reason)。三项驱动能力将从本次能力包中跳过，平台其他能力不受影响。"
        $administratorText = if ($IsAdministrator) { "是" } else { "否" }
        $testSigningText = if ($testSigningEnabled) { "已开启" } else { "未开启" }
        Write-Host "[驱动环境] 管理员=$administratorText；testsigning=$testSigningText；驱动包=不可用。" -ForegroundColor Cyan
        return
    }
    $metadataPath = Join-Path $DriverPackage.PackagePath "driver-package.json"
    $certificatePath = $DriverPackage.CertificatePath
    $metadata = $null
    try {
        if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
            $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        }
    } catch {
        Write-Warning "驱动包元数据无法读取：$($_.Exception.Message)"
    }

    $packageSigned = $null -ne $metadata -and $metadata.signature_valid -eq $true
    $expectedThumbprint = if ($null -eq $metadata) { $null } else { [string]$metadata.certificate_thumbprint }
    $certificate = $null
    $certificateMatchesPackage = $false
    try {
        if (Test-Path -LiteralPath $certificatePath -PathType Leaf) {
            $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
            $certificateMatchesPackage = $packageSigned `
                -and -not [string]::IsNullOrWhiteSpace($expectedThumbprint) `
                -and $certificate.Thumbprint -eq $expectedThumbprint.Replace(" ", "").ToUpperInvariant()
        }

        $certificateTrusted = $false
        if ($certificateMatchesPackage) {
            $certificateTrusted = (Test-LocalMachineCertificate "Root" $certificate.Thumbprint) `
                -and (Test-LocalMachineCertificate "TrustedPublisher" $certificate.Thumbprint)
        }

        if ($certificateMatchesPackage -and -not $certificateTrusted) {
            if (-not $IsAdministrator) {
                Write-Warning "测试用公开证书尚未导入 LocalMachine\Root 和 LocalMachine\TrustedPublisher；当前不是管理员，无法导入。"
            } else {
                $shouldImport = $ImportMode -eq "Always"
                if ($ImportMode -eq "Prompt") {
                    $canPrompt = [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
                    if ($canPrompt) {
                        $answer = Read-Host "是否导入测试用证书到 LocalMachine\Root 和 LocalMachine\TrustedPublisher？[y/N]"
                        $shouldImport = $answer -match '^(?i:y|yes|是)$'
                    } else {
                        Write-Warning "当前会话不能交互询问是否导入测试用证书；可在交互式终端启动，或使用 -DriverCertificateImportMode Always。"
                    }
                }
                if ($shouldImport) {
                    try {
                        Import-DriverTestCertificate $certificate
                        $certificateTrusted = (Test-LocalMachineCertificate "Root" $certificate.Thumbprint) `
                            -and (Test-LocalMachineCertificate "TrustedPublisher" $certificate.Thumbprint)
                        if ($certificateTrusted) {
                            Write-Host "[驱动环境] 测试用公开证书已导入两个 LocalMachine 信任区。" -ForegroundColor Green
                        } else {
                            Write-Warning "测试用公开证书导入后未能在两个 LocalMachine 信任区中确认。"
                        }
                    } catch {
                        Write-Warning "导入测试用公开证书失败：$($_.Exception.Message)"
                    }
                } else {
                    Write-Warning "未导入测试用公开证书；驱动加载与卸载能力不可用。"
                }
            }
        } elseif (-not $packageSigned) {
            Write-Warning "当前预构建驱动包未签名，无法准备有效的测试证书信任；驱动加载与卸载能力不可用。"
        } elseif (-not $certificateMatchesPackage) {
            Write-Warning "测试用公开证书缺失或与签名驱动包指纹不一致；驱动加载与卸载能力不可用。"
        }

        $trustValidation = Get-DriverPackageTrustValidation $DriverPackage
        if ($certificateTrusted -and -not $trustValidation.Ready) {
            Write-Warning "驱动 SYS/CAT 未通过当前 Windows 信任链校验：$($trustValidation.Reason)"
        }
        $administratorText = if ($IsAdministrator) { "是" } else { "否" }
        $testSigningText = if ($testSigningEnabled) { "已开启" } else { "未开启" }
        $certificateText = if ($certificateTrusted) { "已导入" } else { "未就绪" }
        $signatureText = if ($trustValidation.Ready) { "有效" } else { "无效" }
        $environmentReady = $IsAdministrator -and $testSigningEnabled -and $certificateTrusted -and $trustValidation.Ready
        $readinessText = if ($environmentReady) { "通过" } else { "未通过" }
        Write-Host "[驱动环境] 驱动包=$($DriverPackage.Source)；完整性=通过；SYS/CAT 信任链=$signatureText；管理员=$administratorText；testsigning=$testSigningText；测试证书=$certificateText；运行预检=$readinessText。" -ForegroundColor Cyan
        if (-not $testSigningEnabled) {
            Write-Warning "当前启动项未开启 testsigning。平台不会自动修改启动配置；不开启会导致驱动加载与卸载能力不可用。"
        }
        if (-not $IsAdministrator) {
            Write-Warning "驱动三项能力要求管理员权限，当前运行中将不可用。"
        }
    } finally {
        if ($null -ne $certificate) { $certificate.Dispose() }
    }
}

function Test-UsbDeviceEnvironmentAtStartup(
    [bool]$IsAdministrator,
    [string]$ImportMode,
    [psobject]$DriverPackage,
    [bool]$AllowCertificatePrompt
) {
    $testSigningEnabled = Test-CurrentBootTestSigning
    if (-not $DriverPackage.Available) {
        Write-Warning "$($DriverPackage.Reason)。USB 挂载与卸载能力将从本次能力包中跳过，平台其他能力不受影响。"
        $administratorText = if ($IsAdministrator) { "是" } else { "否" }
        $testSigningText = if ($testSigningEnabled) { "已开启" } else { "未开启" }
        Write-Host "[USB UDE 环境] 管理员=$administratorText；testsigning=$testSigningText；驱动包=不可用。" -ForegroundColor Cyan
        return
    }

    $metadataPath = Join-Path $DriverPackage.PackagePath "usb-driver-package.json"
    $certificatePath = $DriverPackage.CertificatePath
    $metadata = $null
    try {
        if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
            $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        }
    } catch {
        Write-Warning "USB UDE 驱动包元数据无法读取：$($_.Exception.Message)"
    }

    $packageSigned = $null -ne $metadata -and $metadata.signature_valid -eq $true
    $expectedThumbprint = if ($null -eq $metadata) { $null } else { [string]$metadata.certificate_thumbprint }
    $certificate = $null
    $certificateMatchesPackage = $false
    try {
        if (Test-Path -LiteralPath $certificatePath -PathType Leaf) {
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
            $certificateMatchesPackage = $packageSigned `
                -and -not $certificate.HasPrivateKey `
                -and -not [string]::IsNullOrWhiteSpace($expectedThumbprint) `
                -and $certificate.Thumbprint -eq $expectedThumbprint.Replace(" ", "").ToUpperInvariant()
        }

        $certificateTrusted = $false
        if ($certificateMatchesPackage) {
            $certificateTrusted = (Test-LocalMachineCertificate "Root" $certificate.Thumbprint) `
                -and (Test-LocalMachineCertificate "TrustedPublisher" $certificate.Thumbprint)
        }

        if ($certificateMatchesPackage -and -not $certificateTrusted) {
            if (-not $IsAdministrator) {
                Write-Warning "USB UDE 测试用公开证书尚未导入 LocalMachine\Root 和 LocalMachine\TrustedPublisher；当前不是管理员，无法导入。"
            } elseif ($AllowCertificatePrompt) {
                $shouldImport = $ImportMode -eq "Always"
                if ($ImportMode -eq "Prompt") {
                    $canPrompt = [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
                    if ($canPrompt) {
                        $answer = Read-Host "是否导入 USB UDE 测试用证书到 LocalMachine\Root 和 LocalMachine\TrustedPublisher？[y/N]"
                        $shouldImport = $answer -match '^(?i:y|yes|是)$'
                    } else {
                        Write-Warning "当前会话不能交互询问是否导入 USB UDE 测试证书；可在交互式终端启动，或使用 -DriverCertificateImportMode Always。"
                    }
                }
                if ($shouldImport) {
                    try {
                        Import-DriverTestCertificate $certificate
                        $certificateTrusted = (Test-LocalMachineCertificate "Root" $certificate.Thumbprint) `
                            -and (Test-LocalMachineCertificate "TrustedPublisher" $certificate.Thumbprint)
                        if ($certificateTrusted) {
                            Write-Host "[USB UDE 环境] 测试用公开证书已导入两个 LocalMachine 信任区。" -ForegroundColor Green
                        } else {
                            Write-Warning "USB UDE 测试用公开证书导入后未能在两个 LocalMachine 信任区中确认。"
                        }
                    } catch {
                        Write-Warning "导入 USB UDE 测试用公开证书失败：$($_.Exception.Message)"
                    }
                } else {
                    Write-Warning "未导入 USB UDE 测试用公开证书；USB 挂载与卸载能力不可用。"
                }
            } else {
                Write-Warning "共享测试证书仍未就绪；USB 挂载与卸载能力不可用。"
            }
        } elseif (-not $packageSigned) {
            Write-Warning "当前预构建 USB UDE 驱动包未签名；USB 挂载与卸载能力不可用。"
        } elseif (-not $certificateMatchesPackage) {
            Write-Warning "USB UDE 公开证书缺失、包含私钥或与签名包指纹不一致；USB 挂载与卸载能力不可用。"
        }

        $trustValidation = Get-DriverPackageTrustValidation $DriverPackage
        if ($certificateTrusted -and -not $trustValidation.Ready) {
            Write-Warning "USB UDE SYS/CAT 未通过当前 Windows 信任链校验：$($trustValidation.Reason)"
        }
        $administratorText = if ($IsAdministrator) { "是" } else { "否" }
        $testSigningText = if ($testSigningEnabled) { "已开启" } else { "未开启" }
        $certificateText = if ($certificateTrusted) { "已导入" } else { "未就绪" }
        $signatureText = if ($trustValidation.Ready) { "有效" } else { "无效" }
        $environmentReady = $IsAdministrator -and $testSigningEnabled -and $certificateTrusted -and $trustValidation.Ready
        $readinessText = if ($environmentReady) { "通过" } else { "未通过" }
        Write-Host "[USB UDE 环境] 驱动包=$($DriverPackage.Source)；完整性=通过；INF/CAT 字节校验=通过；SYS/CAT 信任链=$signatureText；管理员=$administratorText；testsigning=$testSigningText；测试证书=$certificateText；运行预检=$readinessText。" -ForegroundColor Cyan
        if (-not $testSigningEnabled) {
            Write-Warning "当前启动项未开启 testsigning。平台不会自动修改启动配置；不开启会导致 USB 挂载与卸载能力不可用。"
        }
        if (-not $IsAdministrator) {
            Write-Warning "USB 挂载与卸载能力要求管理员权限，当前运行中将不可用。"
        }
    } finally {
        if ($null -ne $certificate) { $certificate.Dispose() }
    }
}

if (Test-Path $statePath) {
    try {
        $existing = Get-Content $statePath -Raw | ConvertFrom-Json
        if ((Test-ProcessAlive ([int]$existing.backend_pid)) -or (Test-ProcessAlive ([int]$existing.frontend_pid))) {
            Write-Host "平台已经启动：$($existing.web_url)" -ForegroundColor Yellow
            Write-Host "如需重启，请先运行 scripts\Stop-EdrTest.ps1。"
            if (-not $NoBrowser) { Start-Process $existing.web_url }
            exit 0
        }
    } catch {
        Write-Warning "旧状态文件无法读取，将创建新状态：$($_.Exception.Message)"
    }
}

if ($PromptBuildMode) {
    $BuildMode = Read-EdrBuildMode
}
if ($SkipBuild -and $BuildMode -eq "Full") {
    throw "-SkipBuild 与 -BuildMode Full 不能同时使用。"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) { throw "未找到 .NET 8 SDK。" }
$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if ($null -eq $pwsh) { throw "未找到 PowerShell 7（pwsh）。" }
$pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
if ($null -eq $pnpm) { throw "未找到 pnpm。请安装 Node.js 22.13+ 和 pnpm 11.9+。" }
$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $node) { throw "未找到 Node.js。云端日志自动下载与前端构建都需要 Node.js 22.13+。" }
if (-not (Test-PortAvailable $ApiPort)) { throw "API 端口 $ApiPort 已被占用，请先停止占用程序或使用 -ApiPort 指定其他端口。" }
if (-not (Test-PortAvailable $WebPort)) { throw "前端端口 $WebPort 已被占用，请先停止占用程序或使用 -WebPort 指定其他端口。" }

$isAdministrator = $false
try {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    $isAdministrator = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $identity.Dispose()
} catch {
    Write-Warning "无法确认当前 PowerShell 的管理员权限：$($_.Exception.Message)"
}
if (-not $isAdministrator) {
    Write-Warning "当前平台未以管理员身份运行。建议关闭后使用管理员权限重新运行 scripts\Start-EdrTest.ps1；五项用户账号活动、三项计划任务安全审计、三项服务活动、组策略修改和三项 WMI permanent subscription 需要管理员权限；虚拟磁盘挂载测试需要管理员权限；USB 挂载与卸载以及三项驱动活动也需要管理员权限，否则会被跳过或不可用。"
}
$driverPackage = Resolve-DriverTestPackage -DevelopmentKitRoot $EwdkRoot
$usbDriverPackage = Resolve-UsbUdeTestPackage -DevelopmentKitRoot $EwdkRoot
[void](Test-DriverEnvironmentAtStartup -IsAdministrator $isAdministrator `
    -ImportMode $DriverCertificateImportMode -DriverPackage $driverPackage)
[void](Test-UsbDeviceEnvironmentAtStartup -IsAdministrator $isAdministrator `
    -ImportMode $DriverCertificateImportMode -DriverPackage $usbDriverPackage `
    -AllowCertificatePrompt (-not $driverPackage.Available))
if (-not $driverPackage.Available) { Remove-DriverActivitySamplePackages }
if (-not $usbDriverPackage.Available) { Remove-UsbDeviceActivitySamplePackages }

[System.IO.Directory]::CreateDirectory($logRoot) | Out-Null

if (-not $SkipBuild) {
    [System.IO.Directory]::CreateDirectory($buildCacheRoot) | Out-Null
    $dotnetVersion = ((& $dotnet.Source --version) | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
        throw "无法读取 .NET SDK 版本。"
    }
    $forceBuild = $BuildMode -eq "Full"
    $modeTitle = if ($forceBuild) { "全量重构" } else { "增量" }
    Write-Host "[构建模式] $modeTitle 启动" -ForegroundColor Cyan

    $runnerOutputDirectory = Split-Path -Parent $runnerDll
    $runnerCachePath = Join-Path $buildCacheRoot "runner.json"
    $runnerFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot -InputPaths @(
        "Directory.Build.props",
        "src\EdrTest",
        "schemas\run-db.sql"
    ) -Properties @{
        cache_contract = "2"
        configuration = "Release"
        dotnet_sdk = $dotnetVersion
    }
    $runnerIsCurrent = -not $forceBuild -and (Test-EdrBuildCache `
        -CachePath $runnerCachePath -Fingerprint $runnerFingerprint `
        -DirectoryPaths @($runnerOutputDirectory) -RequiredFiles @($runnerDll))

    $capabilitySharedSourceInputs = @(Get-EdrCapabilitySharedSourceInputs)
    $sharedSampleFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot `
        -InputPaths $capabilitySharedSourceInputs -Properties @{
        cache_contract = "3"
        configuration = "Release"
        dotnet_sdk = $dotnetVersion
    }
    $repositorySharedSampleFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot `
        -InputPaths $capabilitySharedSourceInputs -Properties @{
        cache_contract = "repository-capability-source-v2"
        configuration = "Release"
    }

    $capabilityBuilds = @(
        [pscustomobject]@{ Key = "process"; Title = "进程活动"; Source = "ProcessActivity"; Script = "Build-ProcessActivitySamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "file"; Title = "文件操作"; Source = "FileManipulation"; Script = "Build-FileManipulationSamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "hash"; Title = "哈希算法"; Source = "HashAlgorithms"; Script = "Build-HashAlgorithmsSamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "account"; Title = "用户账号活动"; Source = "UserAccountActivity"; Script = "Build-UserAccountActivitySamples.ps1"; Arguments = @("-SuppressPrivilegeWarning"); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "network"; Title = "网络活动"; Source = "NetworkActivity"; Script = "Build-NetworkActivitySamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "registry"; Title = "注册表活动"; Source = "RegistryActivity"; Script = "Build-RegistryActivitySamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "scheduled-task"; Title = "计划任务活动"; Source = "ScheduledTaskActivity"; Script = "Build-ScheduledTaskActivitySamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "service"; Title = "服务活动"; Source = "ServiceActivity"; Script = "Build-ServiceActivitySamples.ps1"; Arguments = @("-SuppressPrivilegeWarning"); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "group-policy"; Title = "组策略修改"; Source = "GroupPolicyActivity"; Script = "Build-GroupPolicyActivitySamples.ps1"; Arguments = @("-SuppressPrivilegeWarning"); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "named-pipe"; Title = "命名管道活动"; Source = "NamedPipeActivity"; Script = "Build-NamedPipeActivitySamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "powershell"; Title = "PowerShell 活动"; Source = "PowerShellActivity"; Script = "Build-PowerShellActivitySamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "bits"; Title = "BITS 活动"; Source = "BitsActivity"; Script = "Build-BitsActivitySamples.ps1"; Arguments = @(); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "wmi"; Title = "WMI 活动"; Source = "WmiActivity"; Script = "Build-WmiActivitySamples.ps1"; Arguments = @("-SuppressPrivilegeWarning"); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "virtual-disk"; Title = "虚拟磁盘挂载"; Source = "VirtualDiskActivity"; Script = "Build-VirtualDiskActivitySamples.ps1"; Arguments = @("-SuppressPrivilegeWarning"); Enabled = $true; ContinueOnFailure = $false },
        [pscustomobject]@{ Key = "driver"; Title = "驱动活动"; Source = "DriverActivity"; Script = "Build-DriverActivitySamples.ps1"; Arguments = @("-DriverPackagePath", $driverPackage.PackagePath, "-DriverCertificatePath", $driverPackage.CertificatePath, "-EwdkRoot", $EwdkRoot, "-SuppressPrivilegeWarning"); Enabled = [bool]$driverPackage.Available; ContinueOnFailure = $true },
        [pscustomobject]@{ Key = "usb-device"; Title = "USB 设备活动"; Source = "UsbDeviceActivity"; Script = "Build-UsbDeviceActivitySamples.ps1"; Arguments = @("-UsbDriverPackagePath", $usbDriverPackage.PackagePath, "-DriverCertificatePath", $usbDriverPackage.CertificatePath, "-EwdkRoot", $EwdkRoot, "-SuppressPrivilegeWarning"); Enabled = [bool]$usbDriverPackage.Available; ContinueOnFailure = $true }
    )

    foreach ($definition in $capabilityBuilds) {
        $sourceDirectory = Join-Path $repositoryRoot "sample-src\$($definition.Source)"
        $manifestRoot = Join-Path $sourceDirectory "manifests"
        $packagePaths = if (Test-Path -LiteralPath $manifestRoot -PathType Container) {
            @(Get-ChildItem -LiteralPath $manifestRoot -Directory | Sort-Object Name | ForEach-Object {
                Join-Path $repositoryRoot "samples\$($_.Name)"
            })
        } else {
            @()
        }
        $extraInputs = @()
        if ($definition.Key -eq "driver" -and $definition.Enabled) {
            $extraInputs += @($driverPackage.PackagePath, $driverPackage.CertificatePath)
        }
        if ($definition.Key -eq "usb-device" -and $definition.Enabled) {
            $extraInputs += @($usbDriverPackage.PackagePath, $usbDriverPackage.CertificatePath)
        }
        $fingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot -InputPaths (@(
            $sourceDirectory,
            (Join-Path $PSScriptRoot $definition.Script)
        ) + $extraInputs) -Properties @{
            cache_contract = "3"
            configuration = "Release"
            dotnet_sdk = $dotnetVersion
            shared = $sharedSampleFingerprint
        }
        $repositoryFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot -InputPaths (@(
            $sourceDirectory,
            (Join-Path $PSScriptRoot $definition.Script)
        ) + $extraInputs) -Properties @{
            cache_contract = "repository-capability-source-v2"
            configuration = "Release"
            shared = $repositorySharedSampleFingerprint
        }
        $cachePath = Join-Path $buildCacheRoot "capability-$($definition.Key).json"
        $localStatus = if (-not $definition.Enabled) {
            [pscustomobject]@{ IsCurrent = $false; Reason = "disabled"; Message = "当前环境未启用该能力域" }
        } elseif ($forceBuild) {
            [pscustomobject]@{ IsCurrent = $false; Reason = "full_build"; Message = "已选择全量重构" }
        } else {
            Get-EdrBuildCacheStatus -CachePath $cachePath -Fingerprint $fingerprint `
                -DirectoryPaths $packagePaths -CapabilityPackagePaths $packagePaths
        }
        $localIsCurrent = $localStatus.IsCurrent
        $repositoryFingerprintPath = Join-Path $repositoryCapabilityFingerprintRoot "$($definition.Key).json"
        $repositoryStatus = if (-not $definition.Enabled) {
            [pscustomobject]@{ IsCurrent = $false; Reason = "disabled"; Message = "当前环境未启用该能力域" }
        } elseif ($forceBuild) {
            [pscustomobject]@{ IsCurrent = $false; Reason = "full_build"; Message = "已选择全量重构" }
        } elseif ($localIsCurrent) {
            [pscustomobject]@{ IsCurrent = $false; Reason = "not_checked"; Message = "本地缓存已命中，未检查仓库指纹" }
        } else {
            Get-EdrRepositoryCapabilityFingerprintStatus -FingerprintPath $repositoryFingerprintPath `
                -SourceFingerprint $repositoryFingerprint -RepositoryRoot $repositoryRoot `
                -CapabilityPackagePaths $packagePaths
        }
        $repositoryIsCurrent = $repositoryStatus.IsCurrent
        $isCurrent = $localIsCurrent -or $repositoryIsCurrent
        if ($repositoryIsCurrent) {
            Set-EdrBuildCache -CachePath $cachePath -Fingerprint $fingerprint `
                -DirectoryPaths $packagePaths -Metadata @{
                    build_mode = $BuildMode
                    source = "repository_fingerprint"
                }
        }
        $definition | Add-Member -NotePropertyName Fingerprint -NotePropertyValue $fingerprint
        $definition | Add-Member -NotePropertyName CachePath -NotePropertyValue $cachePath
        $definition | Add-Member -NotePropertyName PackagePaths -NotePropertyValue $packagePaths
        $definition | Add-Member -NotePropertyName IsCurrent -NotePropertyValue $isCurrent
        $definition | Add-Member -NotePropertyName LocalCacheStatus -NotePropertyValue $localStatus
        $definition | Add-Member -NotePropertyName RepositoryCacheStatus -NotePropertyValue $repositoryStatus
        $definition | Add-Member -NotePropertyName CacheSource -NotePropertyValue $(if ($localIsCurrent) { "local" } elseif ($repositoryIsCurrent) { "repository" } else { "none" })
    }

    $buildQueue = @($capabilityBuilds | Where-Object { $_.Enabled -and -not $_.IsCurrent })
    if (-not $runnerIsCurrent -or $buildQueue.Count -gt 0) {
        Write-Host "[1/5] 统一还原 .NET 依赖…" -ForegroundColor Cyan
        & $dotnet.Source restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
    } else {
        Write-Host "[1/5] .NET 框架与能力源码指纹未变化，跳过还原。" -ForegroundColor DarkGray
    }

    if ($runnerIsCurrent) {
        Write-Host "[框架缓存命中] EdrTest Runner" -ForegroundColor DarkGray
    } else {
        Write-Host "[框架重建] EdrTest Runner" -ForegroundColor Cyan
        & $dotnet.Source build (Join-Path $repositoryRoot "src\EdrTest\EdrTest.csproj") `
            --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "EdrTest Runner 构建失败。" }
    }

    Write-Host "[2/5] 检查并构建能力包…" -ForegroundColor Cyan
    foreach ($definition in $capabilityBuilds) {
        if (-not $definition.Enabled) {
            if ($definition.Key -eq "driver") {
                Write-Warning "没有可用的仓库驱动包或 EWDK 后备包，已跳过驱动三项能力包。"
            } elseif ($definition.Key -eq "usb-device") {
                Write-Warning "没有可用的仓库 USB UDE 驱动包或 EWDK 后备包，已跳过 USB 挂载与卸载能力包。"
            }
            continue
        }
        if ($definition.IsCurrent) {
            $cacheSourceTitle = if ($definition.CacheSource -eq "repository") { "仓库指纹" } else { "本地缓存" }
            Write-Host "[能力缓存命中/$cacheSourceTitle] $($definition.Title)" -ForegroundColor DarkGray
            continue
        }

        Write-Host "[能力缓存未命中] $($definition.Title)：本地=$($definition.LocalCacheStatus.Message)；仓库=$($definition.RepositoryCacheStatus.Message)" -ForegroundColor DarkYellow
        Write-Host "[能力重建] $($definition.Title)" -ForegroundColor Cyan
        $scriptArguments = @(
            "-NoProfile",
            "-File", (Join-Path $PSScriptRoot $definition.Script),
            "-Configuration", "Release",
            "-SkipRestore"
        ) + @($definition.Arguments)
        & $pwsh.Source @scriptArguments
        if ($LASTEXITCODE -ne 0) {
            if ($definition.ContinueOnFailure) {
                Write-Warning "$($definition.Title)能力包构建失败，已跳过该类能力；平台其他能力继续启动。"
                if ($definition.Key -eq "driver") { Remove-DriverActivitySamplePackages }
                if ($definition.Key -eq "usb-device") { Remove-UsbDeviceActivitySamplePackages }
                continue
            }
            throw "$($definition.Title)能力样本构建失败。"
        }
        Set-EdrBuildCache -CachePath $definition.CachePath -Fingerprint $definition.Fingerprint `
            -DirectoryPaths $definition.PackagePaths -Metadata @{
                build_mode = $BuildMode
                source = $definition.Source
            }
    }
    if (-not $runnerIsCurrent -or $buildQueue.Count -gt 0) {
        # Controller 发布会再次生成其 ProjectReference 的 EdrTest 输出，因此必须在所有
        # 能力域完成后记录 Runner 快照，避免首次构建后的下一次启动误判为 Runner 损坏。
        Set-EdrBuildCache -CachePath $runnerCachePath -Fingerprint $runnerFingerprint `
            -DirectoryPaths @($runnerOutputDirectory) -Metadata @{ build_mode = $BuildMode }
    }
} else {
    Write-Host "[构建模式] 已显式跳过框架、能力包和前端构建。" -ForegroundColor DarkGray
}

if (-not (Test-Path $runnerDll)) { throw "找不到 Runner：$runnerDll。请移除 -SkipBuild 后重试。" }

$pnpmVersion = ((& $pnpm.Source --version) | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pnpmVersion)) { throw "无法读取 pnpm 版本。" }
$nodeVersion = ((& $node.Source --version) | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($nodeVersion)) { throw "无法读取 Node.js 版本。" }
$frontendDependencyCachePath = Join-Path $buildCacheRoot "frontend-dependencies.json"
$frontendDependencyFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot -InputPaths @(
    "web\package.json",
    "web\pnpm-lock.yaml",
    "web\pnpm-workspace.yaml",
    "web\patches"
) -Properties @{
    cache_contract = "2"
    node = $nodeVersion
    pnpm = $pnpmVersion
}
$frontendDependencyFiles = @(
    (Join-Path $webRoot "node_modules\.modules.yaml"),
    (Join-Path $webRoot "node_modules\playwright-core\package.json")
)
$frontendDependenciesCurrent = Test-EdrBuildCache `
    -CachePath $frontendDependencyCachePath -Fingerprint $frontendDependencyFingerprint `
    -RequiredFiles $frontendDependencyFiles
if (-not $frontendDependenciesCurrent) {
    Write-Host "[3/5] 安装前端依赖…" -ForegroundColor Cyan
    Push-Location $webRoot
    try {
        & $pnpm.Source install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "pnpm install 失败。" }
    } finally {
        Pop-Location
    }
    Set-EdrBuildCache -CachePath $frontendDependencyCachePath `
        -Fingerprint $frontendDependencyFingerprint -Metadata @{
            node = $nodeVersion
            pnpm = $pnpmVersion
        }
} else {
    Write-Host "[3/5] 前端依赖指纹未变化，跳过安装。" -ForegroundColor DarkGray
}

if (-not $SkipBuild) {
    $frontendDist = Join-Path $webRoot "dist"
    $frontendCachePath = Join-Path $buildCacheRoot "frontend.json"
    $frontendFingerprint = Get-EdrBuildFingerprint -RepositoryRoot $repositoryRoot -InputPaths @(
        "web\.openai",
        "web\app",
        "web\automation",
        "web\patches",
        "web\public",
        "web\worker",
        "web\eslint.config.mjs",
        "web\next.config.ts",
        "web\package.json",
        "web\pnpm-lock.yaml",
        "web\pnpm-workspace.yaml",
        "web\sites-vite-plugin.ts",
        "web\tsconfig.json",
        "web\vite.config.ts"
    ) -Properties @{
        api_url = "$apiUrl/api"
        cache_contract = "2"
        node = $nodeVersion
        pnpm = $pnpmVersion
    }
    $frontendIsCurrent = $BuildMode -ne "Full" -and (Test-EdrBuildCache `
        -CachePath $frontendCachePath -Fingerprint $frontendFingerprint `
        -DirectoryPaths @($frontendDist) `
        -RequiredFiles @((Join-Path $frontendDist "server\index.js")))
    if ($frontendIsCurrent) {
        Write-Host "[4/5] 前端源码指纹未变化，跳过构建。" -ForegroundColor DarkGray
    } else {
        Write-Host "[4/5] 构建前端控制面…" -ForegroundColor Cyan
        Push-Location $webRoot
        try {
            $env:VITE_EDR_API_URL = "$apiUrl/api"
            & $pnpm.Source run build
            if ($LASTEXITCODE -ne 0) { throw "pnpm build 失败。" }
        } finally {
            Pop-Location
        }
        Set-EdrBuildCache -CachePath $frontendCachePath -Fingerprint $frontendFingerprint `
            -DirectoryPaths @($frontendDist) -Metadata @{ build_mode = $BuildMode }
    }
} elseif (-not (Test-Path (Join-Path $webRoot "dist\server\index.js"))) {
    throw "找不到前端构建产物。请移除 -SkipBuild 后重试。"
}

$backendOut = Join-Path $logRoot "backend.out.log"
$backendErr = Join-Path $logRoot "backend.err.log"
$frontendOut = Join-Path $logRoot "frontend.out.log"
$frontendErr = Join-Path $logRoot "frontend.err.log"
$startedProcesses = @()

try {
    Write-Host "[5/5] 启动本地 API 与前端…" -ForegroundColor Cyan
    $backendArguments = @(
        "--roll-forward", "Major", $runnerDll, "serve",
        "--host", "127.0.0.1",
        "--port", $ApiPort,
        "--repo-root", $repositoryRoot,
        "--node-path", $node.Source,
        "--allowed-origin", $webUrl,
        "--allowed-origin", "http://localhost:$WebPort"
    )
    $backendCommandLine = Join-NativeArguments $backendArguments
    $backend = Start-Process -FilePath $dotnet.Source -ArgumentList $backendCommandLine -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden -RedirectStandardOutput $backendOut -RedirectStandardError $backendErr -PassThru
    $startedProcesses += $backend

    $frontendArguments = @(
        "-NoProfile", "-File", (Join-Path $PSScriptRoot "Run-WebControlPlane.ps1"),
        "-WebRoot", $webRoot, "-Port", $WebPort
    )
    $frontendCommandLine = Join-NativeArguments $frontendArguments
    $frontend = Start-Process -FilePath $pwsh.Source -ArgumentList $frontendCommandLine -WorkingDirectory $repositoryRoot -WindowStyle Hidden `
        -RedirectStandardOutput $frontendOut -RedirectStandardError $frontendErr -PassThru
    $startedProcesses += $frontend

    [ordered]@{
        schema_version = "1.0"
        repository_root = $repositoryRoot
        backend_pid = $backend.Id
        frontend_pid = $frontend.Id
        api_url = $apiUrl
        web_url = $webUrl
        started_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    } | ConvertTo-Json | Set-Content $statePath -Encoding utf8NoBOM

    $apiReady = $false
    $webReady = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if (-not $apiReady) {
            try {
                $health = Invoke-RestMethod "$apiUrl/api/health" -TimeoutSec 2
                $apiReady = $health.status -eq "ok"
            } catch { }
        }
        if (-not $webReady) {
            try {
                $response = Invoke-WebRequest $webUrl -UseBasicParsing -TimeoutSec 2
                $webReady = $response.StatusCode -eq 200
            } catch { }
        }
        if ($apiReady -and $webReady) { break }
        if ($backend.HasExited) { throw "本地 API 已异常退出，请查看 $backendErr。" }
        if ($frontend.HasExited) { throw "前端已异常退出，请查看 $frontendErr。" }
        Start-Sleep -Milliseconds 250
    }
    if (-not $apiReady) { throw "本地 API 在 30 秒内未通过健康检查，请查看 $backendErr。" }
    if (-not $webReady) { throw "前端在 30 秒内未就绪，请查看 $frontendErr。" }

    Write-Host "平台已启动：$webUrl" -ForegroundColor Green
    Write-Host "本地 API：$apiUrl"
    Write-Host "日志目录：$logRoot"
    if (-not $NoBrowser) { Start-Process $webUrl }
} catch {
    if (Test-Path $statePath) {
        & (Join-Path $PSScriptRoot "Stop-EdrTest.ps1")
    } else {
        foreach ($process in $startedProcesses) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
    }
    throw
}
