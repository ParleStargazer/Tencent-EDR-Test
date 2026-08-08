[CmdletBinding()]
param(
    [string]$InputPath,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($InputPath)) {
    $InputPath = Join-Path $repositoryRoot "reference\260808210300run\logs_export_1786195398517289193_3b5698bd-0e31-4a91-bd83-b4f90280fdef.json"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot "docs\reference\tencent-edr-260808-field-catalog.json"
}
$InputPath = [System.IO.Path]::GetFullPath($InputPath)
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { throw "找不到 EDR 导出文件：$InputPath" }

function Get-JsonType($Value) {
    if ($null -eq $Value) { return "null" }
    if ($Value -is [string] -or $Value -is [char]) { return "string" }
    if ($Value -is [bool]) { return "boolean" }
    if ($Value -is [byte] -or $Value -is [sbyte] -or $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or $Value -is [int64] -or $Value -is [uint64]) { return "integer" }
    if ($Value -is [single] -or $Value -is [double] -or $Value -is [decimal]) { return "number" }
    if ($Value -is [System.Collections.IDictionary] -or $Value -is [pscustomobject]) { return "object" }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) { return "array" }
    return "string"
}

function Test-NonEmpty($Value) {
    if ($null -eq $Value) { return $false }
    if ($Value -is [string]) { return -not [string]::IsNullOrWhiteSpace($Value) }
    if ($Value -is [System.Collections.ICollection]) { return $Value.Count -gt 0 }
    return $true
}

function Get-SanitizedExample([string]$Field, $Value) {
    if ($null -eq $Value) { return $null }
    $type = Get-JsonType $Value
    if ($Field -match '(?i)(EventTime|CreateTime|ModifyTime|AccessTime|StartTimes)$') { return $Value }
    if ($Field -match '(?i)(Pid|ThreadId|KernelId|ReportId|SessionId|EventId|Cmd)$') { return 1234 }
    if ($Field -match '(?i)(AuthId|TenantId|DomainId|Uid|ProcGuid|ChainGuid|EventUUId|LogonGuid|Common\.Guid|Common\.Mid|Uuid)$') {
        return $(if ($type -eq "integer") { 1000 } else { "<redacted-id>" })
    }
    if ($Field -match '(?i)(UserName|DomainName|DeviceGroup|WorkstationName)$') { return "<redacted-identity>" }
    if ($Field -match '(?i)(Sid|LogonId)$') { return "<redacted-security-id>" }
    if ($Field -match '(?i)(ExportIp|SourceIp|DestinationIp|RemoteIp|LocalIp|DstIp|SrcIp|IpAddress|IpIoc)$') { return "203.0.113.10" }
    if ($Field -match '(?i)MacAddr$') { return "00:00:5E:00:53:01" }
    if ($Field -match '(?i)HostName$') { return "EDRTEST-HOST" }
    if ($Field -match '(?i)(Child\.Host|HostIoc)$') { return "example.invalid" }
    if ($Field -match '(?i)(Child\.Url)$') { return "https://example.invalid/resource" }
    if ($Field -match '(?i)CallStack$') { return "[<redacted-addresses>]" }
    if ($Field -match '(?i)(ProcCmdline|CommandLine|TaskArg|ProcChainInfo)$') { return '"C:\EDR-Test\example\Actor.exe" --nonce <redacted>' }
    if ($Field -match '(?i)(FilePath|OldFilePath|LnkPath|NodeName|ProcCurDir|ProcDesktop|pstrLib)$') {
        if ([string]::Equals([string]$Value, "NULL", [System.StringComparison]::OrdinalIgnoreCase)) { return "NULL" }
        $leaf = [System.IO.Path]::GetFileName([string]$Value)
        if ([string]::IsNullOrWhiteSpace($leaf)) { $leaf = "example.bin" }
        $leaf = [regex]::Replace($leaf, '(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{24,}', '<redacted-id>')
        return "C:\EDR-Test\example\$leaf"
    }
    if ($Field -match '(?i)(PipeName)$') { return '\\.\pipe\edrtest-example' }
    if ($Field -match '(?i)(BlobData)$') { return "<redacted-binary-data>" }
    if ($Field -match '(?i)SourceUrl$') { return $(if ([string]::IsNullOrWhiteSpace([string]$Value)) { "" } else { "https://example.invalid/file" }) }
    if ($Field -match '(?i)Md5$') { return $(if ([string]::IsNullOrWhiteSpace([string]$Value)) { "" } else { "0123456789abcdef0123456789abcdef" }) }
    if ($Field -match '(?i)FileSha$') { return $(if ([string]::IsNullOrWhiteSpace([string]$Value)) { "" } else { "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }) }
    if ($Field -match '(?i)Sha1$') { return $(if ([string]::IsNullOrWhiteSpace([string]$Value)) { "" } else { "0123456789abcdef0123456789abcdef01234567" }) }
    if ($Field -match '(?i)Sha256$') { return $(if ([string]::IsNullOrWhiteSpace([string]$Value)) { "" } else { "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }) }
    return $Value
}

function New-FieldCatalog([object[]]$Records) {
    $keys = @($Records | ForEach-Object { $_.PSObject.Properties.Name } | Sort-Object -Unique)
    $catalog = foreach ($key in $keys) {
        $presentValues = @($Records | ForEach-Object {
            $property = $_.PSObject.Properties[$key]
            if ($null -ne $property) { $property.Value }
        })
        $nonEmptyValues = @($presentValues | Where-Object { Test-NonEmpty $_ })
        $types = @($presentValues | ForEach-Object { Get-JsonType $_ } | Sort-Object -Unique)
        $examples = @($nonEmptyValues | ForEach-Object { Get-SanitizedExample $key $_ } |
            Select-Object -Unique | Select-Object -First 3)
        [ordered]@{
            field = $key
            presence_count = $presentValues.Count
            non_empty_count = $nonEmptyValues.Count
            presence_rate = [Math]::Round($presentValues.Count / [Math]::Max($Records.Count, 1), 6)
            types = $types
            examples = $examples
        }
    }
    return @($catalog)
}

$events = @(Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json -Depth 100 -DateKind String)
if ($events.Count -eq 0) { throw "EDR 导出文件没有事件。" }
$eventKinds = @($events | Group-Object 'Action.Type', 'Action.Name' | ForEach-Object {
    $first = $_.Group[0]
    $tables = @($_.Group | ForEach-Object { $_.'@table' } | Sort-Object -Unique)
    [ordered]@{
        action_type = $first.'Action.Type'
        action_name = $first.'Action.Name'
        tables = $tables
        event_count = $_.Count
        field_names = @($_.Group | ForEach-Object { $_.PSObject.Properties.Name } | Sort-Object -Unique)
    }
} | Sort-Object action_type, action_name)

$relativeSource = [System.IO.Path]::GetRelativePath($repositoryRoot, $InputPath).Replace('\', '/')
$output = [ordered]@{
    schema_version = "1.0"
    catalog_id = "tencent-edr-260808-all-fields"
    description = "由 260808210300run 的腾讯 EDR 全字段导出生成；示例已脱敏，reference 原文件不进入版本控制。"
    source = [ordered]@{
        relative_path = $relativeSource
        sha256 = (Get-FileHash -LiteralPath $InputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        event_count = $events.Count
    }
    sanitization = [ordered]@{
        applied = $true
        categories = @("主机与账号", "IP/MAC", "路径与命令行", "事件/进程/租户标识", "调用栈", "文件哈希")
    }
    all_fields = New-FieldCatalog $events
    event_kinds = $eventKinds
}

[System.IO.Directory]::CreateDirectory((Split-Path -Parent $OutputPath)) | Out-Null
$output | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Host "已生成腾讯 EDR 字段目录：$OutputPath（$($events.Count) 条事件，$(@($output.all_fields).Count) 个字段）"
