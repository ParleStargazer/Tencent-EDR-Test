[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WebRoot,
    [Parameter(Mandatory = $true)]
    [int]$Port
)

$ErrorActionPreference = "Stop"
$pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
if ($null -eq $pnpm) {
    throw "未找到 pnpm。请安装 Node.js 22.13+ 和 pnpm 11.9+。"
}

Set-Location $WebRoot
& $pnpm.Source run start -- --hostname 127.0.0.1 --port $Port
exit $LASTEXITCODE
