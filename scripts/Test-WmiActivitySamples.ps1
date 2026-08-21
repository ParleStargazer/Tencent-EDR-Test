[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot "artifacts\wmi-activity-e2e" }
& (Join-Path $PSScriptRoot "Build-WmiActivitySamples.ps1") -Configuration $Configuration -SuppressPrivilegeWarning
if ($LASTEXITCODE -ne 0) { throw "WMI 能力包构建失败。" }

$isAdministrator = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    Write-Warning "[SKIP] 当前不是管理员：已验证 WMI 三项能力包可构建；ROOT\subscription 实际创建、规划映射与未匹配结论请在管理员 PowerShell 中运行本脚本。"
    return
}

$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path $runner)) { dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore }
& dotnet --roll-forward Major $runner run --runs-dir (Join-Path $OutputRoot "runs") --suite-id "wmi-activity-e2e" --next-delay-seconds 0 `
    --manifest (Join-Path $repositoryRoot "samples\win.wmi.filter\capability.json") `
    --manifest (Join-Path $repositoryRoot "samples\win.wmi.consumer\capability.json") `
    --manifest (Join-Path $repositoryRoot "samples\win.wmi.consumer_filter.bind\capability.json")
if ($LASTEXITCODE -ne 0) { throw "WMI Runner 执行失败。" }

$local = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter local-run.json -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$run = Get-Content $local.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
$capabilities = @($run.capabilities | Where-Object capability_id -like "win.wmi.*")
$events = @($run.local_events | Where-Object event_type -eq "wmi")
if ($run.run.status -ne "COMPLETED" -or $capabilities.Count -ne 3 -or @($capabilities | Where-Object status -ne "LOCAL_PASS").Count -gt 0) {
    throw "WMI 三项本地绝对基准未全部通过：$($local.FullName)"
}
if ($events.Count -ne 3 -or @($events | Where-Object { -not $_.data.result.succeeded -or $_.data.before.exists -or -not $_.data.after.exists }).Count -gt 0) {
    throw "WMI 三项本地事件缺少创建前后或成功证据。"
}
$cleanup = @($run.cleanup_results | Where-Object action -eq "delete_wmi_binding_consumer_filter_and_stop_actor")
if ($cleanup.Count -ne 3 -or @($cleanup | Where-Object status -ne "succeeded").Count -gt 0) {
    throw "WMI 三项清理结果异常。"
}

$cloud = @()
foreach ($event in $events) {
    $actor = $run.programs | Where-Object program_instance_id -eq $event.actor_program_id | Select-Object -First 1
    if ($null -eq $actor) { throw "WMI 本地事件缺少 Actor。" }
    $data = $event.data
    $actionName = switch ($event.event_action) {
        "filter" { "WmiEventFilter" }
        "consumer" { "WmiEventConsumer" }
        "consumer_filter_bind" { "WmiEventConsumerToFilter" }
        default { throw "未知 WMI 动作：$($event.event_action)" }
    }
    $cloud += [ordered]@{
        OS = "Windows"
        '@table' = "WMIEvents"
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = "WMI"
        'Action.Name' = $actionName
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id
        'Common.Guid' = "synthetic-agent"
        'Common.ClientVer' = "synthetic-1"
        'Environment.HostName' = $run.run.host.hostname
        'Environment.OsVersion' = $run.run.host.os_version
        'Parent.ProcPid' = $actor.pid
        'Parent.ProcGuid' = $actor.program_instance_id
        'Parent.FileName' = $actor.file_name
        'Parent.FilePath' = $actor.executable
        'Parent.ProcCmdline' = $actor.command_line
        'Child.WmiNamespace' = $data.namespace
        'Child.WmiObjectClass' = $data.object_class
        'Child.WmiObjectName' = $data.object_name
        'Child.WmiObjectPath' = $data.object_path
        'Child.WmiFilterName' = if ($null -eq $data.filter_path) { "" } else { ($run.local_facts | Where-Object key -eq "wmi.filter_name" | Where-Object case_run_id -eq $event.case_run_id | Select-Object -First 1).value }
        'Child.WmiFilterPath' = $data.filter_path
        'Child.WmiQuery' = $data.query
        'Child.WmiQueryLanguage' = $data.query_language
        'Child.WmiEventNamespace' = $data.event_namespace
        'Child.WmiConsumerName' = if ($null -eq $data.consumer_path) { "" } else { ($run.local_facts | Where-Object key -eq "wmi.consumer_name" | Where-Object case_run_id -eq $event.case_run_id | Select-Object -First 1).value }
        'Child.WmiConsumerPath' = $data.consumer_path
        'Child.WmiConsumerClass' = $data.consumer_class
        'Child.WmiLogFilePath' = $data.log_file_path
        'Child.WmiTextTemplate' = $data.text_template
        'Child.WmiBindingPath' = $data.binding_path
        'Child.WmiFilterReference' = $data.after.filter_reference
        'Child.WmiConsumerReference' = $data.after.consumer_reference
    }
}
$cloudPath = Join-Path $OutputRoot "synthetic-cloud.tencent-wmi.json"
$cloud | ConvertTo-Json -Depth 20 | Set-Content $cloudPath -Encoding utf8NoBOM
$baselines = @("wmi_filter.yaml", "wmi_consumer.yaml", "wmi_consumer_filter_bind.yaml")
foreach ($baselineName in $baselines) {
    $validationPath = Join-Path $OutputRoot ("validation-result.tencent-" + [IO.Path]::GetFileNameWithoutExtension($baselineName) + ".json")
    & dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $cloudPath `
        --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
        --baseline (Join-Path $repositoryRoot "baselines\windows\$baselineName") `
        --out $validationPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
    if ($LASTEXITCODE -ne 0) { throw "WMI 腾讯规划映射离线比较失败：$baselineName" }
    $validation = Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
    if ($validation.summary.pass -ne 1) { throw "WMI 规划映射没有得到 PASS：$baselineName" }
}

$unrelatedCloudPath = Join-Path $OutputRoot "current-product-wmi-operation-only.json"
@(
    [ordered]@{
        OS = "Windows"; '@table' = "WMIEvents"; '@timestamp' = $events[0].observed_at_utc
        'Action.Type' = "WinEventLog"; 'Action.Name' = "WmiOperation"
        'Common.EventUUId' = "wmi-execmethod-only"; 'Common.EventTime' = [DateTimeOffset]::Parse($events[0].occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id; 'Environment.HostName' = $run.run.host.hostname
        'Child.Operation' = "Start IWbemServices::ExecMethod Win32_Process::Create"
    },
    [ordered]@{
        OS = "Windows"; '@table' = "ProcEvents"; '@timestamp' = $events[0].observed_at_utc
        'Action.Type' = "Proc"; 'Action.Name' = "ProcessCreate"
        'Common.EventUUId' = "wmi-unrelated-process"; 'Common.EventTime' = [DateTimeOffset]::Parse($events[0].occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id; 'Environment.HostName' = $run.run.host.hostname
        'Parent.ProcPid' = 4; 'Parent.FilePath' = "C:\Windows\System32\System"
        'Child.ProcPid' = 5; 'Child.FilePath' = "C:\Windows\System32\wbem\WmiPrvSE.exe"
    }
) | ConvertTo-Json -Depth 10 -AsArray | Set-Content $unrelatedCloudPath -Encoding utf8NoBOM
foreach ($baselineName in $baselines) {
    $noMatchPath = Join-Path $OutputRoot ("validation-result.current-product-no-" + [IO.Path]::GetFileNameWithoutExtension($baselineName) + ".json")
    & dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $unrelatedCloudPath `
        --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
        --baseline (Join-Path $repositoryRoot "baselines\windows\$baselineName") `
        --out $noMatchPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
    if ($LASTEXITCODE -ne 0) { throw "WMI 当前产品未实现结论验证失败：$baselineName" }
    $noMatch = Get-Content $noMatchPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
    if ($noMatch.summary.pass -ne 0 -or @($noMatch.capabilities | Where-Object validation_status -eq "PASS").Count -gt 0) {
        throw "WmiOperation/ExecMethod 不应使 permanent WMI subscription 能力通过：$baselineName"
    }
}
Write-Host "[PASS] WMI 三项本地基准、规划映射、严格清理及当前产品未匹配结论均符合预期：$($local.FullName)"
