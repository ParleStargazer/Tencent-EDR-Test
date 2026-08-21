# BITS 后台传输任务活动测试样本

## 1. 能力边界

能力 ID 为 `win.bits.job`，验证 EDR 是否能把一次操作识别为 BITS Job 活动，而不只是分别记录到普通进程、网络连接或文件写入。

当前已知腾讯 EDR 字段全集和历史运行日志中没有 BITS 专属表、动作或 Job 字段，因此当前产品的云端结论预期为未匹配。普通 `NetworkEvents`、`FileEvents` 或 `ProcessCreate(bitsadmin.exe)` 只能作为侧面线索，不能使本能力通过。

## 2. 两种独立测试方法

| 方法 | 创建入口 | 云端预期发起进程 | 本地直接证据 |
| --- | --- | --- | --- |
| `bitsadmin` | `bitsadmin.exe /create`、`/addfile`、`/resume`、`/complete` | `bitsadmin.exe` | 命令进程 PID/路径/命令行/退出码；COM 重开 Job；下载文件哈希 |
| `com_api` | `IBackgroundCopyManager::CreateJob` 与 `IBackgroundCopyJob` | `BitsJob.Actor.exe` | COM 返回 Job ID；COM 重开 Job；下载文件哈希 |

两种方法均使用进程内 `TcpListener` 提供的 `127.0.0.1` HTTP 服务，只下载框架生成的 nonce JSON，不访问互联网，也不设置 BITS 通知命令。

## 3. 本地绝对基准

Actor 创建下载 Job 并添加文件后保持 `SUSPENDED`。Controller 在放行前使用独立的 BackgroundCopyManager COM 实例按 Job ID 重新打开任务，并枚举第一个 `IBackgroundCopyFile`，必须验证：

- Job ID、显示名称、下载类型、所有者 SID；
- `SUSPENDED` 状态；
- 远端回环 URL和运行目录内的本地路径；
- Actor 与 bitsadmin 发起进程身份；
- 放行后达到 `TRANSFERRED`，进度字节数等于 payload 大小；
- `Complete` 后 Job 从队列移除；
- 下载文件内容和 SHA-256 与本地 payload 完全一致。

任一子测试失败都会使能力本地结论为 `SAMPLE_ERROR`。异常路径和 Controller 清理阶段都会按精确 Job ID 尝试取消遗留任务。

## 4. 云端映射规划

Canonical 字段位于 `bits.*`：

- `job_id`、`display_name`、`job_type`、`state`、`owner_sid`；
- `remote_url`、`local_path`；
- `bytes_total`、`bytes_transferred`；
- `notification_command`。

腾讯规划路由只接受未来可能出现的 `BitsEvents`、`BITSEvents` 或 `BackgroundTransferEvents` 专属表，并兼容 `Child.BitsJob*` 与通用 `Child.Job*` 字段名。该路由是字段合同，不表示当前产品已经提供这些字段。

BASSLINE 对两种方法分别建立候选，Job ID、显示名称、URL 和本地路径为强锚点，发起程序为中等锚点，强关联时间仍为 15 ms。能力默认采用结果最好的方法形成云端结论，但两种方法均完整展示。

## 5. 安全与运行条件

- 风险等级：`L1`。
- 默认权限：标准用户；BITS 服务可按需启动。
- 网络：仅 IPv4 回环，不需要外网。
- 清理：完成任务或按 Job ID 取消，绝不调用 `bitsadmin /reset`。
- 不使用通知命令、凭据、代理、上传任务或系统范围任务枚举。

接口调用顺序以 Microsoft Learn 的 [IBackgroundCopyJob](https://learn.microsoft.com/windows/win32/api/bits/nn-bits-ibackgroundcopyjob)、[CreateJob](https://learn.microsoft.com/windows/win32/api/bits/nf-bits-ibackgroundcopymanager-createjob) 和 [bitsadmin](https://learn.microsoft.com/windows-server/administration/windows-commands/bitsadmin) 文档为准。
