[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\hash-algorithms-e2e"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$samplesRoot = Join-Path $repositoryRoot "samples"
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

& (Join-Path $PSScriptRoot "Build-HashAlgorithmsSamples.ps1") `
    -Configuration $Configuration -SamplesRoot $samplesRoot
if ($LASTEXITCODE -ne 0) { throw "哈希算法能力包构建失败。" }

$capabilityIds = @("win.hash.md5", "win.hash.sha", "win.hash.imphash")
$runnerArguments = @(
    "run", "--runs-dir", (Join-Path $OutputRoot "runs"), "--suite-id", "hash-algorithms-e2e",
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

$missingExpectedFacts = @()
foreach ($capability in $localRun.capabilities) {
    $manifest = Get-Content (Join-Path $samplesRoot "$($capability.capability_id)\capability.json") -Raw |
        ConvertFrom-Json -Depth 30
    $actualKeys = @($localRun.local_facts |
        Where-Object { $_.case_run_id -eq $capability.case_run_id } |
        ForEach-Object { $_.key })
    foreach ($expectedKey in $manifest.expected_fact_keys) {
        if ($expectedKey -notin $actualKeys) { $missingExpectedFacts += "$($capability.capability_id):$expectedKey" }
    }
}

$syntheticGeneric = @()
$syntheticTencent = @()
foreach ($capability in $localRun.capabilities) {
    $event = $localRun.local_events | Where-Object { $_.case_run_id -eq $capability.case_run_id } | Select-Object -First 1
    $actor = $event.data.actor
    $facts = @{}
    $localRun.local_facts | Where-Object { $_.case_run_id -eq $capability.case_run_id } | ForEach-Object {
        $facts[$_.key] = $_.value
    }
    $format = if ($capability.capability_id -eq "win.hash.imphash") { "PE32+ executable" } else { "JSON" }
    $syntheticGeneric += [ordered]@{
        table = "HashAlgorithms"
        event_id = $event.local_event_id
        host_id = $localRun.run.host.machine_id
        host_name = $localRun.run.host.hostname
        event_time = $event.occurred_at_utc
        actor_pid = $facts["hash.actor_pid"]
        actor_name = [System.IO.Path]::GetFileName($facts["hash.actor_executable"])
        actor_executable = $facts["hash.actor_executable"]
        actor_command_line = $facts["hash.actor_command_line"]
        file_path = $facts["hash.path"]
        file_name = [System.IO.Path]::GetFileName($facts["hash.path"])
        file_size = $facts["hash.file_size_bytes"]
        file_format = $format
        file_md5 = $facts["hash.md5"]
        file_sha1 = $facts["hash.sha1"]
        file_sha256 = $facts["hash.sha256"]
        file_sha512 = $facts["hash.sha512"]
        file_imphash = $facts["hash.imphash"]
        file_sha_type = if ($capability.capability_id -eq "win.hash.sha") { 3 } else { $null }
    }
    $syntheticTencent += [ordered]@{
        OS = "Windows"
        '@table' = "FileEvents"
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = "File"
        'Action.Name' = "FileWriteClose"
        'Child.FileCreateOpName' = "新建文件"
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $localRun.run.host.machine_id
        'Environment.HostName' = $localRun.run.host.hostname
        'Parent.ProcPid' = $facts["hash.actor_pid"]
        'Parent.FileName' = [System.IO.Path]::GetFileName($facts["hash.actor_executable"])
        'Parent.FilePath' = $facts["hash.actor_executable"]
        'Parent.ProcCmdline' = $facts["hash.actor_command_line"]
        'Child.FilePath' = $facts["hash.path"]
        'Child.FileName' = [System.IO.Path]::GetFileName($facts["hash.path"])
        'Child.FileSize' = $facts["hash.file_size_bytes"]
        'Child.FileFormat' = $format
        'Child.FileMd5' = $facts["hash.md5"]
        'Child.FileSha' = $facts["hash.sha256"]
        'Child.FileShaType' = if ($capability.capability_id -eq "win.hash.sha") { 3 } else { $null }
        'Child.FileImpHash' = $facts["hash.imphash"]
    }
}

$genericCloudPath = Join-Path $OutputRoot "synthetic-cloud.hash-algorithms.json"
$tencentCloudPath = Join-Path $OutputRoot "synthetic-cloud.tencent-hash-events.json"
$syntheticGeneric | ConvertTo-Json -Depth 20 | Set-Content $genericCloudPath -Encoding utf8NoBOM
$syntheticTencent | ConvertTo-Json -Depth 20 | Set-Content $tencentCloudPath -Encoding utf8NoBOM
$baselineNames = @("hash_md5.yaml", "hash_sha.yaml", "hash_imphash.yaml")

function Invoke-HashComparison([string]$CloudPath, [string]$MappingName, [string]$OutputName) {
    $validationPath = Join-Path $OutputRoot $OutputName
    $arguments = @(
        "compare", "--local", $localRunFile.FullName,
        "--cloud", $CloudPath,
        "--mapping", (Join-Path $repositoryRoot "mappings\$MappingName"),
        "--out", $validationPath
    )
    foreach ($baselineName in $baselineNames) {
        $arguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$baselineName"))
    }
    & dotnet --roll-forward Major $runner @arguments
    if ($LASTEXITCODE -ne 0) { throw "离线比较失败：$MappingName" }
    return Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
}

$genericValidation = Invoke-HashComparison $genericCloudPath "generic-hash-algorithms-v1.yaml" "validation-result.synthetic.json"
$tencentValidation = Invoke-HashComparison $tencentCloudPath "tencent-edr-proc-events-v1.yaml" "validation-result.tencent-mapping.json"
$extensions = @{}
foreach ($event in $localRun.local_events) {
    $capabilityId = ($localRun.capabilities | Where-Object { $_.case_run_id -eq $event.case_run_id }).capability_id
    $extensions[$capabilityId] = [System.IO.Path]::GetExtension($event.data.file_path).ToLowerInvariant()
}

$assertions = [ordered]@{
    run_completed = $localRun.run.status -eq "COMPLETED"
    capability_count_is_3 = @($localRun.capabilities).Count -eq 3
    all_capabilities_local_pass = @($localRun.capabilities | Where-Object { $_.status -ne "LOCAL_PASS" }).Count -eq 0
    program_count_is_6 = @($localRun.programs).Count -eq 6
    event_count_is_3 = @($localRun.local_events).Count -eq 3
    cleanup_count_is_3 = @($localRun.cleanup_results).Count -eq 3
    all_cleanup_succeeded = @($localRun.cleanup_results | Where-Object { $_.status -ne "succeeded" }).Count -eq 0
    md5_uses_json = $extensions["win.hash.md5"] -eq ".json"
    sha_uses_json = $extensions["win.hash.sha"] -eq ".json"
    imphash_uses_exe = $extensions["win.hash.imphash"] -eq ".exe"
    imphash_is_real_pe = @($localRun.local_events | Where-Object {
        $_.event_action -eq "imphash" -and $_.data.is_portable_executable -eq $true -and $_.data.import_count -gt 0 -and $_.data.hashes.imphash.Length -eq 32
    }).Count -eq 1
    all_manifest_expected_facts_present = $missingExpectedFacts.Count -eq 0
    generic_compare_pass_count_is_3 = $genericValidation.summary.pass -eq 3
    tencent_compare_pass_count_is_3 = $tencentValidation.summary.pass -eq 3
}
$failedAssertions = @($assertions.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
$summaryPath = Join-Path $OutputRoot "test-summary.json"
[ordered]@{
    schema_version = "1.0"
    test_suite = "hash-algorithms-e2e"
    status = if ($failedAssertions.Count -eq 0) { "PASS" } else { "FAIL" }
    tested_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    local_run = $localRunFile.FullName
    capability_ids = @($localRun.capabilities | ForEach-Object { $_.capability_id })
    assertions = $assertions
    failed_assertions = $failedAssertions
} | ConvertTo-Json -Depth 20 | Set-Content $summaryPath -Encoding utf8NoBOM

if ($failedAssertions.Count -gt 0) {
    throw "Hash Algorithms 端到端断言失败：$($failedAssertions -join ', ')。结果：$summaryPath"
}
Write-Host "[PASS] Hash Algorithms 三项能力端到端测试通过。"
Write-Host "本地导出：$($localRunFile.FullName)"
Write-Host "测试摘要：$summaryPath"
