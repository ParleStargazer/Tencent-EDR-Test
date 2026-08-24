[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SamplesRoot,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) {
    $SamplesRoot = Join-Path $repositoryRoot "samples"
}
$SamplesRoot = [System.IO.Path]::GetFullPath($SamplesRoot)
$publishRoot = Join-Path $repositoryRoot "artifacts\file-manipulation-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

if (-not $SkipRestore) {
    dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
}

dotnet publish (Join-Path $repositoryRoot "sample-src\FileManipulation\FileManipulation.Controller\FileManipulation.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "FileManipulation Controller 发布失败。" }

dotnet publish (Join-Path $repositoryRoot "sample-src\FileManipulation\FileManipulation.Behavior\FileManipulation.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "FileManipulation Behavior 发布失败。" }

$packages = @(
    @{ Id = "win.file.create"; Prefix = "FileCreate" },
    @{ Id = "win.file.open"; Prefix = "FileOpen" },
    @{ Id = "win.file.delete"; Prefix = "FileDelete" },
    @{ Id = "win.file.modify"; Prefix = "FileModify" },
    @{ Id = "win.file.rename"; Prefix = "FileRename" }
)

foreach ($package in $packages) {
    $destination = [System.IO.Path]::GetFullPath((Join-Path $SamplesRoot $package.Id))
    $relativeDestination = [System.IO.Path]::GetRelativePath($SamplesRoot, $destination)
    if ($relativeDestination.StartsWith("..", [System.StringComparison]::Ordinal) -or [System.IO.Path]::IsPathRooted($relativeDestination)) {
        throw "能力包目标越出 samples 根目录：$destination"
    }
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null
    Copy-Item (Join-Path $controllerPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $behaviorPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $controllerPublish "FileManipulation.Controller.exe") `
        (Join-Path $destination "$($package.Prefix).Controller.exe") -Force
    Copy-Item (Join-Path $behaviorPublish "FileManipulation.Behavior.exe") `
        (Join-Path $destination "$($package.Prefix).Actor.exe") -Force

    $manifestTemplate = Join-Path $repositoryRoot "sample-src\FileManipulation\manifests\$($package.Id)\capability.json"
    $manifest = Get-Content $manifestTemplate -Raw | ConvertFrom-Json -Depth 30
    $controllerHash = (Get-FileHash (Join-Path $destination $manifest.controller.executable) -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest.controller | Add-Member -NotePropertyName sha256 -NotePropertyValue $controllerHash -Force
    foreach ($participant in $manifest.participants) {
        $participantHash = (Get-FileHash (Join-Path $destination $participant.executable) -Algorithm SHA256).Hash.ToLowerInvariant()
        $participant | Add-Member -NotePropertyName sha256 -NotePropertyValue $participantHash -Force
    }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $destination "capability.json") -Encoding utf8NoBOM
    Write-Host "[已生成] $($package.Id) -> $destination"
}

Write-Host "File Manipulation 五个能力包构建完成：$SamplesRoot"
