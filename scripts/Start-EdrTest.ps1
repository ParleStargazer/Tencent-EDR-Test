[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$ApiPort = 4317,
    [ValidateRange(1024, 65535)]
    [int]$WebPort = 3000,
    [switch]$SkipBuild,
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stateRoot = Join-Path $repositoryRoot ".edr-test"
$logRoot = Join-Path $stateRoot "logs"
$statePath = Join-Path $stateRoot "services.json"
$webRoot = Join-Path $repositoryRoot "web"
$runnerDll = Join-Path $repositoryRoot "src\EdrTest\bin\Release\net8.0-windows\EdrTest.dll"
$apiUrl = "http://127.0.0.1:$ApiPort"
$webUrl = "http://127.0.0.1:$WebPort"

function Test-ProcessAlive([int]$ProcessId) {
    return $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Test-PortAvailable([int]$Port) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
        return $true
    } catch {
        return $false
    } finally {
        $listener.Stop()
    }
}

if (Test-Path $statePath) {
    try {
        $existing = Get-Content $statePath -Raw | ConvertFrom-Json
        if ((Test-ProcessAlive ([int]$existing.backend_pid)) -or (Test-ProcessAlive ([int]$existing.frontend_pid))) {
            Write-Host "平台已经启动：$($existing.web_url)" -ForegroundColor Yellow
            Write-Host "如需重启，请先运行 scripts\Stop-EdrTest.ps1。"
            if (-not $NoBrowser) { Start-Process $existing.web_url }
            exit 0
        }
    } catch {
        Write-Warning "旧状态文件无法读取，将创建新状态：$($_.Exception.Message)"
    }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) { throw "未找到 .NET 8 SDK。" }
$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if ($null -eq $pwsh) { throw "未找到 PowerShell 7（pwsh）。" }
$pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
if ($null -eq $pnpm) { throw "未找到 pnpm。请安装 Node.js 22.13+ 和 pnpm 11.9+。" }
if (-not (Test-PortAvailable $ApiPort)) { throw "API 端口 $ApiPort 已被占用，请先停止占用程序或使用 -ApiPort 指定其他端口。" }
if (-not (Test-PortAvailable $WebPort)) { throw "前端端口 $WebPort 已被占用，请先停止占用程序或使用 -WebPort 指定其他端口。" }

$isAdministrator = $false
try {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    $isAdministrator = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    $identity.Dispose()
} catch {
    Write-Warning "无法确认当前 PowerShell 的管理员权限：$($_.Exception.Message)"
}
if (-not $isAdministrator) {
    Write-Warning "当前平台未以管理员身份运行。建议关闭后使用管理员权限重新运行 scripts\Start-EdrTest.ps1；五项用户账号活动、三项服务活动和组策略修改测试需要管理员权限，否则会被跳过或不可用。"
}

[System.IO.Directory]::CreateDirectory($logRoot) | Out-Null

if (-not $SkipBuild) {
    Write-Host "[1/5] 构建 EdrTest 框架…" -ForegroundColor Cyan
    & $dotnet.Source restore (Join-Path $repositoryRoot "EdrTest.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
    & $dotnet.Source build (Join-Path $repositoryRoot "EdrTest.sln") --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败。" }

    Write-Host "[2/5] 构建 Process、File、Hash、User Account、Network、Registry、Scheduled Task、Service、Group Policy、Named Pipe、PowerShell 与 BITS Activity 能力包…" -ForegroundColor Cyan
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-ProcessActivitySamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-FileManipulationSamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "文件操作能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-HashAlgorithmsSamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "哈希算法能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-UserAccountActivitySamples.ps1") -Configuration Release -SuppressPrivilegeWarning
    if ($LASTEXITCODE -ne 0) { throw "用户账号活动能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-NetworkActivitySamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "网络活动能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-RegistryActivitySamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "注册表活动能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-ScheduledTaskActivitySamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "计划任务活动能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-ServiceActivitySamples.ps1") -Configuration Release -SuppressPrivilegeWarning
    if ($LASTEXITCODE -ne 0) { throw "服务活动能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-GroupPolicyActivitySamples.ps1") -Configuration Release -SuppressPrivilegeWarning
    if ($LASTEXITCODE -ne 0) { throw "组策略修改能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-NamedPipeActivitySamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "命名管道活动能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-PowerShellActivitySamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "PowerShell 活动能力样本构建失败。" }
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot "Build-BitsActivitySamples.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "BITS 活动能力样本构建失败。" }
}

if (-not (Test-Path $runnerDll)) { throw "找不到 Runner：$runnerDll。请移除 -SkipBuild 后重试。" }
if (-not (Test-Path (Join-Path $webRoot "node_modules\.modules.yaml"))) {
    Write-Host "[3/5] 安装前端依赖…" -ForegroundColor Cyan
    Push-Location $webRoot
    try {
        & $pnpm.Source install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "pnpm install 失败。" }
    } finally {
        Pop-Location
    }
} else {
    Write-Host "[3/5] 前端依赖已就绪。" -ForegroundColor DarkGray
}

if (-not $SkipBuild) {
    Write-Host "[4/5] 构建前端控制面…" -ForegroundColor Cyan
    Push-Location $webRoot
    try {
        $env:VITE_EDR_API_URL = "$apiUrl/api"
        & $pnpm.Source run build
        if ($LASTEXITCODE -ne 0) { throw "pnpm build 失败。" }
    } finally {
        Pop-Location
    }
} elseif (-not (Test-Path (Join-Path $webRoot "dist\server\index.js"))) {
    throw "找不到前端构建产物。请移除 -SkipBuild 后重试。"
}

$backendOut = Join-Path $logRoot "backend.out.log"
$backendErr = Join-Path $logRoot "backend.err.log"
$frontendOut = Join-Path $logRoot "frontend.out.log"
$frontendErr = Join-Path $logRoot "frontend.err.log"
$startedProcesses = @()

try {
    Write-Host "[5/5] 启动本地 API 与前端…" -ForegroundColor Cyan
    $backendArguments = @(
        "--roll-forward", "Major", $runnerDll, "serve",
        "--host", "127.0.0.1",
        "--port", $ApiPort,
        "--repo-root", $repositoryRoot,
        "--allowed-origin", $webUrl,
        "--allowed-origin", "http://localhost:$WebPort"
    )
    $backend = Start-Process -FilePath $dotnet.Source -ArgumentList $backendArguments -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden -RedirectStandardOutput $backendOut -RedirectStandardError $backendErr -PassThru
    $startedProcesses += $backend

    $frontend = Start-Process -FilePath $pwsh.Source -ArgumentList @(
        "-NoProfile", "-File", (Join-Path $PSScriptRoot "Run-WebControlPlane.ps1"),
        "-WebRoot", $webRoot, "-Port", $WebPort
    ) -WorkingDirectory $repositoryRoot -WindowStyle Hidden `
        -RedirectStandardOutput $frontendOut -RedirectStandardError $frontendErr -PassThru
    $startedProcesses += $frontend

    [ordered]@{
        schema_version = "1.0"
        repository_root = $repositoryRoot
        backend_pid = $backend.Id
        frontend_pid = $frontend.Id
        api_url = $apiUrl
        web_url = $webUrl
        started_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    } | ConvertTo-Json | Set-Content $statePath -Encoding utf8NoBOM

    $apiReady = $false
    $webReady = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if (-not $apiReady) {
            try {
                $health = Invoke-RestMethod "$apiUrl/api/health" -TimeoutSec 2
                $apiReady = $health.status -eq "ok"
            } catch { }
        }
        if (-not $webReady) {
            try {
                $response = Invoke-WebRequest $webUrl -UseBasicParsing -TimeoutSec 2
                $webReady = $response.StatusCode -eq 200
            } catch { }
        }
        if ($apiReady -and $webReady) { break }
        if ($backend.HasExited) { throw "本地 API 已异常退出，请查看 $backendErr。" }
        if ($frontend.HasExited) { throw "前端已异常退出，请查看 $frontendErr。" }
        Start-Sleep -Milliseconds 250
    }
    if (-not $apiReady) { throw "本地 API 在 30 秒内未通过健康检查，请查看 $backendErr。" }
    if (-not $webReady) { throw "前端在 30 秒内未就绪，请查看 $frontendErr。" }

    Write-Host "平台已启动：$webUrl" -ForegroundColor Green
    Write-Host "本地 API：$apiUrl"
    Write-Host "日志目录：$logRoot"
    if (-not $NoBrowser) { Start-Process $webUrl }
} catch {
    if (Test-Path $statePath) {
        & (Join-Path $PSScriptRoot "Stop-EdrTest.ps1")
    } else {
        foreach ($process in $startedProcesses) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
    }
    throw
}
