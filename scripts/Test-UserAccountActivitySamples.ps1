[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\user-account-activity-e2e"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$samplesRoot = Join-Path $repositoryRoot "samples"
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "五项用户账号活动端到端测试会创建并删除临时本地账号，必须在管理员 PowerShell 中运行。"
    }
} finally {
    $identity.Dispose()
}

& (Join-Path $PSScriptRoot "Build-UserAccountActivitySamples.ps1") `
    -Configuration $Configuration -SamplesRoot $samplesRoot
if ($LASTEXITCODE -ne 0) { throw "用户账号活动能力包构建失败。" }

$capabilityIds = @(
    "win.account.local.create",
    "win.account.local.modify",
    "win.account.local.delete",
    "win.account.login",
    "win.account.logoff"
)
$runnerArguments = @(
    "run", "--runs-dir", (Join-Path $OutputRoot "runs"), "--suite-id", "user-account-activity-e2e",
    "--next-delay-seconds", "1"
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

$eventIds = @{
    local_create = 4720
    local_modify = 4738
    local_delete = 4726
    login = 4624
    logoff = 4634
}
$syntheticGeneric = @()
$syntheticTencent = @()
foreach ($event in $localRun.local_events) {
    $actor = $event.data.actor
    $account = $event.data.account
    $session = $event.data.session
    $controller = $localRun.programs | Where-Object {
        $_.case_run_id -eq $event.case_run_id -and $_.role -eq "controller"
    } | Select-Object -First 1
    $eventLogId = $eventIds[$event.event_action]
    $syntheticGeneric += [ordered]@{
        table = "UserAccountActivity"
        event_id = $event.local_event_id
        host_id = $localRun.run.host.hostname
        host_name = $localRun.run.host.hostname
        event_time = $event.occurred_at_utc
        action = $event.event_action
        actor_pid = $actor.pid
        actor_name = [System.IO.Path]::GetFileName($actor.executable)
        actor_executable = $actor.executable
        actor_command_line = $actor.command_line
        subject_user_name = $controller.user_name
        subject_domain_name = $controller.user_domain
        subject_user_sid = $controller.user_sid
        target_user_name = $account.name
        target_domain_name = $account.domain
        target_user_sid = $account.sid
        event_log_id = $eventLogId
        logon_id = $session.logon_id
        logon_type = $session.logon_type
        authentication_package = $session.authentication_package
        source_address = $session.source_address
    }
    $tencent = [ordered]@{
        OS = "Windows"
        '@table' = if ($event.event_action -in @("login", "logoff")) { "LoginEvents" } else { "AccountEvents" }
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = "WinEventLog"
        'Action.Name' = switch ($event.event_action) {
            "local_create" { "UserLocalCreate" }
            "local_modify" { "UserLocalModify" }
            "local_delete" { "UserLocalDelete" }
            "login" { "LoginSuccess" }
            "logoff" { "Logoff" }
        }
        'Action.EventLogId' = $eventLogId
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $localRun.run.host.hostname
        'Environment.HostName' = $localRun.run.host.hostname
        'Parent.ProcPid' = $actor.pid
        'Parent.FileName' = [System.IO.Path]::GetFileName($actor.executable)
        'Parent.FilePath' = $actor.executable
        'Parent.ProcCmdline' = $actor.command_line
        'Child.SubjectUserName' = $controller.user_name
        'Child.SubjectDomainName' = $controller.user_domain
        'Child.SubjectUserSid' = $controller.user_sid
        'Child.TargetUserName' = $account.name
        'Child.TargetDomainName' = $account.domain
        'Child.TargetLogonId' = $session.logon_id
        'Child.LogonType' = if ($null -ne $session) { "网络" } else { $null }
        'Child.AuthenticationPackageName' = $session.authentication_package
    }
    if ($event.event_action -in @("login", "logoff")) {
        $tencent['Child.TargetUserSid'] = $account.sid
    } else {
        $tencent['Child.TargetSid'] = $account.sid
    }
    $syntheticTencent += $tencent
}

$baselineNames = @(
    "account_local_create.yaml",
    "account_local_modify.yaml",
    "account_local_delete.yaml",
    "account_login.yaml",
    "account_logoff.yaml"
)

$genericCloudPath = Join-Path $OutputRoot "synthetic-cloud.user-account.json"
$syntheticGeneric | ConvertTo-Json -Depth 20 | Set-Content $genericCloudPath -Encoding utf8NoBOM
$genericValidationPath = Join-Path $OutputRoot "validation-result.generic.json"
$genericArguments = @(
    "compare", "--local", $localRunFile.FullName, "--cloud", $genericCloudPath,
    "--mapping", (Join-Path $repositoryRoot "mappings\generic-user-account-activity-v1.yaml"),
    "--out", $genericValidationPath
)
foreach ($baselineName in $baselineNames) {
    $genericArguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$baselineName"))
}
& dotnet --roll-forward Major $runner @genericArguments
$genericExitCode = $LASTEXITCODE
$genericValidation = if (Test-Path $genericValidationPath) {
    Get-Content $genericValidationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
} else { $null }

$tencentCloudPath = Join-Path $OutputRoot "synthetic-cloud.tencent-account-events.json"
$syntheticTencent | ConvertTo-Json -Depth 20 | Set-Content $tencentCloudPath -Encoding utf8NoBOM
$tencentValidationPath = Join-Path $OutputRoot "validation-result.tencent.json"
$tencentArguments = @(
    "compare", "--local", $localRunFile.FullName, "--cloud", $tencentCloudPath,
    "--mapping", (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml"),
    "--out", $tencentValidationPath
)
foreach ($baselineName in $baselineNames) {
    $tencentArguments += @("--baseline", (Join-Path $repositoryRoot "baselines\windows\$baselineName"))
}
& dotnet --roll-forward Major $runner @tencentArguments
$tencentExitCode = $LASTEXITCODE
$tencentValidation = if (Test-Path $tencentValidationPath) {
    Get-Content $tencentValidationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
} else { $null }

$assertions = [ordered]@{
    runner_exit_code_is_0 = $runnerExitCode -eq 0
    run_completed = $localRun.run.status -eq "COMPLETED"
    capability_count_is_5 = @($localRun.capabilities).Count -eq 5
    all_capabilities_local_pass = $failedCapabilities.Count -eq 0
    controller_count_is_5 = @($localRun.programs | Where-Object { $_.role -eq "controller" }).Count -eq 5
    actor_count_is_5 = @($localRun.programs | Where-Object { $_.role -eq "actor" }).Count -eq 5
    event_count_is_5 = @($localRun.local_events).Count -eq 5
    all_events_are_account = @($localRun.local_events | Where-Object { $_.event_type -ne "account" }).Count -eq 0
    all_events_high_confidence = @($localRun.local_events | Where-Object { $_.confidence -ne "high" }).Count -eq 0
    cleanup_count_is_5 = @($localRun.cleanup_results).Count -eq 5
    all_cleanup_succeeded = $failedCleanup.Count -eq 0
    no_controlled_account_remains = @($localRun.cleanup_results | Where-Object { $_.after.account_exists }).Count -eq 0
    all_manifest_expected_facts_present = $missingExpectedFacts.Count -eq 0
    generic_compare_exit_code_is_0 = $genericExitCode -eq 0
    generic_compare_pass_count_is_5 = $null -ne $genericValidation -and $genericValidation.summary.pass -eq 5
    tencent_compare_exit_code_is_0 = $tencentExitCode -eq 0
    tencent_compare_pass_count_is_5 = $null -ne $tencentValidation -and $tencentValidation.summary.pass -eq 5
}
$failedAssertions = @($assertions.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
$summary = [ordered]@{
    schema_version = "1.0"
    test_suite = "user-account-activity-e2e"
    status = if ($failedAssertions.Count -eq 0) { "PASS" } else { "FAIL" }
    tested_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    local_run = $localRunFile.FullName
    capability_ids = @($localRun.capabilities | ForEach-Object { $_.capability_id })
    assertions = $assertions
    failed_assertions = $failedAssertions
}
$summaryPath = Join-Path $OutputRoot "test-summary.json"
$summary | ConvertTo-Json -Depth 20 | Set-Content $summaryPath -Encoding utf8NoBOM

if ($failedAssertions.Count -gt 0) {
    throw "User Account Activity 端到端断言失败：$($failedAssertions -join ', ')。结果：$summaryPath"
}

Write-Host "[PASS] User Account Activity 五项能力端到端测试通过。"
Write-Host "本地导出：$($localRunFile.FullName)"
Write-Host "测试摘要：$summaryPath"
