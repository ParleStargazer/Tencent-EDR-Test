# 计划任务活动测试样本

## 设计依据

`reference/scheduled_task/edr_SchedTaskCreate.json` 的 105 条事件全部是 `ScheduleTaskEvents / WinEventLog / 4698 / SchedTaskCreate`；`edr_SchedTaskUpdate.json` 的 887 条事件全部是 `ScheduleTaskEvents / WinEventLog / 4702 / SchedTaskUpdate`。创建事件的已知客户端包含 `schtasks.exe /Create /XML`，两类事件都提供完整任务路径；创建 XML 位于 `Child.TaskContent`，更新后 XML 位于 `Child.TaskContentNew`。`Parent.*` 可能表示直接客户端，也可能是 Task Scheduler 或其他系统服务侧进程，不能作为必需语义证据。

`reference/260814140000run` 已证明 COM 注册可以命中 `ServiceEvents / InjectHook / RpcSchedTaskCreate`。该动作同时承载创建与更新注册调用，不能单独区分语义，因此继续作为独立 RPC 方法保留，但不会替代 4698/4702 直接语义子测试。

此前的非 RPC 子测试只读取 `auditpol` 状态，然后执行普通 `schtasks.exe` 命令。即使系统没有启用“其他对象访问事件”成功审核，命令和任务状态仍会成功，本地也不会因缺少 4698/4702 而失败。这会产生“行为成功、EDR 没有可采集源”的假阳性。

## 两类独立方法

每项能力保留 Task Scheduler COM 方法，并增加各自独立的安全审计方法：

| 能力 | 方法 ID | 激发操作 | 本机直接证据 | 腾讯动作 |
|---|---|---|---|---|
| 创建 | `security_audit_create` | `schtasks.exe /Create /TN <唯一任务> /XML <定义> /F` | Security 4698 | `SchedTaskCreate` |
| 修改 | `security_audit_update` | 预置禁用任务后，以 `/Create /XML /F` 覆盖完整定义 | Security 4702 | `SchedTaskUpdate` |
| 删除 | `security_audit_delete` | 预置任务后执行 `schtasks.exe /Delete /F` | Security 4699 | 腾讯侧尚未实现 |

安全审计方法通过原生 Audit Policy API 精确保存“其他对象访问事件”子类别的位掩码，仅在行为窗口内补充成功审核位，并在读取证据后恢复原值。它不解析本地化的 `auditpol.exe` 文本，也不备份或覆盖其他审核子类别。三项能力因此需要管理员权限并标记为 L2。

Actor 只有同时满足以下条件才报告本地成功：

1. 计划任务操作前后状态符合创建、修改或删除语义；
2. Security 日志出现本轮唯一任务路径对应的 4698、4702 或 4699；
3. 事件记录号与事件自身 UTC 时间已提取；
4. 审核策略恢复为操作前的精确位掩码。

## BASELINE 与关联

本地运行日志仍是绝对基准。创建与修改的云端必需条件是任务路径、任务 XML 中的本轮唯一标记和 Windows 事件 ID。关联时间使用本机 Security 事件自身的 `System/TimeCreated/@SystemTime`，而不是 `schtasks.exe` 退出时间，因此可继续使用 15 ms 强关联阈值。进程路径和命令行只作推荐证据，避免 EDR 仅保留服务侧调用链时误判。

腾讯 EDR 当前没有可验证的 `SchedTaskDelete` 导出样本。删除能力仍执行与创建、修改相同完整度的本地激发、自验证、证据保存和策略恢复；腾讯云端要求保留为规划项，真实离线比较预计显示未通过，不作为本轮样本实现验收目标。

## 安全与清理

- 每个方法使用 `\EdrTest_<nonce>_<operation>_<method>` 唯一路径，拒绝操作该命名范围外的任务。
- 任务动作只包含不会被调度执行的 `cmd.exe /c rem` 标记，安全审计创建与更新使用一年后的时间触发器。
- Controller 在每个方法结束后精确删除对应任务；审核策略由 Actor 在退出前恢复。
- 审核策略设置、实际启用值、恢复值、事件 XML、事件时间和记录号均写入本地事实或证据文件，便于前端展开排查。
- Security 事件 XML 可能包含当前账号、域和 SID，对应证据文件标记为敏感；导出或共享运行结果前应按平台规范脱敏。

## 构建与验证

```powershell
pwsh -NoProfile -File .\scripts\Build-ScheduledTaskActivitySamples.ps1
# 需要管理员权限；会执行三项本地安全审计子测试及合成云端比较回归
pwsh -NoProfile -File .\scripts\Test-ScheduledTaskActivitySamples.ps1
```

脱敏字段清单与采集统计见 `docs/reference/tencent-edr-scheduled-task-events-field-profile.json`。
