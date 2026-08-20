[CmdletBinding()]
param(
    [string]$ReferenceRoot,
    [string]$FieldListPath,
    [string]$OutputPath,
    [string]$TextOutputPath
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ReferenceRoot)) { $ReferenceRoot = Join-Path $repositoryRoot "reference" }
if ([string]::IsNullOrWhiteSpace($FieldListPath)) { $FieldListPath = Join-Path $repositoryRoot "reference\EDR日志-字段表.txt" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repositoryRoot "docs\reference\tencent-edr-field-catalog.json" }
if ([string]::IsNullOrWhiteSpace($TextOutputPath)) { $TextOutputPath = $FieldListPath }
$ReferenceRoot = [System.IO.Path]::GetFullPath($ReferenceRoot)
$FieldListPath = [System.IO.Path]::GetFullPath($FieldListPath)
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$TextOutputPath = [System.IO.Path]::GetFullPath($TextOutputPath)
if (-not (Test-Path -LiteralPath $ReferenceRoot -PathType Container)) { throw "找不到 reference 目录：$ReferenceRoot" }
if (-not (Test-Path -LiteralPath $FieldListPath -PathType Leaf)) { throw "找不到字段表：$FieldListPath" }

function Get-FieldNames([string]$Path) {
    $pattern = '^(?<field>@(?:collection|table|timestamp)|(?:Action|Alert|Child|Common|Environment|Parent|PParent)\.[A-Za-z][A-Za-z0-9]*|(?:Cmd|DomainId|OS|TenantId|Uuid|Version))(?:\t|$)'
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -notmatch $pattern) { continue }
        $field = $Matches.field
        if ($seen.Add($field)) { $result.Add($field) }
    }
    if ($result.Count -eq 0) { throw "字段表没有可识别的腾讯 EDR 字段。" }
    return @($result)
}

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
    if ($type -in @("array", "object")) { return "<redacted-structured-value>" }
    if ($Field -match '(?i)(EventTime|FileCreateTime|FileModifyTime|FileAccessTime|ProcCreateTime|SysStartTimes)$') { return $Value }
    if ($Field -match '(?i)(Pid|ThreadId|ReportId|SessionId|EventId|Cmd)$') { return 1234 }
    if ($Field -match '(?i)(AuthId|TenantId|DomainId|Uid|ProcGuid|ChainGuid|EventUUId|Common\.Guid|Common\.Mid|Alert\.EventUuid|Alert\.RuleUuid|^Uuid$)$') {
        return $(if ($type -eq "integer") { 1000 } else { "<redacted-id>" })
    }
    if ($Field -match '(?i)(UserName|DisplayName|SamAccountName|UserPrincipalName|DomainName|DeviceGroup|OsUserName)$') { return "<redacted-identity>" }
    if ($Field -match '(?i)(Sid|SidHistory|LogonId)$') { return "<redacted-security-id>" }
    if ($Field -match '(?i)(ExportIp)$') { return "203.0.113.10" }
    if ($Field -match '(?i)MacAddr$') { return "00:00:5E:00:53:01" }
    if ($Field -match '(?i)HostName$') { return "EDRTEST-HOST" }
    if ($Field -match '(?i)(ProcCmdline|ProcChainInfo)$') { return '"C:\EDR-Test\example\Actor.exe" --nonce <redacted>' }
    if ($Field -match '(?i)NodeName$') {
        $text = [string]$Value
        if ($text.StartsWith("\\", [System.StringComparison]::Ordinal)) { return "\\EdrTest_Example" }
        if ($text -match '^[A-Za-z]:\\') { return "C:\EDR-Test\example\object" }
        return "<redacted-object-name>"
    }
    if ($Field -match '(?i)(FilePath|HomeDirectory|HomePath|ProfilePath|ScriptPath)$') {
        if ([string]::Equals([string]$Value, "<未设置值>", [System.StringComparison]::Ordinal)) { return "<未设置值>" }
        $leaf = [System.IO.Path]::GetFileName([string]$Value)
        if ([string]::IsNullOrWhiteSpace($leaf)) { $leaf = "example.bin" }
        $leaf = [regex]::Replace($leaf, '(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{24,}', '<redacted-id>')
        return "C:\EDR-Test\example\$leaf"
    }
    if ($Field -match '(?i)(AllowedToDelegateTo|UserWorkstations)$') { return "<redacted-host-list>" }
    if ($Field -match '(?i)(UserParameters|LogonHours)$') { return "<redacted-account-data>" }
    if ($Field -match '(?i)FileSourceUrl$') { return $(if ([string]::IsNullOrWhiteSpace([string]$Value)) { "" } else { "https://example.invalid/file" }) }
    if ($Field -match '(?i)FileMd5$') { return $(if ([string]::IsNullOrWhiteSpace([string]$Value)) { "" } else { "0123456789abcdef0123456789abcdef" }) }
    return $Value
}

$exactMeanings = @{
    "@collection" = "EDR 平台收集或导出该记录的时间"
    "@table" = "记录所属的腾讯 EDR 事件表"
    "@timestamp" = "记录写入、索引或平台处理时间"
    "Action.EventLogId" = "Windows Event Log 事件 ID"
    "Action.EventMode" = "EDR 事件采集或处理模式"
    "Action.Name" = "腾讯 EDR 原始动作名称"
    "Action.Type" = "腾讯 EDR 原始动作大类"
    "Alert.EventUuid" = "关联告警事件唯一标识"
    "Alert.RuleId" = "命中的告警规则数字 ID"
    "Alert.RuleName" = "命中的告警规则中文名称"
    "Alert.RuleNameEN" = "命中的告警规则英文名称"
    "Alert.RuleNature" = "告警规则性质或来源类型"
    "Alert.RuleUuid" = "告警规则唯一标识"
    "Child.AccountExpires" = "目标账号过期时间或未过期标记"
    "Child.AllowedToDelegateTo" = "目标账号允许委派到的服务主体列表"
    "Child.DisabledPrivilegeList" = "目标登录令牌中被禁用的权限列表"
    "Child.DisplayName" = "目标账号显示名称"
    "Child.EnabledPrivilegeList" = "目标登录令牌中已启用的权限列表"
    "Child.HomeDirectory" = "目标账号主目录配置"
    "Child.HomePath" = "目标账号主目录路径"
    "Child.LogonHours" = "目标账号允许登录的时间段"
    "Child.NewUacValue" = "账号控制属性变更后的十六进制值"
    "Child.NodeName" = "被操作对象的节点名称、路径或任务名称"
    "Child.OldUacValue" = "账号控制属性变更前的十六进制值"
    "Child.PasswordLastSet" = "目标账号最后设置密码的时间或状态"
    "Child.PrimaryGroupId" = "目标账号主组 RID"
    "Child.PrivilegeList" = "事件涉及或分配的 Windows 权限列表"
    "Child.ProfilePath" = "目标账号漫游配置文件路径"
    "Child.SamAccountName" = "目标账号的 SAM 兼容名称"
    "Child.ScriptPath" = "目标账号登录脚本路径"
    "Child.SidHistory" = "目标账号 SID 历史列表"
    "Child.SubjectDomainName" = "发起操作的主体域名"
    "Child.SubjectLogonId" = "发起操作主体的登录会话 ID"
    "Child.SubjectUserName" = "发起操作的主体账号名"
    "Child.SubjectUserSid" = "发起操作主体的 SID"
    "Child.TargetDomainName" = "被操作或登录的目标域名"
    "Child.TargetLogonId" = "目标登录会话 ID"
    "Child.TargetSid" = "被操作目标的 SID"
    "Child.TargetUserName" = "被操作或登录的目标账号名"
    "Child.TargetUserSid" = "目标账号 SID"
    "Child.Type" = "被操作对象的类型或子节点类型"
    "Child.UserAccountControl" = "账号 UAC 标志的文本化变更列表"
    "Child.UserParameters" = "目标账号的扩展用户参数"
    "Child.UserPrincipalName" = "目标账号的用户主体名称 UPN"
    "Child.UserWorkstations" = "允许目标账号登录的工作站列表"
    "Cmd" = "EDR 内部消息或命令编号"
    "Common.AgentMode" = "终端 Agent 工作模式或产品版本模式"
    "Common.AuthId" = "终端与平台之间的内部鉴权标识"
    "Common.ClientVer" = "终端 Agent 客户端版本"
    "Common.DeviceGroup" = "终端所属设备分组"
    "Common.EdrSdkVer" = "EDR 采集 SDK 版本"
    "Common.EventId" = "腾讯 EDR 内部事件类型编号"
    "Common.EventTime" = "事件在终端发生的 Unix 毫秒时间戳"
    "Common.EventUUId" = "腾讯 EDR 事件全局唯一标识"
    "Common.Guid" = "EDR 内部记录或关联链 GUID"
    "Common.LoginStatus" = "Agent 或终端在平台的登录状态"
    "Common.Mid" = "腾讯 EDR 终端机器唯一标识"
    "Common.MonitorName" = "产生记录的监控项中文名称"
    "Common.ReportId" = "Agent 本地上报序号或报告 ID"
    "Common.SessionId" = "事件关联的 Windows 会话 ID"
    "Common.Source" = "事件采集来源模块"
    "Common.Uid" = "EDR 内部用户标识，常见值为 0"
    "Common.UserName" = "EDR 记录关联的当前用户名称"
    "DomainId" = "EDR 内部域或组织索引"
    "Environment.DomainName" = "终端加入的 Windows 域名称"
    "Environment.ExportIp" = "终端向平台上报时使用的出口 IP"
    "Environment.HostName" = "终端主机名"
    "Environment.MacAddr" = "终端网卡 MAC 地址"
    "Environment.OsBit" = "操作系统位数"
    "Environment.OsBuild" = "Windows 操作系统构建号"
    "Environment.OsProduectDesc" = "操作系统产品描述；原字段名包含厂商拼写错误"
    "Environment.OsType" = "操作系统类型"
    "Environment.OsUserName" = "终端当前操作系统用户名"
    "Environment.OsVersion" = "操作系统版本号"
    "Environment.SysStartTimes" = "终端本次系统启动时间"
    "OS" = "事件所属操作系统平台"
    "TenantId" = "腾讯 EDR 租户唯一标识"
    "Uuid" = "记录的辅助唯一标识"
    "Version" = "EDR 记录结构或协议版本"
}

$processSuffixMeanings = @{
    "CloudAttr" = "云端信誉属性"
    "FileAccessTime" = "可执行文件最后访问时间（Unix 秒）"
    "FileCompany" = "可执行文件版本资源中的公司名称"
    "FileCopyright" = "可执行文件版本资源中的版权信息"
    "FileCreateTime" = "可执行文件创建时间（Unix 秒）"
    "FileDesc" = "可执行文件描述"
    "FileDriverType" = "可执行文件所在存储介质或驱动器类型"
    "FileFormat" = "可执行文件格式或 PE 类型"
    "FileIssuer" = "代码签名证书颁发者"
    "FileLegalMark" = "可执行文件版本资源中的法律商标信息"
    "FileMd5" = "可执行文件 MD5"
    "FileMd5Type" = "可执行文件 MD5 的计算范围或类型"
    "FileModifyTime" = "可执行文件最后修改时间（Unix 秒）"
    "FileName" = "可执行文件名"
    "FileOriginalName" = "可执行文件 PE 版本资源中的原始文件名"
    "FilePath" = "可执行文件绝对路径"
    "FileProductName" = "可执行文件 PE 版本资源中的产品名称"
    "FileProductVer" = "可执行文件 PE 版本资源中的产品版本"
    "FileSign" = "代码签名主体或发布者"
    "FileSignStatus" = "代码签名验证状态"
    "FileSignWhite" = "代码签名是否属于白名单"
    "FileSize" = "可执行文件大小（字节）"
    "FileSourceUrl" = "可执行文件下载或来源 URL"
    "FileTags" = "可执行文件的 EDR 标签"
    "FileVersion" = "可执行文件 PE 版本"
    "NodeName" = "对象节点名称，通常与文件路径相关"
    "ProcArch" = "架构"
    "ProcChainGuid" = "关联进程链唯一标识"
    "ProcChainInfo" = "关联进程祖先链的序列化信息"
    "ProcChainTrust" = "关联进程链整体可信度"
    "ProcCmdline" = "完整命令行"
    "ProcCreateTime" = "创建时间"
    "ProcDomainName" = "所属用户域"
    "ProcElevationType" = "令牌提升类型"
    "ProcExited" = "采集时是否已退出"
    "ProcGuid" = "由 EDR 分配的进程实例 GUID"
    "ProcIntegrity" = "完整性级别"
    "ProcPid" = "PID"
    "ProcTrust" = "可信度"
    "ProcUserName" = "所属用户名"
    "ThreadId" = "触发行为的线程 ID"
    "Type" = "对象或节点类型"
}

function Get-FieldMeaning([string]$Field) {
    if ($exactMeanings.ContainsKey($Field)) { return $exactMeanings[$Field] }
    if ($Field -match '^(Parent|PParent)\.(?<suffix>.+)$') {
        $suffix = $Matches.suffix
        if (-not $processSuffixMeanings.ContainsKey($suffix)) { throw "字段缺少中文含义：$Field" }
        $prefix = if ($Field.StartsWith("PParent.", [System.StringComparison]::Ordinal)) {
            "行为主体进程的上级进程"
        } else {
            "行为主体或发起进程"
        }
        return "$($prefix)的$($processSuffixMeanings[$suffix])"
    }
    throw "字段缺少中文含义：$Field"
}

function Get-BaselineRole([string]$Field) {
    if ($Field -eq "Common.EventTime") { return "time_anchor" }
    if ($Field -in @("@table", "Action.EventLogId", "Action.Name", "Action.Type", "Common.MonitorName", "Common.Source")) { return "candidate_filter" }
    if ($Field -match '^(Parent|PParent)\.(ProcPid|ProcGuid|ProcCmdline|FilePath|FileName|ProcCreateTime|ProcUserName|ProcDomainName)$') { return "correlation_anchor" }
    if ($Field -match '^Child\.(NodeName|TargetDomainName|TargetLogonId|TargetSid|TargetUserName|TargetUserSid|SamAccountName|UserPrincipalName)$') { return "correlation_anchor" }
    if ($Field -match '^Child\.') { return "capability_assertion" }
    if ($Field -match '^Parent\.(FileMd5|FileSize|FileSign|FileSignStatus|FileFormat|ProcArch|ProcIntegrity|ProcElevationType)$') { return "recommended_assertion" }
    if ($Field -in @("Common.Mid", "Environment.HostName", "Environment.OsBuild", "Environment.OsVersion", "Environment.SysStartTimes", "OS")) { return "host_scope" }
    return "context_only"
}

function Get-BaselineNote([string]$Field, [string]$Role) {
    switch ($Field) {
        "Common.EventTime" { return "映射为 event.created，并与本地 occurred_at_utc 计算有符号时间差。" }
        "@timestamp" { return "平台处理时间，仅用于诊断和展示，不替代 Common.EventTime。" }
        "@collection" { return "收集或导出时间，仅用于诊断，不作为行为发生时间。" }
        "Action.Name" { return "只可作为 EDR 可选消歧规则，不影响本地规则，也不得成为唯一候选入口。" }
        "Action.EventLogId" { return "Windows Event Log 路径可作为动作语义筛选；仍需本地时间、对象和进程锚点。" }
        "Common.Mid" { return "用于限定终端；只有用户提供可信主机映射时才参与过滤。" }
        "Common.EventUUId" { return "用于候选去重和证据引用，不与本地生成的 ID 比较。" }
    }
    switch ($Role) {
        "candidate_filter" { return "用于候选路由或可选筛选，不能单独证明能力。" }
        "correlation_anchor" { return "优先与本地绝对基准关联；路径、PID、账号或对象需使用相应规范化。" }
        "capability_assertion" { return "仅在对应能力语义明确时作为 required/recommended 断言。" }
        "recommended_assertion" { return "用于增强文件或进程身份置信度，缺失时不应阻止低置信候选展示。" }
        "host_scope" { return "用于主机和运行环境范围确认，不单独证明行为。" }
        default { return "作为诊断上下文保存，默认不直接写入能力 required 断言。" }
    }
}

function New-FieldStat([string]$Field) {
    return [pscustomobject]@{
        Field = $Field
        PresenceCount = 0
        NonEmptyCount = 0
        Types = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        ExampleKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        Examples = [System.Collections.Generic.List[object]]::new()
        Tables = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        ActionNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        SourceRuns = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    }
}

$fields = Get-FieldNames $FieldListPath
$fieldStats = [ordered]@{}
foreach ($field in $fields) { $fieldStats[$field] = New-FieldStat $field }
$eventIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$sourceExports = [System.Collections.Generic.List[object]]::new()
$eventKinds = @{}

foreach ($file in Get-ChildItem -LiteralPath $ReferenceRoot -Recurse -File -Filter *.json | Sort-Object FullName) {
    try { $records = @(Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 100 -DateKind String) }
    catch { continue }
    $edrRecords = @($records | Where-Object {
        $_ -is [pscustomobject] -and $null -ne $_.PSObject.Properties['@table'] -and $null -ne $_.PSObject.Properties['Action.Name']
    })
    if ($edrRecords.Count -eq 0) { continue }
    $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).Replace('\', '/')
    $relativeRun = [System.IO.Path]::GetRelativePath($ReferenceRoot, $file.DirectoryName).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relativeRun) -or $relativeRun -eq ".") { $relativeRun = "reference-root" }
    $included = 0
    for ($index = 0; $index -lt $edrRecords.Count; $index++) {
        $record = $edrRecords[$index]
        $table = [string]$record.'@table'
        $actionName = [string]$record.'Action.Name'
        $actionType = [string]$record.'Action.Type'
        $eventUuid = [string]$record.'Common.EventUUId'
        if ([string]::IsNullOrWhiteSpace($eventUuid)) { $eventUuid = [string]$record.Uuid }
        $identity = if ([string]::IsNullOrWhiteSpace($eventUuid)) {
            "$relativePath#$index"
        } else {
            "$table|$actionName|$eventUuid"
        }
        if (-not $eventIds.Add($identity)) { continue }
        $included++

        $kindKey = "$actionType`u{001f}$actionName"
        if (-not $eventKinds.ContainsKey($kindKey)) {
            $eventKinds[$kindKey] = [pscustomobject]@{
                ActionType = $actionType
                ActionName = $actionName
                Count = 0
                Tables = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            }
        }
        $eventKinds[$kindKey].Count++
        [void]$eventKinds[$kindKey].Tables.Add($table)

        foreach ($field in $fields) {
            $property = $record.PSObject.Properties[$field]
            if ($null -eq $property) { continue }
            $stat = $fieldStats[$field]
            $stat.PresenceCount++
            [void]$stat.Types.Add((Get-JsonType $property.Value))
            [void]$stat.Tables.Add($table)
            [void]$stat.ActionNames.Add($actionName)
            [void]$stat.SourceRuns.Add($relativeRun)
            if (-not (Test-NonEmpty $property.Value)) { continue }
            $stat.NonEmptyCount++
            if ($stat.Examples.Count -ge 3) { continue }
            $example = Get-SanitizedExample $field $property.Value
            $exampleKey = $example | ConvertTo-Json -Compress -Depth 10
            if ($stat.ExampleKeys.Add($exampleKey)) { $stat.Examples.Add($example) }
        }
    }
    $sourceExports.Add([ordered]@{
        relative_path = $relativePath
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        exported_event_count = $edrRecords.Count
        unique_event_count = $included
    })
}

if ($eventIds.Count -eq 0) { throw "reference 中没有识别到 EDR 原始事件。" }

$catalogFields = foreach ($field in $fields) {
    $stat = $fieldStats[$field]
    $role = Get-BaselineRole $field
    [ordered]@{
        field = $field
        meaning_zh = Get-FieldMeaning $field
        observed = $stat.PresenceCount -gt 0
        presence_count = $stat.PresenceCount
        non_empty_count = $stat.NonEmptyCount
        presence_rate = [Math]::Round($stat.PresenceCount / [Math]::Max($eventIds.Count, 1), 6)
        types = @($stat.Types | Sort-Object)
        examples = @($stat.Examples)
        tables = @($stat.Tables | Sort-Object)
        action_names = @($stat.ActionNames | Sort-Object)
        source_runs = @($stat.SourceRuns | Sort-Object)
        baseline_role = $role
        baseline_note_zh = Get-BaselineNote $field $role
    }
}

$catalog = [ordered]@{
    schema_version = "2.0"
    catalog_id = "tencent-edr-baseline-field-reference"
    description = "以 reference/EDR日志-字段表.txt 的厂商字段清单为范围，结合全部已知 run 的 EDR 原始导出补充中文含义、脱敏示例、表/动作上下文和 BASELINE 使用建议；替代原 260808 field-catalog。"
    source = [ordered]@{
        field_list = "reference/EDR日志-字段表.txt"
        reference_export_count = $sourceExports.Count
        unique_event_count = $eventIds.Count
        exports = @($sourceExports)
    }
    sanitization = [ordered]@{
        applied = $true
        categories = @("主机与账号", "IP/MAC", "路径与命令行", "事件/进程/租户标识", "SID/登录标识", "文件哈希")
    }
    baseline_policy = [ordered]@{
        local_run_is_absolute_baseline = $true
        event_time_field = "Common.EventTime"
        action_name_is_optional_edr_filter_only = $true
        unobserved_field_requires_new_export_before_required_assertion = $true
        note = "字段目录限定厂商字段选择；BASELINE 仍只引用 Canonical 字段，原字段只进入 Mapping Profile。"
    }
    field_count = $catalogFields.Count
    fields = @($catalogFields)
    event_kinds = @($eventKinds.Values | Sort-Object ActionType, ActionName | ForEach-Object {
        [ordered]@{
            action_type = $_.ActionType
            action_name = $_.ActionName
            tables = @($_.Tables | Sort-Object)
            event_count = $_.Count
        }
    })
}

[System.IO.Directory]::CreateDirectory((Split-Path -Parent $OutputPath)) | Out-Null
$catalog | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM

$text = [System.Collections.Generic.List[string]]::new()
$text.Add("腾讯 EDR BASELINE 字段基准（由已知 run 自动补充，示例已脱敏）")
$text.Add("字段数：$($catalogFields.Count)；EDR 导出文件：$($sourceExports.Count)；去重事件：$($eventIds.Count)")
$text.Add("规则：本地运行日志是绝对基准；Common.EventTime 是云端事件时间；Action.Name 只作可选 EDR 筛选；未观测字段不能直接设为 required。")
$text.Add("")
$text.Add("字段`t中文含义`tJSON类型`t脱敏示例`t出现表`tAction.Name示例`tBASELINE角色`t使用说明")
foreach ($item in $catalogFields) {
    $examples = @($item.examples | ForEach-Object {
        ($_ | ConvertTo-Json -Compress -Depth 10) -replace "[`r`n`t]", " "
    }) -join "；"
    $text.Add(@(
        $item.field,
        $item.meaning_zh,
        (@($item.types) -join "/"),
        $examples,
        (@($item.tables) -join ","),
        (@($item.action_names | Select-Object -First 8) -join ","),
        $item.baseline_role,
        $item.baseline_note_zh
    ) -join "`t")
}
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $TextOutputPath)) | Out-Null
$text | Set-Content -LiteralPath $TextOutputPath -Encoding utf8NoBOM

Write-Host "已生成腾讯 EDR BASELINE 字段基准：$OutputPath"
Write-Host "已更新人工可读字段表：$TextOutputPath"
Write-Host "字段 $($catalogFields.Count) 个；EDR 导出 $($sourceExports.Count) 份；去重事件 $($eventIds.Count) 条。"
