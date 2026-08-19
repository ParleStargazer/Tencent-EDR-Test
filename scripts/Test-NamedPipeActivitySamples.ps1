[CmdletBinding()]
param([ValidateSet("Debug", "Release")][string]$Configuration = "Release", [string]$OutputRoot)
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot "artifacts\named-pipe-activity-e2e" }
& (Join-Path $PSScriptRoot "Build-NamedPipeActivitySamples.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "命名管道能力包构建失败。" }
$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path $runner)) { dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore }
& dotnet --roll-forward Major $runner run --runs-dir (Join-Path $OutputRoot "runs") --suite-id "named-pipe-activity-e2e" --next-delay-seconds 0 `
    --manifest (Join-Path $repositoryRoot "samples\win.named_pipe.create\capability.json") `
    --manifest (Join-Path $repositoryRoot "samples\win.named_pipe.connect\capability.json")
if ($LASTEXITCODE -ne 0) { throw "命名管道 Runner 执行失败。" }
$local = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter local-run.json -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$run = Get-Content $local.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
$failed = @($run.capabilities | Where-Object { $_.status -ne "LOCAL_PASS" })
if ($run.run.status -ne "COMPLETED" -or $run.capabilities.Count -ne 2 -or $failed.Count -gt 0) { throw "命名管道本地绝对基准未全部通过：$($local.FullName)" }
if (@($run.programs | Where-Object { $_.role -in @("actor", "helper") }).Count -ne 4) { throw "命名管道 Actor/Helper 程序记录数量异常。" }

$genericCloud = @()
$tencentCloud = @()
foreach ($event in $run.local_events) {
    $actor = $event.data.actor
    $operationName = if ($event.event_action -eq "create") { "创建管道" } else { "打开管道" }
    $genericCloud += [ordered]@{
        table = "NamedPipeActivity"; event_id = $event.local_event_id; host_id = $run.run.host.machine_id
        event_time = $event.occurred_at_utc; action = $event.event_action; actor_pid = $actor.pid
        actor_name = [System.IO.Path]::GetFileName($actor.executable); actor_executable = $actor.executable
        actor_command_line = $actor.command_line; pipe_name = $event.data.pipe_name; node_name = $event.data.pipe_name
        operation_name = $operationName; pipe_type = "管道"
    }
    $tencentCloud += [ordered]@{
        OS = "Windows"; '@table' = "FileEvents"; '@timestamp' = $event.observed_at_utc
        'Action.Type' = "File"; 'Action.Name' = "NamedPipe"; 'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id; 'Environment.HostName' = $run.run.host.hostname
        'Parent.ProcPid' = $actor.pid; 'Parent.FileName' = [System.IO.Path]::GetFileName($actor.executable)
        'Parent.FilePath' = $actor.executable; 'Parent.ProcCmdline' = $actor.command_line
        'Child.PipeName' = $event.data.pipe_name; 'Child.NodeName' = $event.data.pipe_name
        'Child.PipeOpName' = $operationName; 'Child.Type' = "管道"
    }
}
$genericCloudPath = Join-Path $OutputRoot "synthetic-cloud.named-pipe.json"
$tencentCloudPath = Join-Path $OutputRoot "synthetic-cloud.tencent-named-pipe.json"
$genericCloud | ConvertTo-Json -Depth 20 | Set-Content $genericCloudPath -Encoding utf8NoBOM
$tencentCloud | ConvertTo-Json -Depth 20 | Set-Content $tencentCloudPath -Encoding utf8NoBOM

function Invoke-NamedPipeComparison([string]$CloudPath, [string]$MappingName, [string]$OutputName) {
    $validationPath = Join-Path $OutputRoot $OutputName
    & dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $CloudPath `
        --mapping (Join-Path $repositoryRoot "mappings\$MappingName") `
        --baseline (Join-Path $repositoryRoot "baselines\windows\named_pipe_create.yaml") `
        --baseline (Join-Path $repositoryRoot "baselines\windows\named_pipe_connect.yaml") `
        --out $validationPath
    if ($LASTEXITCODE -ne 0) { throw "命名管道离线比较失败：$MappingName" }
    return Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
}
$genericValidation = Invoke-NamedPipeComparison $genericCloudPath "generic-named-pipe-activity-v1.yaml" "validation-result.synthetic.json"
$tencentValidation = Invoke-NamedPipeComparison $tencentCloudPath "tencent-edr-proc-events-v1.yaml" "validation-result.tencent-mapping.json"
if ($genericValidation.summary.pass -ne 2 -or $tencentValidation.summary.pass -ne 2) {
    throw "命名管道通用/腾讯映射未得到两个 PASS。"
}
Write-Host "[PASS] 命名管道创建、连接端到端测试通过：$($local.FullName)"
