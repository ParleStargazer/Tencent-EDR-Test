[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SamplesRoot,
    [switch]$SuppressPrivilegeWarning
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SamplesRoot)) { $SamplesRoot = Join-Path $repositoryRoot "samples" }
$SamplesRoot = [System.IO.Path]::GetFullPath($SamplesRoot)
$publishRoot = Join-Path $repositoryRoot "artifacts\group-policy-activity-publish"
$controllerPublish = Join-Path $publishRoot "controller"
$behaviorPublish = Join-Path $publishRoot "behavior"

if (-not $SuppressPrivilegeWarning) {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Write-Warning "组策略修改测试需要管理员权限；当前仍可构建能力包，但实际运行会被 Runner 安全跳过。"
        }
    } finally { $identity.Dispose() }
}

dotnet restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\GroupPolicyActivity\GroupPolicyActivity.Controller\GroupPolicyActivity.Controller.csproj") `
    --configuration $Configuration --no-restore --output $controllerPublish
if ($LASTEXITCODE -ne 0) { throw "GroupPolicyActivity Controller 发布失败。" }
dotnet publish (Join-Path $repositoryRoot "sample-src\GroupPolicyActivity\GroupPolicyActivity.Behavior\GroupPolicyActivity.Behavior.csproj") `
    --configuration $Configuration --no-restore --output $behaviorPublish
if ($LASTEXITCODE -ne 0) { throw "GroupPolicyActivity Behavior 发布失败。" }

$destination = [System.IO.Path]::GetFullPath((Join-Path $SamplesRoot "win.group_policy.modify"))
$relativeDestination = [System.IO.Path]::GetRelativePath($SamplesRoot, $destination)
if ($relativeDestination.StartsWith("..", [System.StringComparison]::Ordinal) -or [System.IO.Path]::IsPathRooted($relativeDestination)) { throw "能力包目标越出 samples 根目录。" }
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
[System.IO.Directory]::CreateDirectory($destination) | Out-Null
Copy-Item (Join-Path $controllerPublish "*") $destination -Recurse -Force
Copy-Item (Join-Path $behaviorPublish "*") $destination -Recurse -Force
Copy-Item (Join-Path $controllerPublish "GroupPolicyActivity.Controller.exe") (Join-Path $destination "GroupPolicyModify.Controller.exe") -Force
Copy-Item (Join-Path $behaviorPublish "GroupPolicyActivity.Behavior.exe") (Join-Path $destination "GroupPolicyModify.Actor.exe") -Force
$manifest = Get-Content (Join-Path $repositoryRoot "sample-src\GroupPolicyActivity\manifests\win.group_policy.modify\capability.json") -Raw | ConvertFrom-Json -Depth 30
$manifest.controller | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $manifest.controller.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
foreach ($participant in $manifest.participants) {
    $participant | Add-Member -NotePropertyName sha256 -NotePropertyValue ((Get-FileHash (Join-Path $destination $participant.executable) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
}
$manifest | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $destination "capability.json") -Encoding utf8NoBOM
Write-Host "[已生成] win.group_policy.modify -> $destination"
