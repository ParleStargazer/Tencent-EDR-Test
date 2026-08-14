[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\scheduled-task-activity-e2e"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$samplesRoot = Join-Path $repositoryRoot "samples"
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

& (Join-Path $PSScriptRoot "Build-ScheduledTaskActivitySamples.ps1") -Configuration $Configuration -SamplesRoot $samplesRoot
if ($LASTEXITCODE -ne 0) { throw "计划任务活动能力包构建失败。" }

$capabilityIds = @("win.scheduled_task.create", "win.scheduled_task.modify", "win.scheduled_task.delete")
$runnerArguments = @(
    "run", "--runs-dir", (Join-Path $OutputRoot "runs"), "--suite-id", "scheduled-task-activity-e2e", "--next-delay-seconds", "0"
)
foreach ($capabilityId in $capabilityIds) {
    $runnerArguments += @("--manifest", (Join-Path $samplesRoot "$capabilityId\capability.json"))
}
$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
& dotnet --roll-forward Major $runner @runnerArguments
if ($LASTEXITCODE -ne 0) { throw "Runner 执行失败，退出码：$LASTEXITCODE" }

$localRunFile = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter "local-run.json" -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $localRunFile) { throw "测试结束但未找到 local-run.json。" }
$localRun = Get-Content $localRunFile.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
$failedCapabilities = @($localRun.capabilities | Where-Object status -ne "LOCAL_PASS")
$failedCleanup = @($localRun.cleanup_results | Where-Object status -ne "succeeded")
$missingFacts = @()
foreach ($capability in $localRun.capabilities) {
    $manifest = Get-Content (Join-Path $samplesRoot "$($capability.capability_id)\capability.json") -Raw | ConvertFrom-Json -Depth 30
    $actual = @($localRun.local_facts | Where-Object case_run_id -eq $capability.case_run_id | ForEach-Object key)
    foreach ($key in $manifest.expected_fact_keys) {
        if ($key -notin $actual) { $missingFacts += "$($capability.capability_id):$key" }
    }
}

$genericCloud = @()
$tencentCloud = @()
foreach ($capability in $localRun.capabilities) {
    $facts = @{}
    $localRun.local_facts | Where-Object case_run_id -eq $capability.case_run_id | ForEach-Object { $facts[$_.key] = $_.value }
    $event = $localRun.local_events | Where-Object case_run_id -eq $capability.case_run_id | Select-Object -First 1
    $actor = $localRun.programs | Where-Object program_instance_id -eq $event.actor_program_id | Select-Object -First 1
    $operation = $event.event_action
    $eventLogId = switch ($operation) { "create" { 4698 } "modify" { 4702 } "delete" { 4699 } }
    $actionName = switch ($operation) { "create" { "SchedTaskCreate" } "modify" { "SchedTaskUpdate" } "delete" { "SchedTaskDelete" } }
    $taskContent = if ($operation -eq "delete") {
        "<Task><Description>$($facts['scheduled_task.before.marker'])</Description></Task>"
    } else {
        "<Task><Description>$($facts['scheduled_task.marker'])</Description></Task>"
    }
    $genericCloud += [ordered]@{
        table = "ScheduledTaskActivity"; event_id = $event.local_event_id; host_id = $localRun.run.host.machine_id
        host_name = $localRun.run.host.hostname; event_time = $event.occurred_at_utc; action = $operation
        actor_pid = $actor.pid; actor_name = $actor.file_name; actor_executable = $actor.executable
        actor_command_line = $actor.command_line; subject_user_name = $env:USERNAME; subject_domain_name = $env:USERDOMAIN
        subject_user_sid = if ($operation -eq "delete") { $facts["scheduled_task.before.principal"] } else { $facts["scheduled_task.after.principal"] }
        event_log_id = $eventLogId; task_name = $facts["scheduled_task.task_path"]; task_content = $taskContent
    }
    $record = [ordered]@{
        OS = "Windows"; '@table' = "ScheduleTaskEvents"; '@timestamp' = $event.observed_at_utc
        'Action.Type' = "WinEventLog"; 'Action.Name' = $actionName; 'Action.EventLogId' = $eventLogId
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $localRun.run.host.machine_id; 'Environment.HostName' = $localRun.run.host.hostname
        'Parent.ProcPid' = $actor.pid; 'Parent.FileName' = $actor.file_name; 'Parent.FilePath' = $actor.executable
        'Parent.ProcCmdline' = $actor.command_line; 'Child.SubjectUserName' = $env:USERNAME
        'Child.SubjectDomainName' = $env:USERDOMAIN
        'Child.SubjectUserSid' = if ($operation -eq "delete") { $facts["scheduled_task.before.principal"] } else { $facts["scheduled_task.after.principal"] }
        'Child.TaskName' = $facts["scheduled_task.task_path"]; 'Child.NodeName' = $facts["scheduled_task.task_path"]
    }
    if ($operation -eq "modify") { $record['Child.TaskContentNew'] = $taskContent }
    elseif ($operation -eq "create") { $record['Child.TaskContent'] = $taskContent }
    $tencentCloud += $record
}

$genericPath = Join-Path $OutputRoot "synthetic-cloud.scheduled-task.json"
$tencentPath = Join-Path $OutputRoot "synthetic-cloud.tencent-schedule-task-events.json"
$genericCloud | ConvertTo-Json -Depth 20 | Set-Content $genericPath -Encoding utf8NoBOM
$tencentCloud | ConvertTo-Json -Depth 20 | Set-Content $tencentPath -Encoding utf8NoBOM
$baselineNames = @("scheduled_task_create.yaml", "scheduled_task_modify.yaml", "scheduled_task_delete.yaml")
$baselineArguments = @()
foreach ($name in $baselineNames) { $baselineArguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$name")) }

$genericResultPath = Join-Path $OutputRoot "validation-result.synthetic.json"
& dotnet --roll-forward Major $runner compare --local $localRunFile.FullName --cloud $genericPath `
    --mapping (Join-Path $repositoryRoot "mappings\generic-scheduled-task-activity-v1.yaml") @baselineArguments --out $genericResultPath
$genericExit = $LASTEXITCODE
$tencentResultPath = Join-Path $OutputRoot "validation-result.tencent-mapping.json"
& dotnet --roll-forward Major $runner compare --local $localRunFile.FullName --cloud $tencentPath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") @baselineArguments --out $tencentResultPath
$tencentExit = $LASTEXITCODE
$genericResult = Get-Content $genericResultPath -Raw | ConvertFrom-Json -Depth 100
$tencentResult = Get-Content $tencentResultPath -Raw | ConvertFrom-Json -Depth 100

$tasksRemoved = $true
foreach ($capability in $localRun.capabilities) {
    $taskPath = $localRun.local_facts |
        Where-Object { $_.case_run_id -eq $capability.case_run_id -and $_.key -eq "scheduled_task.task_path" } |
        Select-Object -First 1 -ExpandProperty value
    if ([string]::IsNullOrWhiteSpace($taskPath) -or -not $taskPath.StartsWith("\EdrTest_", [System.StringComparison]::Ordinal)) {
        $tasksRemoved = $false
        continue
    }
    $queryOutput = & schtasks.exe /Query /TN $taskPath 2>&1
    if ($LASTEXITCODE -eq 0) { $tasksRemoved = $false }
}

$assertions = [ordered]@{
    run_completed = $localRun.run.status -eq "COMPLETED"
    capability_count_is_3 = @($localRun.capabilities).Count -eq 3
    all_capabilities_local_pass = $failedCapabilities.Count -eq 0
    program_count_is_6 = @($localRun.programs).Count -eq 6
    event_count_is_3 = @($localRun.local_events).Count -eq 3
    artifact_count_is_3 = @($localRun.artifacts).Count -eq 3
    cleanup_count_is_3 = @($localRun.cleanup_results).Count -eq 3
    all_cleanup_succeeded = $failedCleanup.Count -eq 0
    all_manifest_expected_facts_present = $missingFacts.Count -eq 0
    all_exact_test_tasks_removed = $tasksRemoved
    generic_compare_exit_code_is_0 = $genericExit -eq 0
    generic_compare_pass_count_is_3 = $genericResult.summary.pass -eq 3
    tencent_compare_exit_code_is_0 = $tencentExit -eq 0
    tencent_compare_pass_count_is_3 = $tencentResult.summary.pass -eq 3
}
$failedAssertions = @($assertions.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)
$summary = [ordered]@{
    schema_version = "1.0"; test_suite = "scheduled-task-activity-e2e"
    status = if ($failedAssertions.Count -eq 0) { "PASS" } else { "FAIL" }
    tested_at_utc = [DateTimeOffset]::UtcNow.ToString("O"); local_run = $localRunFile.FullName
    assertions = $assertions; failed_assertions = $failedAssertions
}
$summaryPath = Join-Path $OutputRoot "test-summary.json"
$summary | ConvertTo-Json -Depth 20 | Set-Content $summaryPath -Encoding utf8NoBOM
if ($failedAssertions.Count -gt 0) { throw "计划任务活动端到端断言失败：$($failedAssertions -join ', ')。结果：$summaryPath" }
Write-Host "[PASS] 计划任务活动三项能力端到端测试通过。"
Write-Host "本地导出：$($localRunFile.FullName)"
Write-Host "测试摘要：$summaryPath"
