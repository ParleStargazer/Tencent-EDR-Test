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

function Test-EdrBuildCache {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CachePath,
        [Parameter(Mandatory)][string]$Fingerprint,
        [string[]]$DirectoryPaths = @(),
        [string[]]$CapabilityPackagePaths = @(),
        [string[]]$RequiredFiles = @()
    )

    if (-not (Test-Path -LiteralPath $CachePath -PathType Leaf)) { return $false }
    try {
        $cache = Get-Content -LiteralPath $CachePath -Raw | ConvertFrom-Json -Depth 30
        if ($cache.schema_version -ne "1.0" -or $cache.fingerprint -ne $Fingerprint) { return $false }

        foreach ($requiredFile in $RequiredFiles) {
            if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { return $false }
        }
        foreach ($packagePath in $CapabilityPackagePaths) {
            if (-not (Test-EdrCapabilityPackage -PackagePath $packagePath)) { return $false }
        }

        $expectedSnapshots = @($cache.directory_snapshots)
        if ($expectedSnapshots.Count -ne $DirectoryPaths.Count) { return $false }
        foreach ($directoryPath in $DirectoryPaths) {
            $actual = Get-EdrDirectorySnapshot -Path $directoryPath
            if ($null -eq $actual) { return $false }
            $expected = @($expectedSnapshots | Where-Object {
                [string]::Equals($_.path, $actual.path, [System.StringComparison]::OrdinalIgnoreCase)
            }) | Select-Object -First 1
            if ($null -eq $expected `
                -or [int]$expected.file_count -ne $actual.file_count `
                -or [long]$expected.total_bytes -ne $actual.total_bytes) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    }
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
