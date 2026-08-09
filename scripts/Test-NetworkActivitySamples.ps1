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
    $events = @($localRun.local_events | Where-Object { $_.case_run_id -eq $capability.case_run_id } | Sort-Object sequence)
    $facts = @{}
    $localRun.local_facts | Where-Object { $_.case_run_id -eq $capability.case_run_id } | ForEach-Object { $facts[$_.key] = $_.value }
    foreach ($event in $events | Where-Object { $_.event_type -eq "network" -and $_.data.stage.sequence -ne 2 }) {
        $actor = $event.data.actor
        if ($event.data.dns_client_service) {
            $cloudPid = $event.data.dns_client_service.pid
            $cloudExecutable = $event.data.dns_client_service.executable
            $cloudCommandLine = $event.data.dns_client_service.command_line_hint
            $cloudEntityId = "dnscache-$cloudPid"
        } else {
            $cloudPid = $actor.pid
            $cloudExecutable = $actor.executable
            $cloudCommandLine = $actor.command_line
            $cloudEntityId = $actor.program_instance_id
        }
        $cloudProgram = $localRun.programs | Where-Object { $_.case_run_id -eq $capability.case_run_id -and $_.pid -eq $cloudPid } | Select-Object -First 1
        $genericAction = if ($event.event_action -eq "file_download") { "tcp_connect" } else { $event.event_action }
        $calibratedBind = $capability.capability_id -in @("win.network.tcp", "win.network.udp")
        $syntheticGeneric += [ordered]@{
            fixture = "NetworkActivity"
            table = "NetworkActivity"
            event_id = $event.local_event_id
            host_id = $localRun.run.host.hostname
            event_time = $event.occurred_at_utc
            event_provider = if ($calibratedBind) { "KernelMon" } else { "Synthetic" }
            action = $genericAction
            actor_pid = $cloudPid
            actor_entity_id = $cloudEntityId
            actor_name = [System.IO.Path]::GetFileName($cloudExecutable)
            actor_executable = $cloudExecutable
            actor_command_line = $cloudCommandLine
            transport = $event.data.connection.transport
            direction = $event.data.connection.direction
            source_ip = if ($calibratedBind) { "0.0.0.0" } else { $event.data.connection.local.address }
            source_port = if ($calibratedBind) { 0 } else { $event.data.connection.local.port }
            endpoint_name = if ($calibratedBind) { "$($event.data.connection.transport)_0.0.0.0:0" } else { $null }
            destination_ip = $event.data.connection.remote.address
            destination_port = $event.data.connection.remote.port
            url = $event.data.http.url
            host = $event.data.http.host
            method = $event.data.http.method
            dns_question = $event.data.dns.question
            dns_answers = $event.data.dns.answers
        }

        $isWinInet = $event.data.subtest -eq "wininet"
        $isTcpUdp = $capability.capability_id -in @("win.network.tcp", "win.network.udp")
        $actionName = if ($isWinInet) { "HttpRequest" } else { "NetBind" }
        $syntheticTencent += [ordered]@{
            OS = "Windows"
            '@table' = "NetworkEvents"
            '@timestamp' = $event.observed_at_utc
            'Action.Type' = "Network"
            'Action.Name' = $actionName
            'Common.Source' = "KernelMon"
            'Common.EventUUId' = $event.local_event_id
            'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
            'Common.Mid' = $localRun.run.host.hostname
            'Environment.HostName' = $localRun.run.host.hostname
            'Parent.ProcPid' = $cloudPid
            'Parent.ProcGuid' = $cloudEntityId
            'Parent.FileName' = [System.IO.Path]::GetFileName($cloudExecutable)
            'Parent.FilePath' = $cloudExecutable
            'Parent.FileMd5' = $cloudProgram.md5
            'Parent.ProcCmdline' = $cloudCommandLine
            'Child.Protocol' = $event.data.connection.transport
            'Child.SrcIp' = if ($actionName -eq "NetBind") { "0.0.0.0" } else { $event.data.connection.local.address }
            'Child.SrcPort' = if ($actionName -eq "NetBind") { 0 } else { $event.data.connection.local.port }
            'Child.NodeName' = if ($actionName -eq "NetBind") { "$($event.data.connection.transport)_0.0.0.0:0" } else { $null }
            'Child.Direct' = if ($actionName -eq "HttpRequest") { "出站" } else { $null }
            'Child.DstIp' = if ($actionName -eq "HttpRequest") { $event.data.connection.remote.address } else { $null }
            'Child.DstPort' = if ($actionName -eq "HttpRequest") { $event.data.connection.remote.port } else { $null }
            'Child.Host' = if ($actionName -eq "HttpRequest") { $event.data.http.host } else { $null }
            'Child.Url' = if ($actionName -eq "HttpRequest") { $event.data.http.url } else { $null }
            'Child.FlowTags' = if ($actionName -eq "HttpRequest") { "HttpGet" } else { $null }
        }
    }

    $fileEvent = $events | Where-Object { $_.event_action -eq "file_download" -and $_.data.stage.sequence -eq 2 } | Select-Object -First 1
    if ($null -ne $fileEvent) {
        $actor = $fileEvent.data.actor
        $actorProgram = $localRun.programs | Where-Object { $_.program_instance_id -eq $fileEvent.actor_program_id } | Select-Object -First 1
        $syntheticGeneric += [ordered]@{
            fixture = "NetworkActivity"
            table = "NetworkDownloadedFile"
            event_id = $fileEvent.local_event_id
            host_id = $localRun.run.host.hostname
            event_time = $facts["network.download.file.occurred_at_utc"]
            actor_pid = $actor.pid
            actor_entity_id = $actor.program_instance_id
            actor_name = [System.IO.Path]::GetFileName($actor.executable)
            actor_executable = $actor.executable
            actor_command_line = $actor.command_line
            file_path = $facts["network.download.file.destination_path"]
            file_name = [System.IO.Path]::GetFileName($facts["network.download.file.destination_path"])
            file_size = $facts["network.download.file.size_bytes"]
            file_md5 = $facts["network.download.file.md5"]
            file_sha256 = $facts["network.download.file.sha256"]
        }
        $syntheticTencent += [ordered]@{
            OS = "Windows"
            '@table' = "FileEvents"
            '@timestamp' = $fileEvent.observed_at_utc
            'Action.Type' = "File"
            'Action.Name' = "FileWriteClose"
            'Child.FileCreateOpName' = "新建文件"
            'Common.EventUUId' = $fileEvent.local_event_id
            'Common.EventTime' = [DateTimeOffset]::Parse($facts["network.download.file.occurred_at_utc"]).ToUnixTimeMilliseconds()
            'Common.Mid' = $localRun.run.host.hostname
            'Environment.HostName' = $localRun.run.host.hostname
            'Parent.ProcPid' = $actor.pid
            'Parent.ProcGuid' = $actor.program_instance_id
            'Parent.FileName' = [System.IO.Path]::GetFileName($actor.executable)
            'Parent.FilePath' = $actor.executable
            'Parent.FileMd5' = $actorProgram.md5
            'Parent.ProcCmdline' = $actor.command_line
            'Child.FilePath' = $facts["network.download.file.destination_path"]
            'Child.FileName' = [System.IO.Path]::GetFileName($facts["network.download.file.destination_path"])
            'Child.FileSize' = $facts["network.download.file.size_bytes"]
            'Child.FileMd5' = $facts["network.download.file.md5"]
            'Child.FileTotalWrite' = $facts["network.download.file.size_bytes"]
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
$downloadLocalEvents = @($localRun.local_events | Where-Object { $_.event_action -eq "file_download" } | Sort-Object sequence)
$genericDownloadResult = $genericResult.capabilities | Where-Object capability_id -eq "win.network.file_download" | Select-Object -First 1
$genericConclusionPath = Join-Path ([System.IO.Path]::GetDirectoryName($genericResultPath)) `
    ([System.IO.Path]::GetFileNameWithoutExtension($genericResultPath) + "-conclusion.md")
$assertions = [ordered]@{
    runner_exit_code_is_0 = $runnerExitCode -eq 0
    run_completed = $localRun.run.status -eq "COMPLETED"
    capability_count_is_5 = @($localRun.capabilities).Count -eq 5
    all_capabilities_local_pass = @($localRun.capabilities | Where-Object status -ne "LOCAL_PASS").Count -eq 0
    program_count_is_19 = @($localRun.programs).Count -eq 19
    controller_count_is_5 = @($localRun.programs | Where-Object role -eq "controller").Count -eq 5
    actor_count_is_7 = @($localRun.programs | Where-Object role -eq "actor").Count -eq 7
    helper_count_is_7 = @($localRun.programs | Where-Object role -eq "helper").Count -eq 7
    event_count_is_8 = @($localRun.local_events).Count -eq 8
    event_actions_complete = (Compare-Object @("dns_query", "dns_query", "file_download", "file_download", "tcp_connect", "udp_connect", "url_access", "url_access") @($localRun.local_events.event_action | Sort-Object)).Count -eq 0
    download_has_2_ordered_local_stages = $downloadLocalEvents.Count -eq 2 -and `
        $downloadLocalEvents[0].data.stage.sequence -eq 1 -and $downloadLocalEvents[1].data.stage.sequence -eq 2 -and `
        ([DateTimeOffset]::Parse($downloadLocalEvents[0].occurred_at_utc) -le [DateTimeOffset]::Parse($downloadLocalEvents[1].occurred_at_utc))
    all_events_high_confidence = @($localRun.local_events | Where-Object confidence -ne "high").Count -eq 0
    cleanup_count_is_7 = @($localRun.cleanup_results).Count -eq 7
    all_cleanup_succeeded = @($localRun.cleanup_results | Where-Object status -ne "succeeded").Count -eq 0
    evidence_artifact_count_is_13 = @($localRun.artifacts).Count -eq 13
    all_event_evidence_refs_resolve = $invalidEvidenceRefs.Count -eq 0
    all_manifest_expected_facts_present = $missingExpectedFacts.Count -eq 0
    generic_compare_exit_code_is_0 = $genericExit -eq 0
    generic_compare_pass_count_is_5 = $genericResult.summary.pass -eq 5
    generic_download_stage_flow_pass = $genericDownloadResult.stage_flow.strategy -eq "ordered_all" -and `
        $genericDownloadResult.stage_flow.status -eq "PASS" -and $genericDownloadResult.stage_results.Count -eq 2 -and `
        $genericDownloadResult.stage_results[1].order_status -eq "passed"
    generic_download_conclusion_describes_two_stages = (Get-Content $genericConclusionPath -Raw).Contains("有序二轮验证")
    tencent_compare_exit_code_is_0 = $tencentExit -eq 0
    tencent_compare_has_3_pass_2_partial = $tencentResult.summary.pass -eq 3 -and $tencentResult.summary.partial -eq 2 -and `
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
