[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\process-activity-e2e"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$samplesRoot = Join-Path $repositoryRoot "samples"
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

& (Join-Path $PSScriptRoot "Build-ProcessActivitySamples.ps1") `
    -Configuration $Configuration -SamplesRoot $samplesRoot
if ($LASTEXITCODE -ne 0) { throw "能力包构建失败。" }

$capabilityIds = @(
    "win.process.create",
    "win.process.terminate",
    "win.process.access",
    "win.process.image_load",
    "win.process.remote_thread",
    "win.process.tampering"
)
$runnerArguments = @(
    "run", "--runs-dir", (Join-Path $OutputRoot "runs"), "--suite-id", "process-activity-e2e",
    "--allow-high-risk"
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
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $localRunFile) { throw "测试结束但未找到 local-run.json。" }

$localRun = Get-Content $localRunFile.FullName -Raw | ConvertFrom-Json -Depth 100
$expectedActions = @("access", "create", "image_load", "remote_thread_create", "tamper", "terminate")
$actualActions = @($localRun.local_events | ForEach-Object { $_.event_action } | Sort-Object -Unique)
$failedCapabilities = @($localRun.capabilities | Where-Object { $_.status -ne "LOCAL_PASS" })
$failedCleanup = @($localRun.cleanup_results | Where-Object { $_.status -ne "succeeded" })
$missingExpectedFacts = @()
foreach ($capability in $localRun.capabilities) {
    $manifestPath = Join-Path $samplesRoot "$($capability.capability_id)\capability.json"
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -Depth 30
    $actualFactKeys = @($localRun.local_facts |
        Where-Object { $_.case_run_id -eq $capability.case_run_id } |
        ForEach-Object { $_.key })
    foreach ($expectedFactKey in $manifest.expected_fact_keys) {
        if ($expectedFactKey -notin $actualFactKeys) {
            $missingExpectedFacts += "$($capability.capability_id):$expectedFactKey"
        }
    }
}
$memoryReleaseFact = $localRun.local_facts | Where-Object { $_.key -eq "process.memory_released" } | Select-Object -First 1
$artifactIds = @($localRun.artifacts | ForEach-Object { $_.artifact_id })
$invalidEvidenceRefs = @($localRun.local_events | ForEach-Object {
    foreach ($evidenceRef in $_.evidence_refs) {
        if ($evidenceRef -notin $artifactIds) { $evidenceRef }
    }
})

$syntheticCloud = @()
foreach ($capability in $localRun.capabilities) {
    $events = @($localRun.local_events | Where-Object { $_.case_run_id -eq $capability.case_run_id })
    foreach ($event in $events) {
        $eventActor = $event.data.actor
        $eventTarget = $event.data.target
        $syntheticCloud += [ordered]@{
            table = "ProcessActivity"
            event_id = $event.local_event_id
            host_id = $localRun.run.host.hostname
            event_time = $event.occurred_at_utc
            action = $event.event_action
            target_pid = $eventTarget.pid
            target_entity_id = $eventTarget.program_instance_id
            target_executable = $eventTarget.executable
            target_command_line = $eventTarget.command_line
            actor_pid = $eventActor.pid
            actor_entity_id = $eventActor.program_instance_id
            actor_executable = $eventActor.executable
            actor_command_line = $eventActor.command_line
            exit_code = $event.data.termination.observed_exit_code
            file_path = $event.data.image.path
            file_name = $event.data.image.file_name
            thread_id = $event.data.thread.thread_id
        }
    }
}
$syntheticCloudPath = Join-Path $OutputRoot "synthetic-cloud.process-activity.json"
$syntheticCloud | ConvertTo-Json -Depth 20 | Set-Content $syntheticCloudPath -Encoding utf8NoBOM
$validationPath = Join-Path $OutputRoot "validation-result.synthetic.json"
$comparisonArguments = @(
    "compare", "--local", $localRunFile.FullName,
    "--cloud", $syntheticCloudPath,
    "--mapping", (Join-Path $repositoryRoot "mappings\generic-process-activity-v1.yaml"),
    "--out", $validationPath
)
$baselineNames = @(
    "process_create.yaml",
    "process_terminate.yaml",
    "process_access.yaml",
    "process_image_load.yaml",
    "process_remote_thread.yaml",
    "process_tampering.yaml"
)
foreach ($baselineName in $baselineNames) {
    $comparisonArguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$baselineName"))
}
& dotnet --roll-forward Major $runner @comparisonArguments
$comparisonExitCode = $LASTEXITCODE
$validation = if (Test-Path $validationPath) {
    Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100
} else {
    $null
}

$assertions = [ordered]@{
    runner_exit_code_is_0 = $runnerExitCode -eq 0
    run_completed = $localRun.run.status -eq "COMPLETED"
    capability_count_is_6 = @($localRun.capabilities).Count -eq 6
    all_capabilities_local_pass = $failedCapabilities.Count -eq 0
    program_count_is_19 = @($localRun.programs).Count -eq 19
    image_load_helper_count_is_1 = @($localRun.programs | Where-Object { $_.role -eq "helper" }).Count -eq 1
    event_count_is_10 = @($localRun.local_events).Count -eq 10
    image_load_event_count_is_5 = @($localRun.local_events | Where-Object { $_.event_action -eq "image_load" }).Count -eq 5
    event_actions_complete = (Compare-Object $expectedActions $actualActions).Count -eq 0
    all_events_high_confidence = @($localRun.local_events | Where-Object { $_.confidence -ne "high" }).Count -eq 0
    cleanup_count_is_6 = @($localRun.cleanup_results).Count -eq 6
    all_cleanup_succeeded = $failedCleanup.Count -eq 0
    evidence_artifact_count_is_6 = @($localRun.artifacts).Count -eq 6
    all_event_evidence_refs_resolve = $invalidEvidenceRefs.Count -eq 0
    nonce_fact_count_is_6 = @($localRun.local_facts | Where-Object { $_.key -eq "correlation.nonce" }).Count -eq 6
    all_manifest_expected_facts_present = $missingExpectedFacts.Count -eq 0
    tamper_memory_was_released = $null -ne $memoryReleaseFact -and $memoryReleaseFact.value -eq $true
    synthetic_compare_exit_code_is_0 = $comparisonExitCode -eq 0
    synthetic_compare_pass_count_is_6 = $null -ne $validation -and $validation.summary.pass -eq 6
    synthetic_compare_has_no_non_pass = $null -ne $validation -and `
        $validation.summary.partial -eq 0 -and $validation.summary.fail -eq 0 -and `
        $validation.summary.inconclusive -eq 0 -and $validation.summary.not_compared -eq 0
}
$failedAssertions = @($assertions.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
$summary = [ordered]@{
    schema_version = "1.0"
    test_suite = "process-activity-e2e"
    status = if ($failedAssertions.Count -eq 0) { "PASS" } else { "FAIL" }
    tested_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    local_run = $localRunFile.FullName
    database = (Get-ChildItem (Split-Path -Parent (Split-Path -Parent $localRunFile.FullName)) -Filter "*.db" | Select-Object -First 1).FullName
    synthetic_cloud = $syntheticCloudPath
    synthetic_validation_result = $validationPath
    capability_ids = @($localRun.capabilities | ForEach-Object { $_.capability_id })
    event_actions = $actualActions
    counts = [ordered]@{
        capabilities = @($localRun.capabilities).Count
        programs = @($localRun.programs).Count
        local_events = @($localRun.local_events).Count
        local_facts = @($localRun.local_facts).Count
        artifacts = @($localRun.artifacts).Count
        cleanup_results = @($localRun.cleanup_results).Count
    }
    assertions = $assertions
    failed_assertions = $failedAssertions
}
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$summaryPath = Join-Path $OutputRoot "test-summary.json"
$summary | ConvertTo-Json -Depth 20 | Set-Content $summaryPath -Encoding utf8NoBOM

if ($failedAssertions.Count -gt 0) {
    throw "Process Activity 端到端断言失败：$($failedAssertions -join ', ')。结果：$summaryPath"
}

Write-Host "[PASS] Process Activity 六项能力端到端测试通过。"
Write-Host "本地导出：$($localRunFile.FullName)"
Write-Host "测试摘要：$summaryPath"
