[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot "artifacts\powershell-activity-e2e" }
& (Join-Path $PSScriptRoot "Build-PowerShellActivitySamples.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "PowerShell 能力包构建失败。" }
$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path $runner)) { dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore }
& dotnet --roll-forward Major $runner run --runs-dir (Join-Path $OutputRoot "runs") --suite-id "powershell-activity-e2e" --next-delay-seconds 0 `
    --manifest (Join-Path $repositoryRoot "samples\win.powershell.script_block\capability.json")
if ($LASTEXITCODE -ne 0) { throw "PowerShell Runner 执行失败。" }

$local = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter local-run.json -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$run = Get-Content $local.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($run.run.status -ne "COMPLETED" -or $run.capabilities.Count -ne 1 -or $run.capabilities[0].status -ne "LOCAL_PASS") {
    throw "PowerShell 本地绝对基准未通过：$($local.FullName)"
}
$actors = @($run.programs | Where-Object role -eq "actor")
$targets = @($run.programs | Where-Object role -eq "target")
if ($actors.Count -ne 2 -or $targets.Count -ne 2) { throw "PowerShell 双子测试的 Actor/Target 程序数量异常。" }

$cloud = @()
foreach ($event in $run.local_events) {
    $target = $run.programs | Where-Object program_instance_id -eq $event.target_program_id | Select-Object -First 1
    if ($null -eq $target) { throw "本地 PowerShell 事件缺少 Target 程序。" }
    $cloud += [ordered]@{
        OS = "Windows"
        '@table' = "ScriptEvents"
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = "Script"
        'Action.Name' = "ScriptScan"
        'Common.MonitorName' = "AmsiHook"
        'Common.Source' = "InjectDll"
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id
        'Common.Guid' = "synthetic-agent"
        'Common.ClientVer' = "synthetic-1"
        'Environment.HostName' = $run.run.host.hostname
        'Environment.OsVersion' = $run.run.host.os_version
        'Parent.ProcPid' = $target.pid
        'Parent.ProcGuid' = $target.program_instance_id
        'Parent.FileName' = $target.file_name
        'Parent.FilePath' = $target.executable
        'Parent.ProcCmdline' = $target.command_line
        'Parent.ProcCreateTime' = [DateTimeOffset]::Parse($target.started_at_utc).ToUnixTimeMilliseconds()
        'Parent.FileMd5' = $target.md5
        'Child.ContentData' = $event.data.script_block.script_text
        'Child.ContentName' = ""
        'Child.Type' = "脚本"
        'Child.HookModule' = "edrinjclr.AmsiUtils::ScanContent"
        'Child.AppVersion' = 3
    }
}
$cloudPath = Join-Path $OutputRoot "synthetic-cloud.tencent-powershell.json"
$cloud | ConvertTo-Json -Depth 20 | Set-Content $cloudPath -Encoding utf8NoBOM
$validationPath = Join-Path $OutputRoot "validation-result.tencent-mapping.json"
& dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $cloudPath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\powershell_script_block.yaml") `
    --out $validationPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
if ($LASTEXITCODE -ne 0) { throw "PowerShell 腾讯映射离线比较失败。" }
$validation = Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($validation.summary.pass -ne 1) { throw "PowerShell 双方法腾讯映射没有得到能力 PASS。" }
$methodResults = @($validation.capabilities[0].method_results)
if ($methodResults.Count -ne 2 -or @($methodResults | Where-Object status -ne "PASS").Count -gt 0) {
    throw "PowerShell 一般命令与显式脚本块方法没有分别通过。"
}
Write-Host "[PASS] PowerShell 一般命令、显式脚本块与腾讯 ScriptScan 映射端到端测试通过：$($local.FullName)"
