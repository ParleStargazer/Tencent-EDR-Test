function Add-EdrFingerprintText {
    param(
        [System.Security.Cryptography.IncrementalHash]$Hasher,
        [AllowEmptyString()][string]$Text
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $Hasher.AppendData($bytes)
}

function Get-EdrBuildFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string[]]$InputPaths,
        [hashtable]$Properties = @{}
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $excludedDirectoryNames = @(".git", ".edr-test", "artifacts", "bin", "dist", "node_modules", "obj", "runs", "samples")
    $filesByPath = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $missingPaths = [System.Collections.Generic.List[string]]::new()

    foreach ($inputPath in $InputPaths) {
        $fullPath = if ([System.IO.Path]::IsPathRooted($inputPath)) {
            [System.IO.Path]::GetFullPath($inputPath)
        } else {
            [System.IO.Path]::GetFullPath((Join-Path $root $inputPath))
        }

        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $filesByPath[$fullPath] = Get-Item -LiteralPath $fullPath
            continue
        }
        if (Test-Path -LiteralPath $fullPath -PathType Container) {
            foreach ($file in Get-ChildItem -LiteralPath $fullPath -File -Force -Recurse) {
                $relativeSegments = [System.IO.Path]::GetRelativePath($fullPath, $file.FullName) -split '[\\/]'
                if (@($relativeSegments | Where-Object { $excludedDirectoryNames -contains $_ }).Count -gt 0) {
                    continue
                }
                $filesByPath[$file.FullName] = $file
            }
            continue
        }

        $missingPaths.Add([System.IO.Path]::GetRelativePath($root, $fullPath).Replace('\', '/'))
    }

    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($key in @($Properties.Keys | Sort-Object)) {
            Add-EdrFingerprintText -Hasher $hasher -Text "property:$key=$($Properties[$key])`n"
        }
        foreach ($missingPath in @($missingPaths | Sort-Object -Unique)) {
            Add-EdrFingerprintText -Hasher $hasher -Text "missing:$missingPath`n"
        }
        foreach ($file in @($filesByPath.Values | Sort-Object FullName)) {
            $relativePath = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            Add-EdrFingerprintText -Hasher $hasher -Text "file:$relativePath`0$($file.Length)`0"
            $stream = [System.IO.File]::OpenRead($file.FullName)
            try {
                $buffer = [byte[]]::new(1024 * 128)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hasher.AppendData($buffer, 0, $read)
                }
            } finally {
                $stream.Dispose()
            }
            Add-EdrFingerprintText -Hasher $hasher -Text "`0"
        }
        return [System.Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
    } finally {
        $hasher.Dispose()
    }
}

function Get-EdrCapabilitySharedSourceInputs {
    <#
      能力 Controller 只依赖 EdrTest 中的清单、数据库、通用值与子测试计时协议。
      Runner、比较器、本地 API 和云端导出服务属于平台控制面，不应使所有能力包失效。
    #>
    return [string[]]@(
        "Directory.Build.props",
        "sample-src\Common",
        "src\EdrTest\EdrTest.csproj",
        "src\EdrTest\packages.lock.json",
        "src\EdrTest\Common.cs",
        "src\EdrTest\CapabilityModels.cs",
        "src\EdrTest\RunDatabase.cs",
        "src\EdrTest\SubtestTiming.cs",
        "schemas\run-db.sql"
    )
}

function Get-EdrDirectorySnapshot {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) { return $null }
    $files = @(Get-ChildItem -LiteralPath $fullPath -File -Force -Recurse)
    $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $totalBytes) { $totalBytes = 0 }
    return [pscustomobject]@{
        path = $fullPath
        file_count = $files.Count
        total_bytes = [long]$totalBytes
    }
}

function Test-EdrCapabilityPackage {
    param([Parameter(Mandatory)][string]$PackagePath)

    $manifestPath = Join-Path $PackagePath "capability.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $false }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 50
        $programs = @($manifest.controller) + @($manifest.participants)
        if ($programs.Count -eq 0) { return $false }
        foreach ($program in $programs) {
            $executable = [string]$program.executable
            if ([string]::IsNullOrWhiteSpace($executable)) { return $false }
            $executablePath = [System.IO.Path]::GetFullPath((Join-Path $PackagePath $executable))
            $relative = [System.IO.Path]::GetRelativePath($PackagePath, $executablePath)
            if ($relative.StartsWith("..", [System.StringComparison]::Ordinal) `
                -or [System.IO.Path]::IsPathRooted($relative) `
                -or -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
                return $false
            }
            $expectedHash = [string]$program.sha256
            if (-not [string]::IsNullOrWhiteSpace($expectedHash)) {
                $actualHash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash
                if (-not [string]::Equals($actualHash, $expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }
        }
        return $true
    } catch {
        return $false
    }
}

function Get-EdrRepositoryRelativePath {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$Path
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $relativePath = [System.IO.Path]::GetRelativePath($root, $fullPath).Replace('\', '/')
    if ($relativePath.StartsWith("..", [System.StringComparison]::Ordinal) `
        -or [System.IO.Path]::IsPathRooted($relativePath)) {
        throw "构建指纹路径必须位于仓库内：$fullPath"
    }
    return $relativePath
}

function Get-EdrCapabilityPackageContentFingerprint {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$PackagePath
    )

    return Get-EdrBuildFingerprint -RepositoryRoot $RepositoryRoot -InputPaths @($PackagePath) -Properties @{
        cache_contract = "capability-package-content-v1"
    }
}

function Get-EdrRepositoryCapabilityFingerprintStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FingerprintPath,
        [Parameter(Mandatory)][string]$SourceFingerprint,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$CapabilityPackagePaths
    )

    if (-not (Test-Path -LiteralPath $FingerprintPath -PathType Leaf)) {
        return [pscustomobject]@{ IsCurrent = $false; Reason = "fingerprint_missing"; Message = "仓库指纹文件不存在" }
    }
    try {
        $record = Get-Content -LiteralPath $FingerprintPath -Raw | ConvertFrom-Json -Depth 30
        if ($record.schema_version -ne "1.0" -or $record.cache_contract -ne "repository-capability-v2" `
            -or $record.source_fingerprint -ne $SourceFingerprint) {
            $contractMismatch = $record.schema_version -ne "1.0" -or $record.cache_contract -ne "repository-capability-v2"
            $message = if ($contractMismatch) { "仓库指纹格式版本不受支持" } else { "能力源码指纹已变化" }
            $reason = if ($contractMismatch) { "schema_mismatch" } else { "source_fingerprint_changed" }
            return [pscustomobject]@{ IsCurrent = $false; Reason = $reason; Message = $message }
        }
        $expectedPackages = @($record.packages)
        if ($expectedPackages.Count -ne $CapabilityPackagePaths.Count) {
            return [pscustomobject]@{ IsCurrent = $false; Reason = "package_count_changed"; Message = "能力包数量已变化" }
        }

        foreach ($packagePath in $CapabilityPackagePaths) {
            if (-not (Test-EdrCapabilityPackage -PackagePath $packagePath)) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "package_invalid"; Message = "能力包缺失或清单校验失败：$packagePath" }
            }
            $relativePath = Get-EdrRepositoryRelativePath -RepositoryRoot $RepositoryRoot -Path $packagePath
            $expected = @($expectedPackages | Where-Object {
                [string]::Equals($_.path, $relativePath, [System.StringComparison]::OrdinalIgnoreCase)
            }) | Select-Object -First 1
            if ($null -eq $expected) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "package_not_recorded"; Message = "仓库指纹未记录能力包：$relativePath" }
            }
            $snapshot = Get-EdrDirectorySnapshot -Path $packagePath
            if ($null -eq $snapshot `
                -or [int]$expected.file_count -ne $snapshot.file_count `
                -or [long]$expected.total_bytes -ne $snapshot.total_bytes) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "package_snapshot_changed"; Message = "能力包文件数或总大小已变化：$relativePath" }
            }
            $contentFingerprint = Get-EdrCapabilityPackageContentFingerprint `
                -RepositoryRoot $RepositoryRoot -PackagePath $packagePath
            if (-not [string]::Equals($expected.content_fingerprint, $contentFingerprint,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "package_content_changed"; Message = "能力包内容指纹已变化：$relativePath" }
            }
        }
        return [pscustomobject]@{ IsCurrent = $true; Reason = "current"; Message = "仓库能力包指纹匹配" }
    } catch {
        return [pscustomobject]@{ IsCurrent = $false; Reason = "fingerprint_invalid"; Message = "仓库指纹读取失败：$($_.Exception.Message)" }
    }
}

function Test-EdrRepositoryCapabilityFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FingerprintPath,
        [Parameter(Mandatory)][string]$SourceFingerprint,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$CapabilityPackagePaths
    )

    return (Get-EdrRepositoryCapabilityFingerprintStatus -FingerprintPath $FingerprintPath `
        -SourceFingerprint $SourceFingerprint -RepositoryRoot $RepositoryRoot `
        -CapabilityPackagePaths $CapabilityPackagePaths).IsCurrent
}

function Set-EdrRepositoryCapabilityFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FingerprintPath,
        [Parameter(Mandatory)][string]$CapabilityKey,
        [Parameter(Mandatory)][string]$SourceFingerprint,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$CapabilityPackagePaths
    )

    $packages = @($CapabilityPackagePaths | Sort-Object | ForEach-Object {
        if (-not (Test-EdrCapabilityPackage -PackagePath $_)) {
            throw "无法记录仓库能力包指纹，能力包不完整：$_"
        }
        $snapshot = Get-EdrDirectorySnapshot -Path $_
        [ordered]@{
            path = Get-EdrRepositoryRelativePath -RepositoryRoot $RepositoryRoot -Path $_
            content_fingerprint = Get-EdrCapabilityPackageContentFingerprint `
                -RepositoryRoot $RepositoryRoot -PackagePath $_
            file_count = $snapshot.file_count
            total_bytes = $snapshot.total_bytes
        }
    })
    $record = [ordered]@{
        schema_version = "1.0"
        cache_contract = "repository-capability-v2"
        capability_key = $CapabilityKey
        source_fingerprint = $SourceFingerprint
        packages = $packages
    }
    $serialized = $record | ConvertTo-Json -Depth 20
    if (Test-Path -LiteralPath $FingerprintPath -PathType Leaf) {
        $current = Get-Content -LiteralPath $FingerprintPath -Raw
        if ([string]::Equals($current.Trim(), $serialized.Trim(), [System.StringComparison]::Ordinal)) {
            return
        }
    }

    $parent = Split-Path -Parent $FingerprintPath
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    $temporaryPath = "$FingerprintPath.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        Set-Content -LiteralPath $temporaryPath -Value $serialized -Encoding utf8NoBOM
        Move-Item -LiteralPath $temporaryPath -Destination $FingerprintPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Get-EdrBuildCacheStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CachePath,
        [Parameter(Mandatory)][string]$Fingerprint,
        [string[]]$DirectoryPaths = @(),
        [string[]]$CapabilityPackagePaths = @(),
        [string[]]$RequiredFiles = @()
    )

    if (-not (Test-Path -LiteralPath $CachePath -PathType Leaf)) {
        return [pscustomobject]@{ IsCurrent = $false; Reason = "cache_missing"; Message = "本地构建缓存不存在" }
    }
    try {
        $cache = Get-Content -LiteralPath $CachePath -Raw | ConvertFrom-Json -Depth 30
        if ($cache.schema_version -ne "1.0") {
            return [pscustomobject]@{ IsCurrent = $false; Reason = "schema_mismatch"; Message = "本地构建缓存格式版本不受支持" }
        }
        if ($cache.fingerprint -ne $Fingerprint) {
            return [pscustomobject]@{ IsCurrent = $false; Reason = "source_fingerprint_changed"; Message = "本地源码或构建环境指纹已变化" }
        }

        foreach ($requiredFile in $RequiredFiles) {
            if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "required_file_missing"; Message = "构建输出文件不存在：$requiredFile" }
            }
        }
        foreach ($packagePath in $CapabilityPackagePaths) {
            if (-not (Test-EdrCapabilityPackage -PackagePath $packagePath)) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "package_invalid"; Message = "能力包缺失或清单校验失败：$packagePath" }
            }
        }

        $expectedSnapshots = @($cache.directory_snapshots)
        if ($expectedSnapshots.Count -ne $DirectoryPaths.Count) {
            return [pscustomobject]@{ IsCurrent = $false; Reason = "snapshot_count_changed"; Message = "构建输出目录数量已变化" }
        }
        foreach ($directoryPath in $DirectoryPaths) {
            $actual = Get-EdrDirectorySnapshot -Path $directoryPath
            if ($null -eq $actual) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "output_directory_missing"; Message = "构建输出目录不存在：$directoryPath" }
            }
            $expected = @($expectedSnapshots | Where-Object {
                [string]::Equals($_.path, $actual.path, [System.StringComparison]::OrdinalIgnoreCase)
            }) | Select-Object -First 1
            if ($null -eq $expected `
                -or [int]$expected.file_count -ne $actual.file_count `
                -or [long]$expected.total_bytes -ne $actual.total_bytes) {
                return [pscustomobject]@{ IsCurrent = $false; Reason = "output_snapshot_changed"; Message = "构建输出文件数或总大小已变化：$directoryPath" }
            }
        }
        return [pscustomobject]@{ IsCurrent = $true; Reason = "current"; Message = "本地构建缓存匹配" }
    } catch {
        return [pscustomobject]@{ IsCurrent = $false; Reason = "cache_invalid"; Message = "本地构建缓存读取失败：$($_.Exception.Message)" }
    }
}

function Test-EdrBuildCache {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CachePath,
        [Parameter(Mandatory)][string]$Fingerprint,
        [string[]]$DirectoryPaths = @(),
        [string[]]$CapabilityPackagePaths = @(),
        [string[]]$RequiredFiles = @()
    )

    return (Get-EdrBuildCacheStatus -CachePath $CachePath -Fingerprint $Fingerprint `
        -DirectoryPaths $DirectoryPaths -CapabilityPackagePaths $CapabilityPackagePaths `
        -RequiredFiles $RequiredFiles).IsCurrent
}

function Set-EdrBuildCache {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CachePath,
        [Parameter(Mandatory)][string]$Fingerprint,
        [string[]]$DirectoryPaths = @(),
        [hashtable]$Metadata = @{}
    )

    $parent = Split-Path -Parent $CachePath
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    $snapshots = @($DirectoryPaths | ForEach-Object { Get-EdrDirectorySnapshot -Path $_ })
    if (@($snapshots | Where-Object { $null -eq $_ }).Count -gt 0) {
        throw "无法记录构建缓存：一个或多个输出目录不存在。"
    }
    $cache = [ordered]@{
        schema_version = "1.0"
        fingerprint = $Fingerprint
        built_at = [DateTimeOffset]::Now.ToString("O")
        directory_snapshots = $snapshots
        metadata = $Metadata
    }
    $temporaryPath = "$CachePath.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        $cache | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
        Move-Item -LiteralPath $temporaryPath -Destination $CachePath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}
