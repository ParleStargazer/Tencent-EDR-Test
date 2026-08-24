[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SamplesRoot,
    [switch]$SuppressPrivilegeWarning,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) { $SamplesRoot = Join-Path $repositoryRoot "samples" }
$SamplesRoot = [System.IO.Path]::GetFullPath($SamplesRoot)
$publishRoot = Join-Path $repositoryRoot "artifacts\wmi-activity-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

if (-not $SuppressPrivilegeWarning) {
    $isAdministrator = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdministrator) {
        Write-Warning "WMI permanent subscription 三项测试需要管理员权限；能力包仍会构建，但运行时会被 Runner 跳过。"
    }
}

if (-not $SkipRestore) {
    dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
}
dotnet publish (Join-Path $repositoryRoot "sample-src\WmiActivity\WmiActivity.Controller\WmiActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "WmiActivity Controller 发布失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\WmiActivity\WmiActivity.Behavior\WmiActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "WmiActivity Behavior 发布失败。" }

$packages = @(
    [pscustomobject]@{ Id = "win.wmi.filter"; Controller = "WmiEventFilter.Controller.exe"; Actor = "WmiEventFilter.Actor.exe" },
    [pscustomobject]@{ Id = "win.wmi.consumer"; Controller = "WmiEventConsumer.Controller.exe"; Actor = "WmiEventConsumer.Actor.exe" },
    [pscustomobject]@{ Id = "win.wmi.consumer_filter.bind"; Controller = "WmiEventConsumerToFilter.Controller.exe"; Actor = "WmiEventConsumerToFilter.Actor.exe" }
)

foreach ($package in $packages) {
    $destination = [System.IO.Path]::GetFullPath((Join-Path $SamplesRoot $package.Id))
    $relativeDestination = [System.IO.Path]::GetRelativePath($SamplesRoot, $destination)
    if ($relativeDestination.StartsWith("..", [System.StringComparison]::Ordinal) -or [System.IO.Path]::IsPathRooted($relativeDestination)) {
        throw "能力包目标越出 samples 根目录。"
    }
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null
    Copy-Item (Join-Path $controllerPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $behaviorPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $controllerPublish "WmiActivity.Controller.exe") (Join-Path $destination $package.Controller) -Force
    Copy-Item (Join-Path $behaviorPublish "WmiActivity.Behavior.exe") (Join-Path $destination $package.Actor) -Force
    $manifest = Get-Content (Join-Path $repositoryRoot "sample-src\WmiActivity\manifests\$($package.Id)\capability.json") -Raw | ConvertFrom-Json -Depth 30
    $manifest.controller | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $manifest.controller.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
    foreach ($participant in $manifest.participants) {
        $participant | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $participant.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
    }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $destination "capability.json") -Encoding utf8NoBOM
    Write-Host "[已生成] $($package.Id) -> $destination"
}
Write-Host "WMI Activity 三项能力包构建完成：$SamplesRoot"
