[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot "artifacts\bits-activity-e2e" }
& (Join-Path $PSScriptRoot "Build-BitsActivitySamples.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "BITS 能力包构建失败。" }
$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path $runner)) { dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore }
& dotnet --roll-forward Major $runner run --runs-dir (Join-Path $OutputRoot "runs") --suite-id "bits-activity-e2e" --next-delay-seconds 0 `
    --manifest (Join-Path $repositoryRoot "samples\win.bits.job\capability.json")
if ($LASTEXITCODE -ne 0) { throw "BITS Runner 执行失败。" }

$local = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter local-run.json -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$run = Get-Content $local.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($run.run.status -ne "COMPLETED" -or $run.capabilities.Count -ne 1 -or $run.capabilities[0].status -ne "LOCAL_PASS") {
    throw "BITS 本地绝对基准未通过：$($local.FullName)"
}
$events = @($run.local_events | Where-Object event_type -eq "bits")
$actors = @($run.programs | Where-Object role -eq "actor")
$targets = @($run.programs | Where-Object role -eq "target")
if ($events.Count -ne 2 -or $actors.Count -ne 2 -or $targets.Count -ne 2) {
    throw "BITS 双子测试的事件或 Actor/Target 程序数量异常。"
}
$methods = @($events | ForEach-Object {
    if ($_.data.job.display_name -like "EDRTEST_BITSADMIN_*") { "bitsadmin" }
    elseif ($_.data.job.display_name -like "EDRTEST_BITSCOM_*") { "com_api" }
})
if (@($methods | Sort-Object -Unique).Count -ne 2) { throw "BITS 两种方法没有分别生成本地事件。" }
foreach ($event in $events) {
    if (-not $event.data.result.succeeded -or -not $event.data.after.download_verified -or -not $event.data.after.job_removed_after_complete) {
        throw "BITS 本地事件缺少传输、哈希或任务移除证据。"
    }
    if ($event.data.job.bytes_total -ne $event.data.job.bytes_transferred -or $event.data.job.bytes_total -le 0) {
        throw "BITS 本地事件的字节进度不完整。"
    }
}

$cloud = @()
foreach ($event in $events) {
    $initiator = $run.programs | Where-Object program_instance_id -eq $event.actor_program_id | Select-Object -First 1
    if ($null -eq $initiator) { throw "BITS 本地事件缺少发起程序。" }
    $job = $event.data.job
    $cloud += [ordered]@{
        OS = "Windows"
        '@table' = "BitsEvents"
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = "BITS"
        'Action.Name' = "BitsJob"
        'Common.EventUUId' = $event.local_event_id
        'Common.EventTime' = [DateTimeOffset]::Parse($event.occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id
        'Common.Guid' = "synthetic-agent"
        'Common.ClientVer' = "synthetic-1"
        'Environment.HostName' = $run.run.host.hostname
        'Environment.OsVersion' = $run.run.host.os_version
        'Parent.ProcPid' = $initiator.pid
        'Parent.ProcGuid' = $initiator.program_instance_id
        'Parent.FileName' = $initiator.file_name
        'Parent.FilePath' = $initiator.executable
        'Parent.ProcCmdline' = $initiator.command_line
        'Parent.ProcCreateTime' = [DateTimeOffset]::Parse($initiator.started_at_utc).ToUnixTimeMilliseconds()
        'Child.BitsJobId' = $job.job_id
        'Child.BitsJobName' = $job.display_name
        'Child.BitsJobType' = $job.job_type
        'Child.BitsJobState' = $job.state
        'Child.BitsOwnerSid' = $job.owner_sid
        'Child.BitsRemoteUrl' = $job.remote_url
        'Child.BitsLocalPath' = $job.local_path
        'Child.BitsBytesTotal' = $job.bytes_total
        'Child.BitsBytesTransferred' = $job.bytes_transferred
        'Child.BitsNotificationCommand' = ""
    }
}
$cloudPath = Join-Path $OutputRoot "synthetic-cloud.tencent-bits.json"
$cloud | ConvertTo-Json -Depth 20 | Set-Content $cloudPath -Encoding utf8NoBOM
$validationPath = Join-Path $OutputRoot "validation-result.tencent-bits-mapping.json"
& dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $cloudPath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\bits_job.yaml") `
    --out $validationPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
if ($LASTEXITCODE -ne 0) { throw "BITS 腾讯规划映射离线比较失败。" }
$validation = Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($validation.summary.pass -ne 1) { throw "BITS 双方法规划映射没有得到能力 PASS。" }
$methodResults = @($validation.capabilities[0].method_results)
if ($methodResults.Count -ne 2 -or @($methodResults | Where-Object status -ne "PASS").Count -gt 0) {
    throw "BITS bitsadmin 与 COM API 方法没有分别通过规划映射。"
}

$unrelatedCloudPath = Join-Path $OutputRoot "current-product-no-bits-event.json"
@([ordered]@{
    OS = "Windows"
    '@table' = "ProcEvents"
    '@timestamp' = $events[0].observed_at_utc
    'Action.Type' = "Proc"
    'Action.Name' = "ProcessCreate"
    'Common.EventUUId' = "bits-no-direct-event"
    'Common.EventTime' = [DateTimeOffset]::Parse($events[0].occurred_at_utc).ToUnixTimeMilliseconds()
    'Common.Mid' = "synthetic-host"
    'Environment.HostName' = $run.run.host.hostname
    'Parent.ProcPid' = 4
    'Parent.FilePath' = "C:\Windows\System32\System"
    'Child.ProcPid' = 5
    'Child.FilePath' = "C:\Windows\System32\svchost.exe"
}) | ConvertTo-Json -Depth 10 -AsArray | Set-Content $unrelatedCloudPath -Encoding utf8NoBOM
$noMatchPath = Join-Path $OutputRoot "validation-result.current-product-no-bits.json"
& dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $unrelatedCloudPath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\bits_job.yaml") `
    --out $noMatchPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
if ($LASTEXITCODE -ne 0) { throw "BITS 当前产品未实现结论验证失败。" }
$noMatch = Get-Content $noMatchPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($noMatch.summary.pass -ne 0 -or $noMatch.capabilities[0].validation_status -eq "PASS") {
    throw "无 BITS 专属事件时不应得到通过结论。"
}
Write-Host "[PASS] BITS bitsadmin/COM 本地基准、规划映射及当前产品未匹配结论均符合预期：$($local.FullName)"
