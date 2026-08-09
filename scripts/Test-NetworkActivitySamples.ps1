[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot "artifacts\network-activity-e2e" }
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$samplesRoot = Join-Path $repositoryRoot "samples"
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

& (Join-Path $PSScriptRoot "Build-NetworkActivitySamples.ps1") -Configuration $Configuration -SamplesRoot $samplesRoot
if ($LASTEXITCODE -ne 0) { throw "网络活动能力包构建失败。" }

$capabilityIds = @(
    "win.network.tcp", "win.network.udp", "win.network.url", "win.network.dns", "win.network.file_download"
)
$runnerArguments = @(
    "run", "--runs-dir", (Join-Path $OutputRoot "runs"), "--suite-id", "network-activity-e2e", "--next-delay-seconds", "0"
)
foreach ($capabilityId in $capabilityIds) {
    $runnerArguments += @("--manifest", (Join-Path $samplesRoot "$capabilityId\capability.json"))
}

$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path $runner)) {
    dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "框架构建失败。" }
}

& dotnet --roll-forward Major $runner @runnerArguments
$runnerExitCode = $LASTEXITCODE
if ($runnerExitCode -ne 0) { throw "Runner 执行失败，退出码：$runnerExitCode" }

$localRunFile = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter "local-run.json" -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $localRunFile) { throw "测试结束但未找到 local-run.json。" }
$localRun = Get-Content $localRunFile.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String

$missingExpectedFacts = @()
foreach ($capability in $localRun.capabilities) {
    $manifest = Get-Content (Join-Path $samplesRoot "$($capability.capability_id)\capability.json") -Raw | ConvertFrom-Json -Depth 30
    $actualFactKeys = @($localRun.local_facts | Where-Object { $_.case_run_id -eq $capability.case_run_id } | ForEach-Object key)
    foreach ($expectedFactKey in $manifest.expected_fact_keys) {
        if ($expectedFactKey -notin $actualFactKeys) { $missingExpectedFacts += "$($capability.capability_id):$expectedFactKey" }
    }
}

$syntheticGeneric = @()
$syntheticTencent = @()
foreach ($capability in $localRun.capabilities) {
    $event = $localRun.local_events | Where-Object { $_.case_run_id -eq $capability.case_run_id } | Select-Object -First 1
    $actor = $event.data.actor
    $actorProgram = $localRun.programs | Where-Object { $_.program_instance_id -eq $event.actor_program_id } | Select-Object -First 1
    $facts = @{}
    $localRun.local_facts | Where-Object { $_.case_run_id -eq $capability.case_run_id } | ForEach-Object { $facts[$_.key] = $_.value }
    $syntheticGeneric += [ordered]@{
        fixture = "NetworkActivity"
        table = "NetworkActivity"
        event_id = $event.local_event_id
        host_id = $localRun.run.host.hostname
        event_time = $event.occurred_at_utc
        action = if ($event.event_action -eq "file_download") { "url_access" } else { $event.event_action }
        actor_pid = $actor.pid
        actor_entity_id = $actor.program_instance_id
        actor_name = [System.IO.Path]::GetFileName($actor.executable)
        actor_executable = $actor.executable
        actor_command_line = $actor.command_line
        transport = $event.data.connection.transport
        direction = $event.data.connection.direction
        source_ip = $event.data.connection.local.address
        source_port = $event.data.connection.local.port
        destination_ip = $event.data.connection.remote.address
        destination_port = $event.data.connection.remote.port
        url = $event.data.http.url
        host = $event.data.http.host
        method = $event.data.http.method
        dns_question = $event.data.dns.question
        dns_answers = $event.data.dns.answers
    }
    $actionName = if ($event.event_action -eq "url_access") {
        "FutureHttpRequest" # 刻意使用未知动作名，验证候选召回不依赖 Action.Name。
    } elseif ($event.event_action -eq "file_download") {
        "HttpRequest"
    } else {
        "SocketRequest"
    }
    $syntheticTencent += [ordered]@{
        OS = "Windows"
        '@table' = "NetworkEvents"
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = "Network"
        'Action.Name' = $actionName
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $localRun.run.host.hostname
        'Environment.HostName' = $localRun.run.host.hostname
        'Parent.ProcPid' = $actor.pid
        'Parent.ProcGuid' = $actor.program_instance_id
        'Parent.FileName' = [System.IO.Path]::GetFileName($actor.executable)
        'Parent.FilePath' = $actor.executable
        'Parent.FileMd5' = $actorProgram.md5
        'Parent.ProcCmdline' = $actor.command_line
        'Child.Protocol' = $event.data.connection.transport
        'Child.Direct' = "出站"
        'Child.SrcIp' = $event.data.connection.local.address
        'Child.SrcPort' = $event.data.connection.local.port
        'Child.DstIp' = $event.data.connection.remote.address
        'Child.DstPort' = $event.data.connection.remote.port
        'Child.Host' = $event.data.http.host
        'Child.Url' = $event.data.http.url
        'Child.FlowTags' = if ($event.event_action -in @("url_access", "file_download")) { "HttpGet" } else { $null }
    }
    if ($event.event_action -eq "file_download") {
        $syntheticGeneric += [ordered]@{
            fixture = "NetworkActivity"
            table = "NetworkDownloadedFile"
            event_id = "$($event.local_event_id)-file"
            host_id = $localRun.run.host.hostname
            event_time = $facts["network.download.file_occurred_at_utc"]
            actor_pid = $actor.pid
            actor_entity_id = $actor.program_instance_id
            actor_name = [System.IO.Path]::GetFileName($actor.executable)
            actor_executable = $actor.executable
            actor_command_line = $actor.command_line
            file_path = $facts["network.download.destination_path"]
            file_name = [System.IO.Path]::GetFileName($facts["network.download.destination_path"])
            file_size = $facts["network.download.size_bytes"]
            file_md5 = $facts["network.download.md5"]
            file_sha256 = $facts["network.download.sha256"]
        }
        $syntheticTencent += [ordered]@{
            OS = "Windows"
            '@table' = "FileEvents"
            '@timestamp' = $event.observed_at_utc
            'Action.Type' = "File"
            'Action.Name' = "FileWriteClose"
            'Child.FileCreateOpName' = "新建文件"
            'Common.EventUUId' = "$($event.local_event_id)-file"
            'Common.EventTime' = [DateTimeOffset]::Parse($facts["network.download.file_occurred_at_utc"]).ToUnixTimeMilliseconds()
            'Common.Mid' = $localRun.run.host.hostname
            'Environment.HostName' = $localRun.run.host.hostname
            'Parent.ProcPid' = $actor.pid
            'Parent.ProcGuid' = $actor.program_instance_id
            'Parent.FileName' = [System.IO.Path]::GetFileName($actor.executable)
            'Parent.FilePath' = $actor.executable
            'Parent.FileMd5' = $actorProgram.md5
            'Parent.ProcCmdline' = $actor.command_line
            'Child.FilePath' = $facts["network.download.destination_path"]
            'Child.FileName' = [System.IO.Path]::GetFileName($facts["network.download.destination_path"])
            'Child.FileSize' = $facts["network.download.size_bytes"]
            'Child.FileMd5' = $facts["network.download.md5"]
            'Child.FileTotalWrite' = $facts["network.download.size_bytes"]
        }
    }
}

$baselineNames = @("network_tcp.yaml", "network_udp.yaml", "network_url.yaml", "network_dns.yaml", "network_file_download.yaml")
function Invoke-NetworkComparison([string]$CloudPath, [string]$MappingPath, [string]$ResultPath) {
    $arguments = @("compare", "--local", $localRunFile.FullName, "--cloud", $CloudPath, "--mapping", $MappingPath, "--out", $ResultPath)
    foreach ($name in $baselineNames) { $arguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$name")) }
    & dotnet --roll-forward Major $runner @arguments | Out-Host
    $comparisonExitCode = $LASTEXITCODE
    return $comparisonExitCode
}

$genericPath = Join-Path $OutputRoot "synthetic-cloud.network-generic.json"
$syntheticGeneric | ConvertTo-Json -Depth 30 | Set-Content $genericPath -Encoding utf8NoBOM
$genericResultPath = Join-Path $OutputRoot "validation-result.network-generic.json"
$genericExit = Invoke-NetworkComparison $genericPath (Join-Path $repositoryRoot "mappings\generic-network-activity-v1.yaml") $genericResultPath
$genericResult = Get-Content $genericResultPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String

$tencentPath = Join-Path $OutputRoot "synthetic-cloud.network-tencent.json"
$syntheticTencent | ConvertTo-Json -Depth 30 | Set-Content $tencentPath -Encoding utf8NoBOM
$tencentResultPath = Join-Path $OutputRoot "validation-result.network-tencent.json"
$tencentExit = Invoke-NetworkComparison $tencentPath (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") $tencentResultPath
$tencentResult = Get-Content $tencentResultPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String

$artifactIds = @($localRun.artifacts | ForEach-Object artifact_id)
$invalidEvidenceRefs = @($localRun.local_events | ForEach-Object {
    foreach ($reference in $_.evidence_refs) { if ($reference -notin $artifactIds) { $reference } }
})
$assertions = [ordered]@{
    runner_exit_code_is_0 = $runnerExitCode -eq 0
    run_completed = $localRun.run.status -eq "COMPLETED"
    capability_count_is_5 = @($localRun.capabilities).Count -eq 5
    all_capabilities_local_pass = @($localRun.capabilities | Where-Object status -ne "LOCAL_PASS").Count -eq 0
    program_count_is_15 = @($localRun.programs).Count -eq 15
    controller_count_is_5 = @($localRun.programs | Where-Object role -eq "controller").Count -eq 5
    actor_count_is_5 = @($localRun.programs | Where-Object role -eq "actor").Count -eq 5
    helper_count_is_5 = @($localRun.programs | Where-Object role -eq "helper").Count -eq 5
    event_count_is_5 = @($localRun.local_events).Count -eq 5
    event_actions_complete = (Compare-Object @("dns_query", "file_download", "tcp_connect", "udp_connect", "url_access") @($localRun.local_events.event_action | Sort-Object)).Count -eq 0
    all_events_high_confidence = @($localRun.local_events | Where-Object confidence -ne "high").Count -eq 0
    cleanup_count_is_5 = @($localRun.cleanup_results).Count -eq 5
    all_cleanup_succeeded = @($localRun.cleanup_results | Where-Object status -ne "succeeded").Count -eq 0
    evidence_artifact_count_is_10 = @($localRun.artifacts).Count -eq 10
    all_event_evidence_refs_resolve = $invalidEvidenceRefs.Count -eq 0
    all_manifest_expected_facts_present = $missingExpectedFacts.Count -eq 0
    generic_compare_exit_code_is_0 = $genericExit -eq 0
    generic_compare_pass_count_is_5 = $genericResult.summary.pass -eq 5
    tencent_compare_exit_code_is_0 = $tencentExit -eq 0
    tencent_compare_has_4_pass_1_partial = $tencentResult.summary.pass -eq 4 -and $tencentResult.summary.partial -eq 1 -and `
        $tencentResult.summary.fail -eq 0 -and $tencentResult.summary.inconclusive -eq 0 -and $tencentResult.summary.not_compared -eq 0
}
$failedAssertions = @($assertions.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)
$summary = [ordered]@{
    schema_version = "1.0"
    test_suite = "network-activity-e2e"
    status = if ($failedAssertions.Count -eq 0) { "PASS" } else { "FAIL" }
    tested_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    local_run = $localRunFile.FullName
    capability_ids = @($localRun.capabilities.capability_id)
    assertions = $assertions
    failed_assertions = $failedAssertions
    missing_expected_facts = $missingExpectedFacts
}
$summaryPath = Join-Path $OutputRoot "network-activity-e2e-summary.json"
$summary | ConvertTo-Json -Depth 30 | Set-Content $summaryPath -Encoding utf8NoBOM
$summary | ConvertTo-Json -Depth 30
if ($failedAssertions.Count -gt 0) { throw "Network Activity 端到端测试失败：$($failedAssertions -join ', ')" }
