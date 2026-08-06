[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SamplesRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) {
    $SamplesRoot = Join-Path $repositoryRoot "samples"
}
$SamplesRoot = [System.IO.Path]::GetFullPath($SamplesRoot)
$publishRoot = Join-Path $repositoryRoot "artifacts\process-activity-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }

dotnet publish (Join-Path $repositoryRoot "sample-src\ProcessActivity\ProcessActivity.Controller\ProcessActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "Controller 发布失败。" }

dotnet publish (Join-Path $repositoryRoot "sample-src\ProcessActivity\ProcessActivity.Behavior\ProcessActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "Behavior 发布失败。" }

$packages = @(
    @{ Id = "win.process.create"; Prefix = "ProcessCreate" },
    @{ Id = "win.process.terminate"; Prefix = "ProcessTerminate" },
    @{ Id = "win.process.access"; Prefix = "ProcessAccess" },
    @{ Id = "win.process.image_load"; Prefix = "ImageLoad" },
    @{ Id = "win.process.remote_thread"; Prefix = "RemoteThread" },
    @{ Id = "win.process.tampering"; Prefix = "ProcessTampering" }
)

foreach ($package in $packages) {
    $destination = Join-Path $SamplesRoot $package.Id
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null
    Copy-Item (Join-Path $controllerPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $behaviorPublish "*") $destination -Recurse -Force
    Copy-Item (Join-Path $controllerPublish "ProcessActivity.Controller.exe") `
        (Join-Path $destination "$($package.Prefix).Controller.exe") -Force
    Copy-Item (Join-Path $behaviorPublish "ProcessActivity.Behavior.exe") `
        (Join-Path $destination "$($package.Prefix).Actor.exe") -Force
    Copy-Item (Join-Path $behaviorPublish "ProcessActivity.Behavior.exe") `
        (Join-Path $destination "$($package.Prefix).Target.exe") -Force

    $manifestTemplate = Join-Path $repositoryRoot "sample-src\ProcessActivity\manifests\$($package.Id)\capability.json"
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

Write-Host "Process Activity 六个能力包构建完成：$SamplesRoot"
