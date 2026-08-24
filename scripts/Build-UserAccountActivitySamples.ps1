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
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) {
    $SamplesRoot = Join-Path $repositoryRoot "samples"
}
$SamplesRoot = [System.IO.Path]::GetFullPath($SamplesRoot)
$publishRoot = Join-Path $repositoryRoot "artifacts\user-account-activity-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

$isAdministrator = $false
try {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    $isAdministrator = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $identity.Dispose()
} catch {
    Write-Warning "无法确认当前 PowerShell 的管理员权限：$($_.Exception.Message)"
}
if (-not $isAdministrator -and -not $SuppressPrivilegeWarning) {
    Write-Warning "当前 PowerShell 未以管理员身份运行。建议使用管理员权限重新运行；能力包可以构建，但五项用户账号活动测试可能不可用并会被 Runner 跳过。"
}

if (-not $SkipRestore) {
    dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
}

dotnet publish (Join-Path $repositoryRoot "sample-src\UserAccountActivity\UserAccountActivity.Controller\UserAccountActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "UserAccountActivity Controller 发布失败。" }

dotnet publish (Join-Path $repositoryRoot "sample-src\UserAccountActivity\UserAccountActivity.Behavior\UserAccountActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "UserAccountActivity Behavior 发布失败。" }

$packages = @(
    @{ Id = "win.account.local.create"; Prefix = "AccountLocalCreate" },
    @{ Id = "win.account.local.modify"; Prefix = "AccountLocalModify" },
    @{ Id = "win.account.local.delete"; Prefix = "AccountLocalDelete" },
    @{ Id = "win.account.login"; Prefix = "AccountLogin" },
    @{ Id = "win.account.logoff"; Prefix = "AccountLogoff" }
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
    Copy-Item (Join-Path $controllerPublish "UserAccountActivity.Controller.exe") `
        (Join-Path $destination "$($package.Prefix).Controller.exe") -Force
    Copy-Item (Join-Path $behaviorPublish "UserAccountActivity.Behavior.exe") `
        (Join-Path $destination "$($package.Prefix).Actor.exe") -Force

    $manifestTemplate = Join-Path $repositoryRoot "sample-src\UserAccountActivity\manifests\$($package.Id)\capability.json"
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

Write-Host "User Account Activity 五个能力包构建完成：$SamplesRoot"
