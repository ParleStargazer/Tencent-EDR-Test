# 计划任务活动测试样本设计

## 目标与范围

本能力包验证计划任务创建、修改、删除三项行为，每项能力均包含 Task Scheduler COM 与系统 `schtasks.exe` 两个独立方法。比较页面分别显示两种方法并采用通过情况最好的方法作为能力结论。创建的直接语义事件为 4698，修改为 4702，删除为 4699；删除的 `SchedTaskDelete` 尚无真实样本，须等待实测校准。

## 260814140000run 结论

- 原 COM 样本已被检测：`ServiceEvents / InjectHook / RpcSchedTaskCreate` 与本地任务路径、Actor PID 完全一致，EDR 时间晚于本地完成时间约 27 ms。
- `RpcSchedTaskCreate` 表示 `RegisterTaskDefinition` 注册任务的 RPC Hook。创建和更新都可能使用同一个动作名，因此规范化动作是 `register`，不能仅凭该字段区分创建与修改语义。
- `SchedTaskCreate` 来自另一条 `ScheduleTaskEvents / WinEventLog / 4698` 链。当前测试主机的导出没有任何 `ScheduleTaskEvents`；同目录提供的 560 条相关记录均来自其他主机。
- Windows 是否生成 4698 受“审核其他对象访问事件（Audit Other Object Access Events）”策略控制。仅凭 EDR 导出无法区分“本机未生成 4698”和“EDR 未接入/导出本机 4698”，因此方法 2 通过语言无关的子类别 GUID `{0CCE9227-69AE-11D9-BED3-505054503030}` 保存 `auditpol` 状态，并保存本机 Security 日志查询结果。

完整分析保存在 `reference/EDR能力细节.txt`。

## 安全模型

- 每个方法只使用根目录下的唯一任务路径 `\EdrTest_<nonce>_<operation>_<method>`，不枚举、覆盖或删除其他任务。
- COM 方法创建默认禁用且无触发器的任务。
- `schtasks.exe` 创建方法启用任务，以贴近常见 4698 样本；任务只有一年后的单次时间触发器，动作是系统 `cmd.exe` 的无害 `rem`。修改方法对无触发器的禁用任务使用 `/Change /ENABLE`，避免 `/TR` 触发运行身份密码交互；删除方法使用 `/Delete`。测试过程绝不启动任务并在采证后立即精确清理。
- Controller 独立查询 Task Scheduler 2.0 COM 服务，清理只删除精确任务路径并再次确认不存在。
- 样本只读取审计策略与 Security 日志，不会自动修改本机审计策略。

## 行为与云端主证据

| 能力/方法 | Actor 行为 | 本地绝对结论 | 云端主证据 |
|---|---|---|---|
| 创建 / `task_scheduler_com` | COM `RegisterTask` | 前不存在、后存在、任务禁用、XML/主体/动作完整 | `ServiceEvents`、`InjectHook`、`RpcSchedTaskCreate`、任务路径、客户端 PID |
| 创建 / `schtasks_cli` | 系统 `schtasks.exe /Create /SC ONCE` | 前不存在、后存在、任务启用、包含未来 `TimeTrigger`；同时记录本机 4698 诊断 | `ScheduleTaskEvents`、`WinEventLog`、4698、任务路径、任务 XML 中动作标记 |
| 修改 / `task_scheduler_com` | COM `RegisterTaskDefinition(TASK_UPDATE)` | 前后均存在、XML 哈希不同、修改后标记可读 | `ServiceEvents`、`RpcSchedTaskCreate` 注册 RPC、任务路径、客户端 PID；它只证明注册调用，不单独证明修改语义 |
| 修改 / `schtasks_cli` | 系统 `schtasks.exe /Change /ENABLE` | 前后均存在、XML 哈希不同、由禁用变为启用、动作保持不变且任务无触发器；同时记录本机 4702 诊断 | `ScheduleTaskEvents`、`SchedTaskUpdate`、4702、任务路径、`Child.TaskContentNew` 中唯一预置标记 |
| 删除 / `task_scheduler_com` | COM 删除精确任务路径 | 前存在、后不存在 | 4699、任务路径；Action.Name 暂定 `SchedTaskDelete` |
| 删除 / `schtasks_cli` | 系统 `schtasks.exe /Delete /TN ... /F` | 前存在、后不存在；同时记录本机 4699 诊断 | 4699、任务路径；Action.Name 暂定 `SchedTaskDelete` |

修改和删除的预置创建可能同时产生创建事件，因此比较器仍要求目标事件的行为语义与 EventLog ID。Action.Name 是 EDR 侧可选消歧规则，不参与也不改变本地基准。创建默认值为 `SchedTaskCreate, RpcSchedTaskCreate`，修改默认值为 `SchedTaskUpdate, RpcSchedTaskCreate`，以免全局筛选提前排除任一方法；删除仍为 `SchedTaskDelete`。

## 字段映射与关联

- 4698/4702/4699：`Child.TaskName`（兼容 `Child.NodeName`）→ `scheduled_task.name`
- 4698/4702：`Child.TaskContent` / `Child.TaskContentNew` → `scheduled_task.content`
- RPC Hook：`Child.TaskName` → `scheduled_task.name`，`Child.NodeName`/`Child.FilePath` → `scheduled_task.command`，`Child.TaskArg` → `scheduled_task.arguments`
- `Action.EventLogId` → `winlog.event_id`
- `Child.Subject*` → `user.*`
- `Parent.*` → `process.*`

任务路径是强锚点，Actor PID/路径及 15 ms 时间差是强/中等证据。真实 WinEventLog 可能只保留 `svchost.exe`，因此它能通过推荐项，但页面会提示“EDR 仅保留 Task Scheduler 服务侧调用链，需要补充客户端调用链”。CLI 方法分别只读查询本机 4698/4702/4699；若本机找到对应事件而云端没有直接语义事件，结论应指向 EDR 接入或导出链，若本机也没有，则先检查审计策略。

## 构建与验证

```powershell
pwsh -NoProfile -File .\scripts\Build-ScheduledTaskActivitySamples.ps1
pwsh -NoProfile -File .\scripts\Test-ScheduledTaskActivitySamples.ps1
```

字段清单与脱敏示例见 `docs/reference/tencent-edr-scheduled-task-events-field-profile.json`。
