# 组策略修改能力样本

## 能力与安全边界

`win.group_policy.modify@0.1.0` 由 Controller 和 Actor 组成，需要管理员权限。测试只创建
`HKEY_LOCAL_MACHINE\SOFTWARE\Policies\EdrTest\Runs\<nonce>\ValidationMarker`，值类型固定为字符串，内容只包含本轮 nonce 与 `BEFORE/AFTER` 标记。该路径没有 Windows 策略语义；样本禁止访问 `EdrTest\Runs` 之外的键，并在采证后只删除本轮 32 位十六进制 nonce 子键。

Controller 先确认冲突键不存在，预置 `BEFORE` 值；Actor 紧邻 `RegSetValueExW` 前记录 UTC 时间并修改为 `AFTER`。Controller 读取原子结果协议后使用 64 位注册表视图独立复核，再将 PID、程序路径、命令行、键路径、值名/类型、新旧值和清理结果写入 SQLite。

## EDR BASELINE

腾讯参考日志确认事件位于 `RegEvents`，`Action.Name=RegSetValue`，主要字段如下：

- `Child.RegKeyPath`、`Child.RegValName`；
- `Child.RegOldValData`、`Child.RegValData`；
- `Child.RegOldValType`、`Child.RegValType`；
- `Child.RegGroupName=组策略`；
- `Parent.ProcPid`、`Parent.FilePath`、`Parent.ProcCmdline`；
- `Common.EventTime`。

本地结果是绝对基准。云端候选先按键、值、Actor 和时间召回，15 ms 以内作为强时间证据；默认 `Action.Name=RegSetValue` 只在候选命中后消歧，不改变本地规则。

## 构建与验证

```powershell
pwsh -NoProfile -File scripts/Build-GroupPolicyActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-GroupPolicyActivitySamples.ps1
```

标准用户可以构建能力包，但 Runner 会以 `ADMINISTRATOR_REQUIRED` 安全跳过实际行为。

## 260820092000run 调研结论

本轮 EDR 导出能够关联到 `GroupPolicyModify.Controller.exe` 和 `GroupPolicyModify.Actor.exe`，包括进程创建、运行时诊断管道、结果 JSON 写入等事件，证明测试进程本身已进入导出范围。Actor 的整个可见执行区间没有 `RegEvents`，导出中也没有 `ValidationMarker` 或 `SOFTWARE\Policies\EdrTest\Runs`。

本目录的 `local.json` 与 `edr.json` 文件大小和 SHA-256 完全相同，实际没有保存本轮 `local-run.json`，因此不能从该目录复核 API 前后时间戳和本地事实。后续复测应同时保留真正的本地导出；本次判断依据是 EDR 中完整可见的测试进程区间和组策略旁证日志，不把缺失的本地文件当作证据。

旁证文件 `edr_group_policy_reg_relate.json` 的 469 条记录全部为 `RegEvents / RegSetValue / Child.RegGroupName=组策略`，但时间范围约早于本轮 7 天；键集中在 Windows Defender、Windows System 和 Software Restriction Policies 等已知敏感策略位置。由此更符合以下判断：腾讯 EDR 的“组策略”事件按受关注的真实策略键/值规则采集或分类，并不会因为注册表路径位于 `SOFTWARE\Policies` 就采集任意自定义测试键。当前样本能够证明 Windows 注册表写入成功，但其隔离键没有真实策略语义，不能稳定触发该产品的组策略遥测。

## 后续修复方案（本次未实施）

新增独立的 `known_policy_same_value` 子测试，保留现有隔离键方法作为本地写入控制组：

1. 仅允许从显式白名单选择参考日志已证实受监控的真实策略键和值名；默认优先选择测试机上已经存在的项，不自动创建或猜测安全策略。
2. 读取并保存键是否存在、值类型和原始数据，然后用 `RegSetValueExW` 原样写回同一类型、同一数据。该操作能够产生真实策略路径上的写调用，同时不改变策略的有效值。
3. 若目标值不存在，则将子测试标记为 `NOT_APPLICABLE`，不为了触发遥测创建 Defender、软件限制策略等安全配置。
4. 写入后立即独立复核值、记录 API 前后时间和 Actor 身份；清理阶段再次核对值未变化。任何类型或数据差异都使本地测试失败并告警。
5. BASELINE 按方法分别关联，继续以本地路径、值名、Actor 和时间为基准；`Child.RegGroupName=组策略`、`Common.MonitorName=组策略` 作为 EDR 必需字段。只有该真实策略方法可以作为腾讯产品的能力结论，隔离键方法只提供本地控制证据。

该方案涉及真实安全策略位置，应保持管理员门禁，并把风险等级提升到至少 L2；在隔离测试机上完成键选择和回滚演练后再实现。
