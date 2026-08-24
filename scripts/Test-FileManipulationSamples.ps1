[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\file-manipulation-e2e"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$samplesRoot = Join-Path $repositoryRoot "samples"
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

& (Join-Path $PSScriptRoot "Build-FileManipulationSamples.ps1") `
    -Configuration $Configuration -SamplesRoot $samplesRoot
if ($LASTEXITCODE -ne 0) { throw "文件操作能力包构建失败。" }

$capabilityIds = @(
    "win.file.create",
    "win.file.open",
    "win.file.delete",
    "win.file.modify",
    "win.file.rename"
)
$runnerArguments = @(
    "run", "--runs-dir", (Join-Path $OutputRoot "runs"), "--suite-id", "file-manipulation-e2e",
    "--next-delay-seconds", "0"
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

$localRun = Get-Content $localRunFile.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
$expectedActions = @("create", "delete", "modify", "open", "rename")
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
$artifactIds = @($localRun.artifacts | ForEach-Object { $_.artifact_id })
$invalidEvidenceRefs = @($localRun.local_events | ForEach-Object {
    foreach ($evidenceRef in $_.evidence_refs) {
        if ($evidenceRef -notin $artifactIds) { $evidenceRef }
    }
})

$syntheticCloud = @()
$syntheticTencent = @()
foreach ($capability in $localRun.capabilities) {
    $selectedSubtest = if ($capability.capability_id -eq "win.file.delete") { "dll_disposition_5s" } else { "json" }
    $event = $localRun.local_events | Where-Object { $_.case_run_id -eq $capability.case_run_id -and $_.data.subtest -eq $selectedSubtest } | Select-Object -First 1
    $actor = $event.data.actor
    $actorProgram = $localRun.programs | Where-Object { $_.program_instance_id -eq $event.actor_program_id } | Select-Object -First 1
    $facts = @{}
    $localRun.local_facts | Where-Object { $_.case_run_id -eq $capability.case_run_id } | ForEach-Object {
        $facts[$_.key] = $_.value
    }
    $subtestPrefix = "file.$selectedSubtest"
    $filePath = if ($event.event_action -eq "rename") { $event.data.destination_path } else { $event.data.path }
    $oldPath = if ($event.event_action -eq "rename") { $event.data.source_path } else { $null }
    $size = switch ($event.event_action) {
        "delete" { $event.data.before.size_bytes }
        default { $event.data.after.size_bytes }
    }
    $md5 = switch ($event.event_action) {
        "delete" { $event.data.before.hashes.md5 }
        default { $event.data.after.hashes.md5 }
    }
    $tencentOperationName = switch ($event.event_action) {
        "create" { "新建文件" }
        "open" { "打开文件" }
        "modify" { "覆盖写文件" }
        default { $null }
    }
    $canonicalOperationName = switch ($event.event_action) {
        "create" { "create_new" }
        "open" { "open_existing" }
        "modify" { "overwrite_existing" }
        default { $null }
    }
    $syntheticCloud += [ordered]@{
        table = "FileManipulation"
        event_id = $event.local_event_id
        host_id = $localRun.run.host.hostname
        event_time = $event.occurred_at_utc
        action = $event.event_action
        actor_pid = $actor.pid
        actor_entity_id = $actor.program_instance_id
        actor_name = [System.IO.Path]::GetFileName($actor.executable)
        actor_executable = $actor.executable
        actor_command_line = $actor.command_line
        user_name = "EDRTEST-USER"
        user_domain = "EDRTEST"
        file_path = $filePath
        old_file_path = $oldPath
        file_name = [System.IO.Path]::GetFileName($filePath)
        file_size = $size
        file_md5 = $md5
        file_sha256 = if ($event.event_action -eq "create") { $facts["$subtestPrefix.after.sha256"] } else { $null }
        operation_name = $canonicalOperationName
        read_bytes = $facts["$subtestPrefix.open.bytes_read"]
        write_bytes = if ($event.event_action -eq "modify") { $facts["$subtestPrefix.modify.bytes_written"] } else { $facts["$subtestPrefix.open.bytes_written"] }
    }
    $actionName = if ($event.event_action -in @("create", "open", "modify")) {
        "FileWriteClose"
    } elseif ($event.event_action -eq "delete") {
        "FileDelete"
    } else {
        "MoveFileExW"
    }
    $syntheticTencent += [ordered]@{
        OS = "Windows"
        '@table' = if ($event.event_action -eq "rename") { "DirOperationEvents" } else { "FileEvents" }
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = if ($event.event_action -eq "rename") { "InjectHook" } else { "File" }
        'Action.Name' = $actionName
        'Child.FileCreateOpName' = $tencentOperationName
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $localRun.run.host.hostname
        'Common.Guid' = "edrtest-agent"
        'Common.ClientVer' = "fixture"
        'Environment.HostName' = $localRun.run.host.hostname
        'Environment.OsVersion' = $localRun.run.host.os_version
        'Parent.ProcPid' = $actor.pid
        'Parent.ProcGuid' = $actor.program_instance_id
        'Parent.FileName' = [System.IO.Path]::GetFileName($actor.executable)
        'Parent.FilePath' = $actor.executable
        'Parent.FileMd5' = $actorProgram.md5
        'Parent.ProcCmdline' = $actor.command_line
        'Parent.ProcCreateTime' = [DateTimeOffset]::Parse($actor.started_at_utc).ToUnixTimeMilliseconds()
        'Parent.ProcUserName' = "EDRTEST-USER"
        'Parent.ProcDomainName' = "EDRTEST"
        'Child.FilePath' = $filePath
        'Child.OldFilePath' = $oldPath
        'Child.FileName' = [System.IO.Path]::GetFileName($filePath)
        'Child.FileSize' = $size
        'Child.FileMd5' = $md5
        'Child.FileTotalRead' = $facts["$subtestPrefix.open.bytes_read"]
        'Child.FileTotalWrite' = if ($event.event_action -eq "modify") { $facts["$subtestPrefix.modify.bytes_written"] } else { $facts["$subtestPrefix.open.bytes_written"] }
    }
}
$syntheticCloudPath = Join-Path $OutputRoot "synthetic-cloud.file-manipulation.json"
$syntheticCloud | ConvertTo-Json -Depth 20 | Set-Content $syntheticCloudPath -Encoding utf8NoBOM
$validationPath = Join-Path $OutputRoot "validation-result.synthetic.json"
$comparisonArguments = @(
    "compare", "--local", $localRunFile.FullName,
    "--cloud", $syntheticCloudPath,
    "--mapping", (Join-Path $repositoryRoot "mappings\generic-file-manipulation-v1.yaml"),
    "--out", $validationPath
)
foreach ($baselineName in @("file_create.yaml", "file_open.yaml", "file_delete.yaml", "file_modify.yaml", "file_rename.yaml")) {
    $comparisonArguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$baselineName"))
}
& dotnet --roll-forward Major $runner @comparisonArguments
$comparisonExitCode = $LASTEXITCODE
$validation = if (Test-Path $validationPath) {
    Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
} else {
    $null
}
$syntheticTencentPath = Join-Path $OutputRoot "synthetic-cloud.tencent-file-events.json"
$syntheticTencent | ConvertTo-Json -Depth 20 | Set-Content $syntheticTencentPath -Encoding utf8NoBOM
$tencentValidationPath = Join-Path $OutputRoot "validation-result.tencent-mapping.json"
$tencentComparisonArguments = @(
    "compare", "--local", $localRunFile.FullName,
    "--cloud", $syntheticTencentPath,
    "--mapping", (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml"),
    "--out", $tencentValidationPath
)
foreach ($baselineName in @("file_create.yaml", "file_open.yaml", "file_delete.yaml", "file_modify.yaml", "file_rename.yaml")) {
    $tencentComparisonArguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$baselineName"))
}
& dotnet --roll-forward Major $runner @tencentComparisonArguments
$tencentComparisonExitCode = $LASTEXITCODE
$tencentValidation = if (Test-Path $tencentValidationPath) {
    Get-Content $tencentValidationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
} else {
    $null
}

$assertions = [ordered]@{
    runner_exit_code_is_0 = $runnerExitCode -eq 0
    run_completed = $localRun.run.status -eq "COMPLETED"
    capability_count_is_5 = @($localRun.capabilities).Count -eq 5
    all_capabilities_local_pass = $failedCapabilities.Count -eq 0
    program_count_is_19 = @($localRun.programs).Count -eq 19
    controller_count_is_5 = @($localRun.programs | Where-Object { $_.role -eq "controller" }).Count -eq 5
    actor_count_is_14 = @($localRun.programs | Where-Object { $_.role -eq "actor" }).Count -eq 14
    event_count_is_14 = @($localRun.local_events).Count -eq 14
    txt_subtest_count_is_5 = @($localRun.local_events | Where-Object { $_.data.subtest -eq "txt" -and $_.data.file_extension -eq ".txt" }).Count -eq 5
    json_subtest_count_is_5 = @($localRun.local_events | Where-Object { $_.data.subtest -eq "json" -and $_.data.file_extension -eq ".json" }).Count -eq 5
    delayed_json_delete_subtest_count_is_1 = @($localRun.local_events | Where-Object {
        $_.event_action -eq "delete" -and $_.data.subtest -eq "json_delayed_5s" -and $_.data.file_extension -eq ".json"
    }).Count -eq 1
    delayed_json_delete_wait_is_at_least_5s = @($localRun.local_events | Where-Object {
        $_.event_action -eq "delete" -and $_.data.subtest -eq "json_delayed_5s" `
            -and [long]$_.data.settle.requested_delay_ms -eq 5000 `
            -and [long]$_.data.settle.elapsed_ms -ge 5000 `
            -and [long]$_.data.settle.landed_to_delete_ms -ge 5000
    }).Count -eq 1
    dll_dotnet_delete_subtest_count_is_1 = @($localRun.local_events | Where-Object {
        $_.event_action -eq "delete" -and $_.data.subtest -eq "dll_dotnet_delete_5s" `
            -and $_.data.file_extension -eq ".dll" `
            -and $_.data.delete.method -eq "dotnet_file_delete"
    }).Count -eq 1
    json_disposition_delete_subtest_count_is_1 = @($localRun.local_events | Where-Object {
        $_.event_action -eq "delete" -and $_.data.subtest -eq "json_disposition_5s" `
            -and $_.data.file_extension -eq ".json" `
            -and $_.data.delete.method -eq "file_disposition_info"
    }).Count -eq 1
    dll_disposition_delete_subtest_count_is_1 = @($localRun.local_events | Where-Object {
        $_.event_action -eq "delete" -and $_.data.subtest -eq "dll_disposition_5s" `
            -and $_.data.file_extension -eq ".dll" `
            -and $_.data.delete.method -eq "file_disposition_info"
    }).Count -eq 1
    dll_disposition_delete_wait_is_at_least_5s = @($localRun.local_events | Where-Object {
        $_.event_action -eq "delete" -and $_.data.subtest -eq "dll_disposition_5s" `
            -and [long]$_.data.settle.requested_delay_ms -eq 5000 `
            -and [long]$_.data.settle.elapsed_ms -ge 5000 `
            -and [long]$_.data.settle.landed_to_delete_ms -ge 5000
    }).Count -eq 1
    all_three_new_delete_methods_wait_at_least_5s = @($localRun.local_events | Where-Object {
        $_.event_action -eq "delete" `
            -and $_.data.subtest -in @("dll_dotnet_delete_5s", "json_disposition_5s", "dll_disposition_5s") `
            -and [long]$_.data.settle.requested_delay_ms -eq 5000 `
            -and [long]$_.data.settle.elapsed_ms -ge 5000 `
            -and [long]$_.data.settle.landed_to_delete_ms -ge 5000
    }).Count -eq 3
    event_actions_complete = (Compare-Object $expectedActions $actualActions).Count -eq 0
    all_events_high_confidence = @($localRun.local_events | Where-Object { $_.confidence -ne "high" }).Count -eq 0
    cleanup_count_is_14 = @($localRun.cleanup_results).Count -eq 14
    all_cleanup_succeeded = $failedCleanup.Count -eq 0
    evidence_artifact_count_is_14 = @($localRun.artifacts).Count -eq 14
    all_event_evidence_refs_resolve = $invalidEvidenceRefs.Count -eq 0
    nonce_fact_count_is_5 = @($localRun.local_facts | Where-Object { $_.key -eq "correlation.nonce" }).Count -eq 5
    occurred_time_fact_count_is_14 = @($localRun.local_facts | Where-Object { $_.key -match '^file\.(txt|json|json_delayed_5s|dll_dotnet_delete_5s|json_disposition_5s|dll_disposition_5s)\.occurred_at_utc$' }).Count -eq 14
    all_manifest_expected_facts_present = $missingExpectedFacts.Count -eq 0
    synthetic_compare_exit_code_is_0 = $comparisonExitCode -eq 0
    synthetic_compare_pass_count_is_5 = $null -ne $validation -and $validation.summary.pass -eq 5
    synthetic_compare_has_no_non_pass = $null -ne $validation -and `
        $validation.summary.partial -eq 0 -and $validation.summary.fail -eq 0 -and `
        $validation.summary.inconclusive -eq 0 -and $validation.summary.not_compared -eq 0
    tencent_mapping_compare_exit_code_is_0 = $tencentComparisonExitCode -eq 0
    tencent_mapping_compare_pass_count_is_5 = $null -ne $tencentValidation -and $tencentValidation.summary.pass -eq 5
    tencent_mapping_compare_has_no_non_pass = $null -ne $tencentValidation -and `
        $tencentValidation.summary.partial -eq 0 -and $tencentValidation.summary.fail -eq 0 -and `
        $tencentValidation.summary.inconclusive -eq 0 -and $tencentValidation.summary.not_compared -eq 0
}
$failedAssertions = @($assertions.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
$summary = [ordered]@{
    schema_version = "1.0"
    test_suite = "file-manipulation-e2e"
    status = if ($failedAssertions.Count -eq 0) { "PASS" } else { "FAIL" }
    tested_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    local_run = $localRunFile.FullName
    database = (Get-ChildItem (Split-Path -Parent (Split-Path -Parent $localRunFile.FullName)) -Filter "*.db" | Select-Object -First 1).FullName
    synthetic_cloud = $syntheticCloudPath
    synthetic_validation_result = $validationPath
    synthetic_tencent_cloud = $syntheticTencentPath
    tencent_mapping_validation_result = $tencentValidationPath
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
    throw "File Manipulation 端到端断言失败：$($failedAssertions -join ', ')。结果：$summaryPath"
}

Write-Host "[PASS] File Manipulation 五项能力端到端测试通过。"
Write-Host "本地导出：$($localRunFile.FullName)"
Write-Host "测试摘要：$summaryPath"
