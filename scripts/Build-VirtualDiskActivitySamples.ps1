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
$publishRoot = Join-Path $repositoryRoot "artifacts\virtual-disk-activity-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

if (-not $SuppressPrivilegeWarning) {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $administrator = [System.Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
            [System.Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $administrator) {
            Write-Warning "虚拟磁盘挂载需要管理员权限和显式允许 L2；能力包仍会构建，但实际测试会被 Runner 安全跳过。"
        }
    } finally { $identity.Dispose() }
}

if (-not $SkipRestore) {
    dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
}
dotnet publish (Join-Path $repositoryRoot "sample-src\VirtualDiskActivity\VirtualDiskActivity.Controller\VirtualDiskActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "VirtualDiskActivity Controller 发布失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\VirtualDiskActivity\VirtualDiskActivity.Behavior\VirtualDiskActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "VirtualDiskActivity Behavior 发布失败。" }

$destination = [System.IO.Path]::GetFullPath((Join-Path $SamplesRoot "win.device.virtual_disk.mount"))
$relativeDestination = [System.IO.Path]::GetRelativePath($SamplesRoot, $destination)
if ($relativeDestination.StartsWith("..", [System.StringComparison]::Ordinal) -or [System.IO.Path]::IsPathRooted($relativeDestination)) {
    throw "能力包目标越出 samples 根目录。"
}
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
[System.IO.Directory]::CreateDirectory($destination) | Out-Null
Copy-Item (Join-Path $controllerPublish "*") $destination -Recurse -Force
Copy-Item (Join-Path $behaviorPublish "*") $destination -Recurse -Force
Copy-Item (Join-Path $controllerPublish "VirtualDiskActivity.Controller.exe") (Join-Path $destination "VirtualDiskMount.Controller.exe") -Force
Copy-Item (Join-Path $behaviorPublish "VirtualDiskActivity.Behavior.exe") (Join-Path $destination "VirtualDiskMount.Actor.exe") -Force
$manifest = Get-Content (Join-Path $repositoryRoot "sample-src\VirtualDiskActivity\manifests\win.device.virtual_disk.mount\capability.json") -Raw | ConvertFrom-Json -Depth 30
$manifest.controller | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $manifest.controller.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
foreach ($participant in $manifest.participants) {
    $participant | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $participant.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
}
$manifest | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $destination "capability.json") -Encoding utf8NoBOM
Write-Host "[已生成] win.device.virtual_disk.mount -> $destination"
