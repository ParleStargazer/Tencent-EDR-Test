[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\service-activity-e2e"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$samplesRoot = Join-Path $repositoryRoot "samples"

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "服务活动端到端测试需要管理员权限。请使用管理员 PowerShell 重新运行。"
    }
} finally {
    $identity.Dispose()
}
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

& (Join-Path $PSScriptRoot "Build-ServiceActivitySamples.ps1") -Configuration $Configuration -SamplesRoot $samplesRoot
if ($LASTEXITCODE -ne 0) { throw "服务活动能力包构建失败。" }

$capabilityIds = @("win.service.create", "win.service.modify", "win.service.delete")
$runnerArguments = @(
    "run", "--runs-dir", (Join-Path $OutputRoot "runs"), "--suite-id", "service-activity-e2e", "--next-delay-seconds", "0"
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
    $actor = $localRun.programs | Where-Object { $_.case_run_id -eq $capability.case_run_id -and $_.role -eq "actor" } | Select-Object -First 1
    $operation = [string]$event.event_action
    $eventTime = [string]$facts["service.completed_at_utc"]
    $serviceName = [string]$facts["service.name"]
    $displayName = if ($operation -eq "delete") { [string]$facts["service.before.display_name"] } else { [string]$facts["service.display_name"] }
    $binaryPath = if ($operation -eq "delete") { [string]$facts["service.before.binary_path"] } else { [string]$facts["service.binary_path"] }
    $eventTimeMs = [DateTimeOffset]::Parse($eventTime).ToUnixTimeMilliseconds()
    $apiEventTime = [DateTimeOffset]::Parse($eventTime).AddMilliseconds(-1)
    $apiEventTimeText = $apiEventTime.ToString("O")
    $apiEventTimeMs = $apiEventTime.ToUnixTimeMilliseconds()
    $apiAction = switch ($operation) { "create" { "CreateServiceW" } "modify" { "ChangeServiceConfigW" } "delete" { "DeleteService" } }

    $genericCloud += [ordered]@{
        table = "ServiceActivity"; event_id = "$($event.local_event_id)-api"; host_id = $localRun.run.host.machine_id
        host_name = $localRun.run.host.hostname; event_time = $apiEventTimeText; event_type = "service_api"; action = $operation
        actor_pid = $actor.pid; actor_name = $actor.file_name; actor_executable = $actor.executable
        actor_command_line = $actor.command_line; user_name = $env:USERNAME; user_domain = $env:USERDOMAIN
        event_log_id = $null; service_name = $serviceName; service_display_name = $displayName
        service_binary_path = $binaryPath; service_start_type = "disabled"
        service_old_start_type = if ($operation -eq "modify") { "demand" } else { $null }
        service_account = "LocalSystem"; service_type = "win32_own_process"; service_state = "stopped"
    }
    $tencentCloud += [ordered]@{
        OS = "Windows"; '@table' = "ServiceEvents"; '@timestamp' = $apiEventTimeText
        'Action.Type' = "InjectHook"; 'Action.Name' = $apiAction; 'Common.EventUUId' = "$($event.local_event_id)-api"
        'Common.EventTime' = $apiEventTimeMs; 'Common.Mid' = $localRun.run.host.machine_id
        'Environment.HostName' = $localRun.run.host.hostname; 'Parent.ProcPid' = $actor.pid
        'Parent.FileName' = $actor.file_name; 'Parent.FilePath' = $actor.executable; 'Parent.ProcCmdline' = $actor.command_line
        'Child.ServiceName' = $serviceName; 'Child.DisplayName' = $displayName; 'Child.BinaryPath' = $binaryPath
        'Child.NewStartType' = "disabled"; 'Child.OldStartType' = if ($operation -eq "modify") { "demand" } else { $null }
        'Child.ServiceAccount' = "LocalSystem"; 'Child.ServiceType' = "win32_own_process"
    }
    if ($operation -eq "delete") { continue }

    $systemEventId = if ($operation -eq "create") { 7045 } else { 7040 }
    $genericCloud += [ordered]@{
        table = "ServiceActivity"; event_id = "$($event.local_event_id)-system"; host_id = $localRun.run.host.machine_id
        host_name = $localRun.run.host.hostname; event_time = $eventTime; event_type = "service"; action = $operation
        actor_pid = 748; actor_name = "services.exe"; actor_executable = "C:\Windows\System32\services.exe"
        actor_command_line = "C:\Windows\System32\services.exe"; event_log_id = $systemEventId
        service_name = $serviceName; service_display_name = $displayName; service_binary_path = $binaryPath
        service_start_type = "disabled"; service_old_start_type = if ($operation -eq "modify") { "demand" } else { $null }
        service_account = "LocalSystem"; service_type = "win32_own_process"; service_state = "stopped"
    }
    $tencentCloud += [ordered]@{
        OS = "Windows"; '@table' = "SystemEvents"; '@timestamp' = $eventTime
        'Action.Type' = "WinEventLog"; 'Action.Name' = if ($operation -eq "create") { "ServiceInstall" } else { "ServiceConfigChange" }
        'Action.EventLogId' = $systemEventId; 'Common.EventUUId' = "$($event.local_event_id)-system"
        'Common.EventTime' = $eventTimeMs; 'Common.Mid' = $localRun.run.host.machine_id
        'Environment.HostName' = $localRun.run.host.hostname; 'Parent.ProcPid' = 748
        'Parent.FileName' = "services.exe"; 'Parent.FilePath' = "C:\Windows\System32\services.exe"
        'Parent.ProcCmdline' = "C:\Windows\System32\services.exe"; 'Child.ServiceName' = $serviceName
        'Child.DisplayName' = $displayName; 'Child.ServiceFileName' = $binaryPath; 'Child.StartType' = "disabled"
        'Child.OldStartType' = if ($operation -eq "modify") { "demand" } else { $null }
        'Child.ServiceAccount' = "LocalSystem"; 'Child.ServiceType' = "win32_own_process"
    }
}

$genericPath = Join-Path $OutputRoot "synthetic-cloud.service.json"
$tencentPath = Join-Path $OutputRoot "synthetic-cloud.tencent-service-events.json"
$genericCloud | ConvertTo-Json -Depth 20 | Set-Content $genericPath -Encoding utf8NoBOM
$tencentCloud | ConvertTo-Json -Depth 20 | Set-Content $tencentPath -Encoding utf8NoBOM
$baselineArguments = @()
foreach ($name in @("service_create.yaml", "service_modify.yaml", "service_delete.yaml")) {
    $baselineArguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$name"))
}

$genericResultPath = Join-Path $OutputRoot "validation-result.synthetic.json"
& dotnet --roll-forward Major $runner compare --local $localRunFile.FullName --cloud $genericPath `
    --mapping (Join-Path $repositoryRoot "mappings\generic-service-activity-v1.yaml") @baselineArguments --out $genericResultPath
$genericExit = $LASTEXITCODE
$tencentResultPath = Join-Path $OutputRoot "validation-result.tencent-mapping.json"
& dotnet --roll-forward Major $runner compare --local $localRunFile.FullName --cloud $tencentPath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") @baselineArguments --out $tencentResultPath
$tencentExit = $LASTEXITCODE
$genericResult = Get-Content $genericResultPath -Raw | ConvertFrom-Json -Depth 100
$tencentResult = Get-Content $tencentResultPath -Raw | ConvertFrom-Json -Depth 100
$genericDualMethods = @($genericResult.capabilities | Where-Object { $_.capability_id -in @("win.service.create", "win.service.modify") } | ForEach-Object method_results)
$tencentDualMethods = @($tencentResult.capabilities | Where-Object { $_.capability_id -in @("win.service.create", "win.service.modify") } | ForEach-Object method_results)

$servicesRemoved = $true
foreach ($serviceName in @($localRun.local_facts | Where-Object key -eq "service.name" | ForEach-Object value)) {
    if ([string]::IsNullOrWhiteSpace($serviceName) -or -not $serviceName.StartsWith("EdrTestSvc_", [System.StringComparison]::Ordinal)) {
        $servicesRemoved = $false
        continue
    }
    if ($null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { $servicesRemoved = $false }
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
    all_exact_test_services_removed = $servicesRemoved
    generic_compare_exit_code_is_0 = $genericExit -eq 0
    generic_compare_pass_count_is_3 = $genericResult.summary.pass -eq 3
    generic_all_four_alternative_methods_pass = $genericDualMethods.Count -eq 4 -and @($genericDualMethods | Where-Object status -ne "PASS").Count -eq 0
    tencent_compare_exit_code_is_0 = $tencentExit -eq 0
    tencent_compare_pass_count_is_3 = $tencentResult.summary.pass -eq 3
    tencent_all_four_alternative_methods_pass = $tencentDualMethods.Count -eq 4 -and @($tencentDualMethods | Where-Object status -ne "PASS").Count -eq 0
}
$failedAssertions = @($assertions.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)
$summary = [ordered]@{
    schema_version = "1.0"; test_suite = "service-activity-e2e"
    status = if ($failedAssertions.Count -eq 0) { "PASS" } else { "FAIL" }
    tested_at_utc = [DateTimeOffset]::UtcNow.ToString("O"); local_run = $localRunFile.FullName
    assertions = $assertions; failed_assertions = $failedAssertions
}
$summaryPath = Join-Path $OutputRoot "test-summary.json"
$summary | ConvertTo-Json -Depth 20 | Set-Content $summaryPath -Encoding utf8NoBOM
if ($failedAssertions.Count -gt 0) { throw "服务活动端到端断言失败：$($failedAssertions -join ', ')。结果：$summaryPath" }
Write-Host "[PASS] 服务活动三项能力端到端测试通过。"
Write-Host "本地导出：$($localRunFile.FullName)"
Write-Host "测试摘要：$summaryPath"
