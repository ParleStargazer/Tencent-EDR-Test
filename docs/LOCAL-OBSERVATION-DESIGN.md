# 本地测试信息采集与 JSON 数据契约

## 1. 目的

本文定义每轮本地能力测试必须收集的信息，以及 `local-run.json` 的版本化格式。该文件是 SQLite 运行数据库的确定性导出，不是腾讯 EDR 云端 JSON 的仿制品。

设计目标：

1. Controller 能独立证明目标 Windows 行为确实发生；
2. Compare 能用本地强锚点从云端导出中唯一召回对应事件；
3. 任意 PASS、FAIL 或 INCONCLUSIVE 都能回溯到程序、时间、参数和证据；
4. 不依赖腾讯 EDR API，不伪造厂商内部字段，不把云端原始日志写进运行数据库；
5. 同一 SQLite 数据库重复导出时，除明确标记的导出时间外，业务内容保持确定。

对应文件：

- 完整轮次 Schema：`schemas/run-export.schema.json`
- 分类事件数据 Schema：`schemas/local-event-data.schema.json`
- 进程创建完整示例：`examples/local-run.process-create.example.json`
- SQLite DDL：`schemas/run-db.sql`

## 2. 从参考 JSON 得到的事实

对本地忽略目录中的 `reference/EDR事件导出示例.json` 进行结构统计后得到：

| 项目 | 结果 |
| --- | --- |
| 顶层容器 | JSON 数组 |
| 记录数 | 1760 |
| 表 | `ProcEvents`，1760 条 |
| 唯一字段数 | 161 |
| `ProcessCreate` | 1744 条 |
| `ProcessHandleObject` | 13 条 |
| `RemoteThread` | 3 条 |

参考文件只能直接证明“进程活动”导出格式，不能证明文件、注册表、网络等其他表的真实腾讯字段。其他能力域的本地字段依据 Windows 行为本身和项目能力目录设计；取得相应云端导出后，应该补充 Mapping Profile，而不是改写本地事实语义。

### 2.1 云端字段组

参考记录采用扁平键名，主要分为：

| 字段组 | 含义 | 本地对应 |
| --- | --- | --- |
| `@collection`、`@timestamp`、`@table` | 云端收集/导出元数据 | 不在本地伪造；只用于云端范围和记录内采集时差 |
| `Action.*` | 云端事件类别和动作 | `local_events[].event_type/event_action` |
| `Common.EventTime` | 事件发生时间，Unix ms | `occurred_at_utc` |
| `Common.EventUUId` | 腾讯事件唯一 ID | 云端证据 ID，本地没有对应值 |
| `Common.Mid` | 腾讯终端 ID | 本地主机信息 + 用户导出清单；可选 `agent_id_hint` 仅接受显式配置 |
| `Environment.*` | 主机、操作系统和启动时间 | `run.host` |
| `Parent.*` | 行为发起进程 | Actor 程序实例和 `data.actor` |
| `Child.*` | 新进程或被操作进程 | Target 程序实例和 `data.target` |
| `PParent.*` | 发起进程的上级进程 | Controller/Actor 的 `parent_pid` 与程序链 |

### 2.2 进程活动的高价值云端字段

参考文件中以下字段在相应动作里稳定出现，应该在本地尽量取得可比较事实：

| 云端字段 | 本地字段 | 用途 |
| --- | --- | --- |
| `Environment.HostName` | `run.host.hostname` | 主机过滤 |
| `Environment.OsBuild` | `run.host.os_build` | 环境说明 |
| `Environment.SysStartTimes` | `run.host.boot_time_utc` | 区分重启前后 PID |
| `Child.FilePath` | `programs[target].executable` | 强锚点 |
| `Child.FileName` | `programs[target].file_name` | 弱锚点 |
| `Child.FileMd5` | `programs[target].md5` | 文件身份 |
| `Child.FileSize` | `programs[target].file_size_bytes` | 文件身份辅助 |
| `Child.ProcPid` | `programs[target].pid` | 中锚点，不能单独判定 |
| `Child.ProcCreateTime` | `programs[target].started_at_utc` | 与 PID 组成进程身份 |
| `Child.ProcCmdline` | `programs[target].command_line` | nonce 强锚点 |
| `Child.ProcUserName/DomainName` | `programs[target].user` | 字段完整性 |
| `Child.ProcArch` | `programs[target].architecture` | 字段完整性 |
| `Child.ProcIntegrity` | `programs[target].integrity_level` | 字段完整性 |
| `Parent.FilePath` | `programs[actor].executable` | 强锚点 |
| `Parent.ProcPid` | `programs[actor].pid` | 父子关系 |
| `Parent.ProcCmdline` | `programs[actor].command_line` | nonce 强锚点 |
| `Parent.ThreadId` | `data.actor.thread_id` 或事件扩展 | 线程关联 |
| `Child.HandleAccessMask` | `data.access.requested_access_mask` | 进程访问专属 |
| `Child.HandleObjectOpName` | `data.access.operation_name` | 进程访问专属 |
| `Child.ThreadId` | `data.thread.thread_id` | 远程线程专属 |
| `Child.ThreadStartAddr` | `data.thread.start_address` | 远程线程专属 |

`Child.ProcGuid`、`Parent.ProcGuid` 是 EDR 生成的内部实体 ID。本地不得猜测或构造这些值。本地使用“PID + 进程开始时间 + 绝对路径 + 命令行 nonce”形成可验证的进程身份。

地址字段在参考 JSON 中是数字，但 Windows 地址可能超过 JavaScript 安全整数范围；本地 JSON 一律使用 `0x...` 十六进制字符串。

## 3. 必须区分的五层信息

每个能力都必须同时记录以下五层，不能只写一条“执行成功”：

1. **执行意图**：能力 ID、版本、参数、nonce、风险级别、预检结果；
2. **参与程序**：Controller、Actor、Target、Helper 的文件身份和进程身份；
3. **操作结果**：Win32 API 是否调用、返回值、Win32 Error、NTSTATUS；
4. **独立观测**：Controller 使用句柄查询、文件读取、注册表查询、本地服务回执或事件日志确认的实际状态；
5. **清理证据**：清理前状态、清理动作、清理后状态和错误。

状态语义：

- `attempted=true`：Actor 确实尝试调用目标 API；
- `succeeded=true`：API 或系统调用返回成功；
- `confidence=high`：Controller 又通过独立数据源确认了实际结果；
- `LOCAL_PASS`：行为、独立观测和清理全部满足 BASELINE；
- API 返回成功但无法独立确认时，不得直接得到 `LOCAL_PASS`。

## 4. 顶层 JSON 结构

```json
{
  "schema_version": "1.1",
  "run": {},
  "capabilities": [],
  "programs": [],
  "local_events": [],
  "local_facts": [],
  "artifacts": [],
  "cleanup_results": [],
  "execution_logs": [],
  "integrity": {}
}
```

### 4.1 `run`

一轮仅有一个 `run`：

- `run_id`：UUIDv7；
- SQLite Schema、Runner 版本、Suite 和环境 ID；
- 开始/结束 UTC、IANA 时区、UTC 偏移；
- 系统时钟来源、同步状态、单调时钟频率；
- 主机名、Windows 版本/Build/Edition、架构、启动时间、域；
- 可选 Agent ID/版本提示，只能来自用户配置或公开支持的本地信息，不主动接入腾讯后台。

主机启动时间是必需字段。PID 会复用，只有在同一主机启动周期内结合进程开始时间才有意义。

### 4.2 `capabilities`

每个能力实例记录：

- `case_run_id`、能力 ID/版本、Manifest hash、BASELINE 版本；
- 中英文名称、能力域、顺序、nonce；
- 风险级别、所需权限、参数和预检条件；
- 本地状态、开始/结束、单调耗时和错误；
- `observation_window`：观察器起止、数据源、丢失事件数和警告。

如果观察器报告丢失事件，即使目标事件未出现，也不能仅凭本地观察得出“行为没有发生”。

### 4.3 `programs`

Controller、Actor、Target、Helper 每个进程实例分别记录：

- 角色、实例名和实例序号；
- EXE 绝对路径、文件名、大小、文件时间；
- MD5、SHA-1、SHA-256、IMPHASH；
- 签名状态、签名者、签发者和证书指纹；
- PID、父 PID、Session ID、架构；
- 完整命令行、工作目录；
- 用户 SID/名称/域、完整性级别和提升类型；
- 进程开始/结束 UTC、退出码；
- 启动 API 的尝试、成功与错误。

其中 SHA-256、PID、父 PID、命令行和开始时间是所有程序实例的必需字段。命令行必须携带 nonce，但不得包含凭据。

### 4.4 `local_events`

一条本地行为观测包括：

- 唯一 ID、能力实例 ID、事件内顺序；
- `event_type`、`event_action`；
- nonce；
- 行为发生 UTC、Controller 观测 UTC、相对本能力开始的单调毫秒；
- 数据源、采集方法、采集器版本和可信度；
- Actor/Target 程序实例引用；
- 分类 `data`；
- 证据工件引用。

`occurred_at_utc` 与 `observed_at_utc` 不应混为一个字段。前者用于匹配 EDR `Common.EventTime`，后者用于评估本地观察器延迟。

### 4.5 `local_facts`

`local_facts` 是 BASELINE 可直接引用的稳定键值索引，例如：

```text
process.create_succeeded
process.child_pid
process.parent_pid
file.path
file.after.sha256
registry.after.value_data
network.remote.port
correlation.nonce
```

复杂、可变的数据放在 `local_events[].data`；经常参与断言的单值同时投影到 `local_facts`。每个事实必须声明来源和可信度。

### 4.6 `artifacts`、`cleanup_results`、`execution_logs`、`integrity`

- 工件只记录相对路径、类型、大小、SHA-256 和敏感标记，不内嵌大文件；
- 清理记录必须包含前后状态；
- 执行日志保存级别、阶段、消息和属性，并通过 `case_run_id` 归入具体能力；
- 完整性记录包含数据库 hash、Schema hash、数据库大小和各数组计数；
- JSON 自身 hash 不能写入自身，Export 工具在旁路输出 `<file>.sha256`。

## 5. 53 项能力的数据要求

以下路径均位于 `local_events[].data`。每条数据还必须包含该分类的 `kind`、`operation` 和 `result`。

### 5.1 进程活动（Process Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 进程创建（Process Creation） | `process/create` | Actor、Target；双方 PID/路径/命令行/开始时间；父子关系；初始线程 ID；CreateProcess flags 与结果 |
| 进程终止（Process Termination） | `process/terminate` | Target 身份；终止方式；请求退出码、观察退出码；退出时间；操作结果 |
| 进程访问（Process Access） | `process/access` | Actor/Target 身份；操作名；请求和授予的 Access Mask；句柄是否取得；错误码 |
| 加载镜像或动态库（Image/Library Loaded） | `process/image_load` | Target 的系统目录 LoadLibraryW、应用目录 LoadLibraryW、应用目录 LoadLibraryExW，以及 `dotnet.exe` Helper 的 AssemblyLoadContext 托管程序集加载四个子项；各自的目标角色/PID、发生时间、方法、源路径、实际模块路径、文件名、基址、大小、hash、加载前后枚举；临时 DLL 清理结果 |
| 远程线程创建（Remote Thread Creation） | `process/remote_thread_create` | Actor/Target；线程 ID；起始地址、参数地址、flags；API 结果 |
| 进程篡改活动（Process Tampering Activity） | `process/tamper` | Actor/Target；技术名；目标地址/长度；变更前后 hash；API 结果；恢复证据 |

### 5.2 文件操作（File Manipulation）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 文件创建（File Creation） | `file/create` | Actor；绝对路径；创建前不存在；创建后大小、时间、hash；API 结果 |
| 文件打开（File Opened） | `file/open` | Actor；路径；Desired Access、Share Mode、Creation Disposition；句柄结果 |
| 文件删除（File Deletion） | `file/delete` | Actor；路径；删除前 hash/大小；删除后不存在；API 结果 |
| 文件修改（File Modification） | `file/modify` | Actor；路径；修改前后大小/hash/时间；写入字节数和 offset |
| 文件重命名（File Renaming） | `file/rename` | Actor；源/目标绝对路径；源前状态、目标后状态；API 结果 |

### 5.3 用户账号活动（User Account Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 本地账号创建（Local Account Creation） | `account/local_create` | 账号名、SID、域、类型；创建前后状态；调用者；错误码 |
| 本地账号修改（Local Account Modification） | `account/local_modify` | 账号身份；变更字段；变更前后状态；调用者；错误码 |
| 本地账号删除（Local Account Deletion） | `account/local_delete` | 账号身份；删除前存在、删除后不存在；调用者；错误码 |
| 账号登录（Account Login） | `account/login` | SID/账号/域；Session ID、Logon ID/Type、认证包、来源地址；结果 |
| 账号注销（Account Logoff） | `account/logoff` | 同一登录会话身份；注销时间；注销后会话状态；结果 |

### 5.4 网络活动（Network Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| TCP 连接（TCP Connection） | `network/tcp_connect` | Actor；方向；本地/远端 IP、端口和地址族；连接结果 |
| UDP 连接（UDP Connection） | `network/udp_connect` | Actor；本地/远端端点；发送/接收结果 |
| URL 访问（URL） | `network/url_access` | Actor；完整 URL、scheme、host、port、path、method、状态码、nonce |
| DNS 查询（DNS Query） | `network/dns_query` | Actor；问题名、Query Type、答案、解析器；查询结果 |
| 文件下载（File Downloaded） | `network/file_download` | URL/HTTP 信息；落地绝对路径；大小和 hash；下载结果 |

### 5.5 哈希算法（Hash Algorithms）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| MD5 哈希（MD5） | `hash/md5` | 文件绝对路径、大小、算法名、32 位十六进制摘要、计算结果 |
| SHA 哈希（SHA） | `hash/sha` | 文件绝对路径、大小、实际算法（SHA-1/SHA-256/SHA-512）、与算法位数一致的摘要、计算结果 |
| 导入表哈希（IMPHASH） | `hash/imphash` | PE 文件路径、大小、IMPHASH、解析结果 |

### 5.6 注册表活动（Registry Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 键/值创建（Key/Value Creation） | `registry/create` | Actor；Hive、Key、Value Name/Type；32/64 位视图；前后值；错误码 |
| 键/值修改（Key/Value Modification） | `registry/modify` | 同上；修改前后数据或数据 hash |
| 键/值删除（Key/Value Deletion） | `registry/delete` | 同上；删除前存在、删除后不存在 |

### 5.7 计划任务活动（Schedule Task Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 计划任务创建（Scheduled Task Creation） | `scheduled_task/create` | Actor；Task Path；XML SHA-256；Principal、Actions、Triggers、Enabled；前后状态 |
| 计划任务修改（Scheduled Task Modification） | `scheduled_task/modify` | 同上；旧/新定义 hash 和变更字段 |
| 计划任务删除（Scheduled Task Deletion） | `scheduled_task/delete` | Task Path；删除前定义；删除后不存在；结果 |

### 5.8 服务活动（Service Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 服务创建（Service Creation） | `service/create` | Actor；Service Name/Display Name；Binary Path、Start Type、Account、Service Type；前后状态 |
| 服务修改（Service Modification） | `service/modify` | 同上；修改前后配置 |
| 服务删除（Service Deletion） | `service/delete` | 服务名；删除前配置；删除后不存在；结果 |

### 5.9 驱动/模块活动（Driver/Module Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 驱动加载（Driver Loaded） | `driver/load` | Actor；驱动/服务名；镜像路径、基址、大小、hash、签名；加载前后状态 |
| 驱动修改（Driver Modification） | `driver/modify` | 驱动身份；文件或服务配置变更前后状态；结果 |
| 驱动卸载（Driver Unloaded） | `driver/unload` | 驱动身份；卸载前已加载、卸载后未加载；结果 |

### 5.10 设备操作（Device Operations）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 虚拟磁盘挂载（Virtual Disk Mount） | `device/virtual_disk_mount` | 镜像路径；Device Instance、Volume GUID、盘符、挂载点；前后状态 |
| USB 设备卸载（USB Device Unmount） | `device/usb_unmount` | Instance ID、Class GUID、VID/PID、序列号、Volume；前后状态 |
| USB 设备挂载（USB Device Mount） | `device/usb_mount` | 同上；挂载点和盘符；前后状态 |

### 5.11 其他相关事件（Other Relevant Events）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 组策略修改（Group Policy Modification） | `group_policy/modify` | Actor；Computer/User Scope；Policy Path/Name；后端注册表路径；前后值；结果 |

### 5.12 命名管道活动（Named Pipe Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 管道创建（Pipe Creation） | `named_pipe/create` | 完整 `\\.\pipe\...` 名；Server PID；方向、模拟级别；结果 |
| 管道连接（Pipe Connection） | `named_pipe/connect` | 管道名；Server/Client PID；连接时间；结果 |

### 5.13 EDR 系统运维活动（EDR SysOps）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| Agent 启动（Agent Start） | `edr_sysops/agent_start` | 产品、版本、服务/进程身份；启动前后状态；结果 |
| Agent 停止（Agent Stop） | `edr_sysops/agent_stop` | 同上；停止前后状态；结果 |
| Agent 安装（Agent Install） | `edr_sysops/agent_install` | 安装器路径/hash；版本；安装前后状态；退出码 |
| Agent 卸载（Agent Uninstall） | `edr_sysops/agent_uninstall` | 产品/版本；卸载前后状态；退出码 |
| Agent 心跳保活（Agent Keep-Alive） | `edr_sysops/agent_keep_alive` | Agent 身份；心跳序号、时间、前后存活状态 |
| Agent 异常错误（Agent Errors） | `edr_sysops/agent_error` | Agent 身份；错误码和脱敏摘要；错误前后状态 |

EDR SysOps 不等于对腾讯后台进行 API 接入。L3 变更必须使用隔离 VM、快照和显式审批；默认构建不应提供可在生产环境执行的停止、卸载能力。

### 5.14 WMI 活动（WMI Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| WMI 事件消费者与过滤器绑定（WmiEventConsumerToFilter） | `wmi/consumer_filter_bind` | Actor；Namespace；Binding、Filter、Consumer 路径；前后状态 |
| WMI 事件消费者（WmiEventConsumer） | `wmi/consumer` | Namespace、对象名/类；命令模板或脚本 hash；前后状态 |
| WMI 事件过滤器（WmiEventFilter） | `wmi/filter` | Namespace、Filter 名、WQL Query；前后状态 |

### 5.15 BITS 后台传输任务活动（BIT JOBS Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| BITS 任务活动（BIT JOBS Activity） | `bits/job` | Actor；Job ID/Name/Type/State/Owner；远端 URL、本地路径、字节数、通知命令；前后状态 |

### 5.16 PowerShell 活动（PowerShell Activity）

| 能力 | `event_type/action` | 必须收集 |
| --- | --- | --- |
| 脚本块活动（Script-Block Activity） | `powershell/script_block` | Actor；Script Block/Runspace/Pipeline ID；Engine/Host；脚本路径、文本 SHA-256、命令行；结果 |

脚本文本默认只保存测试框架自行生成的无敏感内容。对于任意外部脚本，默认只保存 SHA-256 和脱敏摘要。

## 6. 采集来源与可信度

建议的来源优先级：

| 可信度 | 来源示例 | 用法 |
| --- | --- | --- |
| `high` | Win32 API 返回 + Controller 独立句柄/状态查询 | LOCAL_PASS 的主要证据 |
| `high` | 文件/注册表/服务/WMI/BITS 对象的前后读取 | 状态型行为证据 |
| `medium` | 本地 ETW、Windows Event Log、WMI 事件 | 辅助时间和字段，必须记录丢失计数 |
| `medium` | 受控 Helper 的服务端回执 | 网络、管道和下载证据 |
| `low` | Actor stdout 自报、进程列表轮询、文本日志 | 诊断，不可单独形成 LOCAL_PASS |

Actor/Target/Helper 只返回小型版本化消息。Controller 必须核对消息中的 PID、nonce 和句柄结果后再写 SQLite。

## 7. 云端关联策略

### 7.1 通用强锚点

- `run_id + case_run_id + nonce`；
- 唯一工件绝对路径；
- Actor/Target EXE 绝对路径；
- 含 nonce 的完整命令行、URL、DNS 名、任务名、服务名、注册表路径、管道名等。

### 7.2 中锚点

- PID + 进程开始时间；
- Actor PID + Target PID + 父子/访问关系；
- 文件 hash + 大小；
- 本地/远端网络四元组；
- SID + Session ID + 时间窗。

### 7.3 弱锚点

- 文件名、进程名、主机名、宽时间窗。

PID、文件名或主机名均不得单独形成 PASS。若云端找不到 nonce，Compare 可以组合多个中锚点；候选先按锚点得分、再按与本地行为时间的距离排序，得分和时间距离仍相同时必须返回 `INCONCLUSIVE`。验证结果保留每条候选的规范化字段与 EDR 原始完整日志，供人工复核。

## 8. 时间与顺序

- 所有持久化时间使用带 `Z` 的 UTC RFC 3339；
- 每条事件同时记录本能力开始后的 `monotonic_offset_ms`；
- 程序和事件都记录序号，不能依赖 JSON 数组当前顺序；
- Windows 文件时间或 Unix ms 必须在采集边界转换，保留原单位只放在诊断扩展；
- 云端 `@collection - Common.EventTime` 只能称为“记录内采集时差”，不是用户可见延迟。

## 9. 数据最小化与脱敏

- 不保存口令、令牌、Cookie、私钥、完整内存内容；
- 命令行和 URL 中的秘密参数在写库前脱敏；nonce 不属于秘密；
- 用户名可以默认脱敏，但 SID、域和主机匹配策略必须保持确定；
- PowerShell/WMI 脚本优先保存 hash；
- Agent 错误只保存错误码和受控摘要；
- 标记为 `sensitive=true` 的工件不得进入 Git，也不得由前端上传；
- `reference/`、`runs/`、导入云端 JSON 和本地数据库继续保持不追踪。

## 10. 实现验收

一个能力的本地采集实现至少通过以下检查：

1. JSON 通过 `schemas/run-export.schema.json`；
2. `event_type/event_action` 与 `data.kind/operation` 一致；
3. 所有 ID 引用存在，计数与 `integrity.record_counts` 一致；
4. Controller、Actor 至少各有一个程序实例；需要目标对象时存在 Target；
5. nonce 出现在能力、事件和至少一个可由 EDR 采集的行为载体中；
6. API 返回和独立观测同时存在；
7. observation window 覆盖行为时间，丢失计数已记录；
8. 所有本地 BASELINE 字段都能从 programs 或 local_facts 解析；
9. 清理完成并有前后证据；
10. 数据库、Schema 和证据工件 hash 可复核。
