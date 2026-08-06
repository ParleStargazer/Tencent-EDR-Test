[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$statePath = Join-Path $repositoryRoot ".edr-test\services.json"
if (-not (Test-Path $statePath)) {
    Write-Host "未发现运行状态文件，平台可能尚未启动。" -ForegroundColor Yellow
    exit 0
}

$state = Get-Content $statePath -Raw | ConvertFrom-Json
if (-not [string]::Equals([System.IO.Path]::GetFullPath([string]$state.repository_root), $repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "状态文件所属仓库与当前仓库不一致，拒绝停止进程。"
}

$allProcesses = @(Get-CimInstance Win32_Process)
function Get-ProcessTree([int]$RootId) {
    $result = New-Object System.Collections.Generic.List[int]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($RootId)
    while ($queue.Count -gt 0) {
        $parentId = $queue.Dequeue()
        foreach ($child in $allProcesses | Where-Object { [int]$_.ParentProcessId -eq $parentId }) {
            $queue.Enqueue([int]$child.ProcessId)
            $result.Add([int]$child.ProcessId)
        }
    }
    $result.Add($RootId)
    return @($result)
}

$roots = @([int]$state.backend_pid, [int]$state.frontend_pid)
$validatedRoots = @()
foreach ($rootId in $roots) {
    $process = $allProcesses | Where-Object { [int]$_.ProcessId -eq $rootId } | Select-Object -First 1
    if ($null -eq $process) { continue }
    if ([string]::IsNullOrWhiteSpace([string]$process.CommandLine) -or $process.CommandLine.IndexOf($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Warning "PID $rootId 的命令行不属于当前仓库，已跳过。"
        continue
    }
    $validatedRoots += $rootId
}

$processIds = @($validatedRoots | ForEach-Object { Get-ProcessTree $_ } | Select-Object -Unique)
[array]::Reverse($processIds)
foreach ($processId in $processIds) {
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
}

Remove-Item -LiteralPath $statePath -Force
Write-Host "EDR 验证平台已停止。" -ForegroundColor Green
