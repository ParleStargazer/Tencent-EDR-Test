# 计划任务活动测试样本设计

## 目标与范围

本能力包验证计划任务创建、修改、删除三项行为。EDR 云端证据来自 Windows 安全事件日志：创建 4698、修改 4702、删除 4699。腾讯 EDR 已有创建与修改真实导出；删除按 `SchedTaskDelete` 假定支持，必须等待测试机实测，不能使用创建或修改事件替代删除结论。

## 安全模型

- 每轮只使用根目录下的唯一任务路径 `\EdrTest_<nonce>_<operation>`，不枚举、覆盖或删除其他任务。
- 任务绑定当前交互用户、最低权限、默认禁用、禁止按需启动且没有触发器；样本从不启动任务。
- 动作仅指向系统 `cmd.exe` 的无害退出或注释参数，即使用户手工解除禁用也不产生持久化或外部影响。
- Controller 在 Actor 前后都独立查询 Task Scheduler 2.0 COM 服务，清理只删除精确任务路径并再次确认不存在。

## 三项行为

| 能力 | Controller 预置 | Actor 行为 | 本地绝对结论 | 云端主证据 |
|---|---|---|---|---|
| 创建 | 确认任务不存在 | `TASK_CREATE` 注册禁用任务 | 前不存在、后存在、XML/主体/动作/禁用状态完整 | 4698、任务路径、`Child.TaskContent` 中 nonce |
| 修改 | 创建 `BEFORE` 定义 | `TASK_UPDATE` 更新描述和动作参数 | 前后均存在、XML 哈希不同、`AFTER` 标记可读 | 4702、任务路径、`Child.TaskContentNew` 中 nonce |
| 删除 | 创建 `BEFORE` 定义 | 删除精确任务路径 | 前存在、后不存在 | 4699、任务路径；Action.Name 假定 `SchedTaskDelete` |

修改和删除的预置创建可能同时产生 4698，因此比较器仍要求目标事件的行为语义与 EventLog ID。Action.Name 是 EDR 侧可选消歧规则，不参与也不改变本地基准。

## 字段映射与关联

- `Child.TaskName`，兼容 `Child.NodeName` → `scheduled_task.name`
- 创建 `Child.TaskContent`、修改优先 `Child.TaskContentNew` → `scheduled_task.content`
- `Action.EventLogId` → `winlog.event_id`
- `Child.Subject*` → `user.*`
- `Parent.*` → `process.*`

任务路径是强锚点；XML 中的本轮 nonce 是必需断言；Actor PID/路径是中等锚点。真实修改日志可能只保留 `svchost.exe`，因此它能通过推荐项，但页面会提示“EDR 仅保留 Task Scheduler 服务侧调用链，需要补充客户端调用链”。强关联时间默认 15 ms，低置信候选仍受用户配置的无关联事件时间上限约束。

## 构建与验证

```powershell
pwsh -NoProfile -File .\scripts\Build-ScheduledTaskActivitySamples.ps1
pwsh -NoProfile -File .\scripts\Test-ScheduledTaskActivitySamples.ps1
```

字段清单与脱敏示例见 `docs/reference/tencent-edr-scheduled-task-events-field-profile.json`。
