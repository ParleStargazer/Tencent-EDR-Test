[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot "artifacts\virtual-disk-activity-e2e" }
& (Join-Path $PSScriptRoot "Build-VirtualDiskActivitySamples.ps1") -Configuration $Configuration -SuppressPrivilegeWarning
if ($LASTEXITCODE -ne 0) { throw "虚拟磁盘能力包构建失败。" }

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $administrator = [System.Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
} finally { $identity.Dispose() }
if (-not $administrator) {
    Write-Warning "[SKIP] 当前不是管理员：已验证虚拟磁盘能力包可构建；实际 VHD 挂载、双端复核、规划映射与严格清理请在管理员 PowerShell 中运行本脚本。"
    return
}

$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path $runner)) { dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore }
& dotnet --roll-forward Major $runner run --runs-dir (Join-Path $OutputRoot "runs") --suite-id "virtual-disk-activity-e2e" --next-delay-seconds 0 `
    --allow-high-risk `
    --manifest (Join-Path $repositoryRoot "samples\win.device.virtual_disk.mount\capability.json")
if ($LASTEXITCODE -ne 0) { throw "虚拟磁盘 Runner 执行失败。" }

$local = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter local-run.json -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$run = Get-Content $local.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
$capability = $run.capabilities | Where-Object capability_id -eq "win.device.virtual_disk.mount" | Select-Object -First 1
$events = @($run.local_events | Where-Object event_type -eq "device" | Where-Object event_action -eq "virtual_disk_mount")
if ($run.run.status -ne "COMPLETED" -or $capability.status -ne "LOCAL_PASS" -or $events.Count -ne 2) {
    throw "虚拟磁盘双方法本地绝对基准未通过：$($local.FullName)"
}
$methods = @($events | ForEach-Object { $_.data.method } | Sort-Object -Unique)
if ($methods.Count -ne 2 -or $methods -notcontains "VDISK_POWERSHELL" -or $methods -notcontains "VDISK_NATIVE_API") {
    throw "虚拟磁盘本地事件缺少两个独立方法。"
}
if (@($events | Where-Object { -not $_.data.result.succeeded -or $_.data.before.attached -or -not $_.data.after.attached }).Count -gt 0) {
    throw "虚拟磁盘事件缺少未挂载 -> 已挂载的本地证据。"
}
$cleanup = @($run.cleanup_results | Where-Object action -like "detach_and_delete_virtual_disk_*")
if ($cleanup.Count -ne 2 -or @($cleanup | Where-Object status -ne "succeeded").Count -gt 0) {
    throw "虚拟磁盘双方法没有完成精确卸载与镜像删除。"
}

$cloud = @()
foreach ($event in $events) {
    $actor = $run.programs | Where-Object program_instance_id -eq $event.actor_program_id | Select-Object -First 1
    if ($null -eq $actor) { throw "虚拟磁盘本地事件缺少发起进程。" }
    $data = $event.data
    $methodPrefix = if ($data.method -eq "VDISK_POWERSHELL") { "virtual_disk.vdisk_powershell" } else { "virtual_disk.vdisk_native_api" }
    $sha256 = ($run.local_facts | Where-Object case_run_id -eq $event.case_run_id | Where-Object key -eq "$methodPrefix.image_sha256" | Select-Object -First 1).value
    $cloud += [ordered]@{
        OS = "Windows"
        '@table' = "VirtualDiskEvents"
        '@timestamp' = $event.observed_at_utc
        'Action.Type' = "Device"
        'Action.Name' = "VirtualDiskMount"
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
        'Child.VirtualDiskInstanceId' = $data.device.instance_id
        'Child.VirtualDiskImagePath' = $data.device.image_path
        'Child.VirtualDiskPhysicalPath' = $data.device.physical_path
        'Child.VirtualDiskImageSha256' = $sha256
        'Child.VirtualDiskSize' = $data.device.virtual_size_bytes
        'Child.VirtualDiskReadOnly' = $data.device.read_only
        'Child.VirtualDiskNoDriveLetter' = $data.device.no_drive_letter
        'Child.VirtualDiskMethod' = $data.method
        'Child.VirtualDiskProvider' = $data.device.provider
    }
}
$cloudPath = Join-Path $OutputRoot "synthetic-cloud.tencent-virtual-disk.json"
$cloud | ConvertTo-Json -Depth 20 | Set-Content $cloudPath -Encoding utf8NoBOM
$validationPath = Join-Path $OutputRoot "validation-result.tencent-virtual-disk.json"
& dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $cloudPath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\device_virtual_disk_mount.yaml") `
    --out $validationPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
if ($LASTEXITCODE -ne 0) { throw "虚拟磁盘腾讯规划映射离线比较失败。" }
$validation = Get-Content $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
$methodResults = @($validation.capabilities[0].method_results)
if ($validation.summary.pass -ne 1 -or $methodResults.Count -ne 2 -or @($methodResults | Where-Object validation_status -ne "PASS").Count -gt 0) {
    throw "虚拟磁盘双方法规划映射没有全部通过。"
}

$unrelatedCloudPath = Join-Path $OutputRoot "current-product-no-virtual-disk-event.json"
@(
    [ordered]@{
        OS = "Windows"; '@table' = "ScriptEvents"; '@timestamp' = $events[0].observed_at_utc
        'Action.Type' = "Script"; 'Action.Name' = "ScriptScan"
        'Common.EventUUId' = "virtual-disk-unrelated-script"; 'Common.EventTime' = [DateTimeOffset]::Parse($events[0].occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id; 'Environment.HostName' = $run.run.host.hostname
        'Parent.ProcPid' = 4; 'Parent.FilePath' = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
        'Child.ContentData' = "Mount-DiskImage"
    },
    [ordered]@{
        OS = "Windows"; '@table' = "FileEvents"; '@timestamp' = $events[0].observed_at_utc
        'Action.Type' = "File"; 'Action.Name' = "FileWriteClose"
        'Common.EventUUId' = "virtual-disk-unrelated-file"; 'Common.EventTime' = [DateTimeOffset]::Parse($events[0].occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id; 'Environment.HostName' = $run.run.host.hostname
        'Parent.ProcPid' = 4; 'Parent.FilePath' = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
        'Child.FilePath' = $events[0].data.device.image_path
    }
) | ConvertTo-Json -Depth 10 -AsArray | Set-Content $unrelatedCloudPath -Encoding utf8NoBOM
$noMatchPath = Join-Path $OutputRoot "validation-result.current-product-no-virtual-disk.json"
& dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $unrelatedCloudPath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\device_virtual_disk_mount.yaml") `
    --out $noMatchPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
if ($LASTEXITCODE -ne 0) { throw "虚拟磁盘当前产品未实现结论验证失败。" }
$noMatch = Get-Content $noMatchPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($noMatch.summary.pass -ne 0 -or @($noMatch.capabilities | Where-Object validation_status -eq "PASS").Count -gt 0) {
    throw "普通 ScriptScan/FileWriteClose 不应使虚拟磁盘挂载能力通过。"
}
Write-Host "[PASS] 虚拟磁盘双方法本地基准、规划映射、严格清理及当前产品未匹配结论均符合预期：$($local.FullName)"
