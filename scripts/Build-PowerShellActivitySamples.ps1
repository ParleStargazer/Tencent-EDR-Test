[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SamplesRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) { $SamplesRoot = Join-Path $repositoryRoot "samples" }
$SamplesRoot = [System.IO.Path]::GetFullPath($SamplesRoot)
$publishRoot = Join-Path $repositoryRoot "artifacts\powershell-activity-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\PowerShellActivity\PowerShellActivity.Controller\PowerShellActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "PowerShellActivity Controller 发布失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\PowerShellActivity\PowerShellActivity.Behavior\PowerShellActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "PowerShellActivity Behavior 发布失败。" }

$capabilityId = "win.powershell.script_block"
$destination = [System.IO.Path]::GetFullPath((Join-Path $SamplesRoot $capabilityId))
$relativeDestination = [System.IO.Path]::GetRelativePath($SamplesRoot, $destination)
if ($relativeDestination.StartsWith("..", [System.StringComparison]::Ordinal) -or [System.IO.Path]::IsPathRooted($relativeDestination)) {
    throw "能力包目标越出 samples 根目录。"
}
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
[System.IO.Directory]::CreateDirectory($destination) | Out-Null
Copy-Item (Join-Path $controllerPublish "*") $destination -Recurse -Force
Copy-Item (Join-Path $behaviorPublish "*") $destination -Recurse -Force
Copy-Item (Join-Path $controllerPublish "PowerShellActivity.Controller.exe") (Join-Path $destination "PowerShellScriptBlock.Controller.exe") -Force
Copy-Item (Join-Path $behaviorPublish "PowerShellActivity.Behavior.exe") (Join-Path $destination "PowerShellScriptBlock.Actor.exe") -Force
$manifest = Get-Content (Join-Path $repositoryRoot "sample-src\PowerShellActivity\manifests\$capabilityId\capability.json") -Raw | ConvertFrom-Json -Depth 30
$manifest.controller | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $manifest.controller.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
foreach ($participant in $manifest.participants) {
    $participant | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $participant.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
}
$manifest | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $destination "capability.json") -Encoding utf8NoBOM
Write-Host "[已生成] $capabilityId -> $destination"
Write-Host "PowerShell Activity 能力包构建完成：$SamplesRoot"
