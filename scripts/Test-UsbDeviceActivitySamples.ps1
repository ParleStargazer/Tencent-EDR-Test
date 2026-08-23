[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\usb-device-activity-e2e"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$prebuilt = Join-Path $repositoryRoot "drivers\UsbUdeTest\prebuilt\x64"
$publicCertificate = Join-Path $repositoryRoot "drivers\cert\EdrTestDriverTest.cer"

& (Join-Path $PSScriptRoot "Build-UsbDeviceActivitySamples.ps1") `
    -Configuration $Configuration -UsbDriverPackagePath $prebuilt `
    -DriverCertificatePath $publicCertificate -SuppressPrivilegeWarning
if ($LASTEXITCODE -ne 0) { throw "USB Device Activity 能力包构建失败。" }
dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw ".NET 解决方案构建失败。" }
dotnet run --project (Join-Path $repositoryRoot "tests\EdrTest.Tests\EdrTest.Tests.csproj") `
    --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "USB BASELINE/映射框架回归失败。" }
node --test (Join-Path $repositoryRoot "tests\contract\usb-device-activity-contract.test.mjs")
if ($LASTEXITCODE -ne 0) { throw "USB Device Activity 静态契约测试失败。" }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $administrator = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} finally {
    $identity.Dispose()
}

$bcdOutput = & (Join-Path $env:SystemRoot "System32\bcdedit.exe") /enum "{current}" 2>&1
$testSigning = ($LASTEXITCODE -eq 0) -and (($bcdOutput -join "`n") -match '(?im)^\s*testsigning\s+(Yes|On|是|开启)\s*$')
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publicCertificate)
try {
    $trusted = $true
    foreach ($storeName in @("Root", "TrustedPublisher")) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new(
            $storeName, [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        try {
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            if ($store.Certificates.Find(
                    [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                    $certificate.Thumbprint,
                    $false).Count -eq 0) { $trusted = $false }
        } finally {
            $store.Dispose()
        }
    }
} finally {
    $certificate.Dispose()
}

if (-not $administrator -or -not $testSigning -or -not $trusted) {
    Write-Warning "[SKIP] USB UDE 原生运行条件未就绪：管理员=$administrator；testsigning=$testSigning；公开证书双信任区=$trusted。已完成驱动签名、能力包、.NET 与静态契约验证；平台启动时可提示导入证书，但不会自动修改 testsigning。"
    return
}

$runner = Join-Path $repositoryRoot "src\EdrTest\bin\$Configuration\net8.0-windows\EdrTest.dll"
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    dotnet build (Join-Path $repositoryRoot "EdrTest.sln") --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "EdrTest Runner 构建失败。" }
}

& dotnet --roll-forward Major $runner run `
    --runs-dir (Join-Path $OutputRoot "runs") `
    --suite-id "usb-device-activity-e2e" `
    --next-delay-seconds 0 `
    --allow-high-risk `
    --manifest (Join-Path $repositoryRoot "samples\win.device.usb.mount\capability.json") `
    --manifest (Join-Path $repositoryRoot "samples\win.device.usb.unmount\capability.json")
if ($LASTEXITCODE -ne 0) { throw "USB Device Activity Runner 执行失败。" }

$local = Get-ChildItem (Join-Path $OutputRoot "runs") -Filter local-run.json -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $local) { throw "USB 原生测试没有生成 local-run.json。" }
$run = Get-Content -LiteralPath $local.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String
$capabilities = @($run.capabilities | Where-Object capability_id -in @(
    "win.device.usb.mount", "win.device.usb.unmount"))
$events = @($run.local_events | Where-Object event_type -eq "device" |
    Where-Object event_action -in @("usb_mount", "usb_unmount"))
if ($run.run.status -ne "COMPLETED" -or $capabilities.Count -ne 2 `
    -or @($capabilities | Where-Object status -ne "LOCAL_PASS").Count -gt 0) {
    throw "USB 挂载/卸载本地绝对基准未全部通过：$($local.FullName)"
}
if ($events.Count -ne 2) { throw "USB 本地事件应恰好包含挂载和卸载各一条。" }
foreach ($event in $events) {
    if (-not $event.data.result.succeeded) { throw "USB 本地事件结果未通过：$($event.event_action)" }
    if ($event.event_action -eq "usb_mount" -and ($event.data.before.present -or -not $event.data.after.present)) {
        throw "USB 挂载事件缺少不存在 -> 存在的独立 PnP 证据。"
    }
    if ($event.event_action -eq "usb_unmount" -and (-not $event.data.before.present -or $event.data.after.present)) {
        throw "USB 卸载事件缺少存在 -> 不存在的独立 PnP 证据。"
    }
}
$cleanup = @($run.cleanup_results | Where-Object action -eq "detach_usb_remove_ude_root_and_driver_package")
if ($cleanup.Count -ne 2 -or @($cleanup | Where-Object status -ne "succeeded").Count -gt 0) {
    throw "USB UDE 驱动、根设备或模拟 PnP Instance 未完成精确清理。"
}

$cloud = @()
foreach ($event in $events) {
    $caseRunId = $event.case_run_id
    $facts = @{}
    foreach ($fact in @($run.local_facts | Where-Object case_run_id -eq $caseRunId)) {
        $facts[$fact.key] = $fact.value
    }
    $cloud += [ordered]@{
        table = "UsbDeviceActivity"
        action = if ($event.event_action -eq "usb_mount") { "UsbDeviceMount" } else { "UsbDeviceUnmount" }
        event_id = $event.local_event_id
        host_id = $run.run.host.machine_id
        host_name = $run.run.host.hostname
        event_time = $event.occurred_at_utc
        actor_pid = $facts["usb.actor_pid"]
        actor_executable = $facts["usb.actor_executable"]
        actor_command_line = $facts["usb.actor_command_line"]
        instance_id = $facts["usb.instance_id"]
        class_guid = $facts["usb.class_guid"]
        vendor_id = $facts["usb.vendor_id"]
        product_id = $facts["usb.product_id"]
        serial_number = $facts["usb.serial_number"]
        description = $facts["usb.description"]
        manufacturer = $facts["usb.manufacturer"]
        service = $facts["usb.service"]
        driver_key = $facts["usb.driver_key"]
        method = "USB_UDE_PNP"
        provider = "UsbUdeTest/UdeCx"
    }
}
$cloudPath = Join-Path $OutputRoot "synthetic-cloud.generic-usb.json"
$cloud | ConvertTo-Json -Depth 20 -AsArray | Set-Content $cloudPath -Encoding utf8NoBOM
$validationPath = Join-Path $OutputRoot "validation-result.generic-usb.json"
& dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $cloudPath `
    --mapping (Join-Path $repositoryRoot "mappings\generic-usb-device-activity-v1.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\device_usb_mount.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\device_usb_unmount.yaml") `
    --out $validationPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
if ($LASTEXITCODE -ne 0) { throw "USB 规划直接映射离线比较失败。" }
$validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($validation.summary.pass -ne 2) { throw "USB 规划直接映射没有使两项能力通过。" }

$sideEvidencePath = Join-Path $OutputRoot "current-product-driver-side-evidence-only.json"
@(
    [ordered]@{
        OS = "Windows"; '@table' = "ModuleEvents"; '@timestamp' = $events[0].observed_at_utc
        'Action.Type' = "Module"; 'Action.Name' = "LoadDriver"; 'Common.Source' = "KernelMon"
        'Common.EventUUId' = "usb-side-load-driver"
        'Common.EventTime' = [DateTimeOffset]::Parse($events[0].occurred_at_utc).ToUnixTimeMilliseconds()
        'Common.Mid' = $run.run.host.machine_id; 'Environment.HostName' = $run.run.host.hostname
        'Child.FileName' = "UsbUdeTest.sys"; 'Child.FilePath' = "C:\Windows\System32\drivers\UsbUdeTest.sys"
    }
) | ConvertTo-Json -Depth 10 -AsArray | Set-Content $sideEvidencePath -Encoding utf8NoBOM
$noMatchPath = Join-Path $OutputRoot "validation-result.current-product-no-usb-direct.json"
& dotnet --roll-forward Major $runner compare --local $local.FullName --cloud $sideEvidencePath `
    --mapping (Join-Path $repositoryRoot "mappings\tencent-edr-proc-events-v1.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\device_usb_mount.yaml") `
    --baseline (Join-Path $repositoryRoot "baselines\windows\device_usb_unmount.yaml") `
    --out $noMatchPath --strong-correlation-time-ms 15 --candidate-time-limit-ms 1000
if ($LASTEXITCODE -ne 0) { throw "USB 当前产品未实现结论验证失败。" }
$noMatch = Get-Content -LiteralPath $noMatchPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String
if ($noMatch.summary.pass -ne 0 -or @($noMatch.capabilities | Where-Object validation_status -eq "PASS").Count -gt 0) {
    throw "LoadDriver 等侧面证据不能使 USB 挂载或卸载能力通过。"
}

Write-Host "[PASS] USB 挂载/卸载本地基准、UDE PnP 行为、规划映射、严格清理和腾讯未实现结论均符合预期：$($local.FullName)"
