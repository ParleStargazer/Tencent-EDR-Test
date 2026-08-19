[CmdletBinding()]
param([ValidateSet("Debug", "Release")][string]$Configuration = "Release", [string]$OutputRoot)
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot "artifacts\group-policy-activity-e2e" }
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try { $administrator = [System.Security.Principal.WindowsPrincipal]::new($identity).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator) } finally { $identity.Dispose() }
if (-not $administrator) { throw "组策略端到端测试需要管理员 PowerShell；能力包构建与 Runner 跳过逻辑可在标准用户环境验证。" }
& (Join-Path $PSScriptRoot "Build-GroupPolicyActivitySamples.ps1") -Configuration $Configuration -SuppressPrivilegeWarning
if ($LASTEXITCODE -ne 0) { throw "组策略能力包构建失败。" }
$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path $runner)) { dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore }
& dotnet --roll-forward Major $runner run --runs-dir (Join-Path $OutputRoot "runs") --suite-id "group-policy-activity-e2e" --next-delay-seconds 0 `
    --manifest (Join-Path $repositoryRoot "samples\win.group_policy.modify\capability.json")
if ($LASTEXITCODE -ne 0) { throw "组策略 Runner 执行失败。" }
$local = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter local-run.json -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$run = Get-Content $local.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($run.run.status -ne "COMPLETED" -or $run.capabilities[0].status -ne "LOCAL_PASS") { throw "组策略本地绝对基准未通过：$($local.FullName)" }
Write-Host "[PASS] 组策略修改端到端测试通过：$($local.FullName)"
