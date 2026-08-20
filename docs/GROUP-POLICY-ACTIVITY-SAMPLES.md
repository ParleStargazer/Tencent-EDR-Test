# 组策略修改能力样本

## 能力与安全边界

`win.group_policy.modify@0.3.0` 由 Controller 和 Actor 组成，需要管理员权限和 L2 高风险确认。同轮串行执行两种方法：

- `isolated_policy_key`：创建 `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\EdrTest\Runs\<nonce>\ValidationMarker` 控制组，写入 `BEFORE/AFTER` 标记，随后精确删除本轮键；
- `known_policy_same_value`：优先选择当前机器已经存在的白名单策略值，以相同原生类型、相同原始字节调用 `RegSetValueExW` 写回；若均不存在，Controller 临时预置参考日志确认可识别的安全增强值 `EnableSmartScreen=1`，Actor 随后执行同值回写。

两种方法都在 `RegSetValueExW` 前后记录 UTC 时间、原生类型、原始数据长度和 SHA-256。Actor 自检后，Controller 使用独立句柄再次读取。原值存在时清理阶段只验证其完全不变；使用安全兜底时只在当前值仍与预置 DWORD `1` 完全一致的情况下删除该值，并在原键也不存在且仍为空时删除空键。若发现并发变化，样本报告 `CLEANUP_ERROR`，不会删除或覆盖可能合法的新策略。

`known_policy_target` 默认是 `auto`，也可在参数 JSON 中指定白名单 ID。路径和值名同时在 Controller 和 Actor 中硬编码校验，不能通过命令行传入任意注册表位置。目前白名单来自参考导出中确认存在的 Windows System、SmartScreen 和 Defender 策略项。

## EDR BASELINE

腾讯参考日志确认事件位于 `RegEvents`，`Action.Name=RegSetValue`，主要字段如下：

- `Child.RegKeyPath`、`Child.RegValName`；
- `Child.RegOldValData`、`Child.RegValData`；
- `Child.RegOldValType`、`Child.RegValType`；
- `Child.RegGroupName=组策略`；
- `Common.MonitorName=组策略`；
- `Parent.ProcPid`、`Parent.FilePath`、`Parent.ProcCmdline`；
- `Common.EventTime`。

本地结果是绝对基准。云端候选先按键、值、Actor 和时间召回，15 ms 以内作为强时间证据；默认 `Action.Name=RegSetValue` 只在候选命中后消歧，不改变本地规则。比较页分别展示隔离控制组和 L2 同值回写，采用检出情况最佳的方法形成结论。

## 构建与验证

```powershell
pwsh -NoProfile -File scripts/Build-GroupPolicyActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-GroupPolicyActivitySamples.ps1
```

标准用户可以构建能力包，但 Runner 会以 `ADMINISTRATOR_REQUIRED` 安全跳过实际行为。直接使用 CLI 时必须附加 `--allow-high-risk`：

```powershell
dotnet src/EdrTest/bin/Release/net8.0-windows/EdrTest.dll run `
  --manifest samples/win.group_policy.modify/capability.json `
  --allow-high-risk
```

`known_policy_target=auto` 时，即使测试机没有现存白名单值，L2 方法也会通过 `EnableSmartScreen=1` 安全兜底实际执行，并在 Actor 退出后恢复测试前状态。显式指定其他不存在的白名单项时仍不会擅自创建该项。

## 260820092000run 调研结论

本轮 EDR 导出能够关联到 `GroupPolicyModify.Controller.exe` 和 `GroupPolicyModify.Actor.exe`，包括进程创建、运行时诊断管道、结果 JSON 写入等事件，证明测试进程本身已进入导出范围。Actor 的整个可见执行区间没有 `RegEvents`，导出中也没有 `ValidationMarker` 或 `SOFTWARE\Policies\EdrTest\Runs`。

本目录的 `local.json` 与 `edr.json` 文件大小和 SHA-256 完全相同，实际没有保存本轮 `local-run.json`，因此不能从该目录复核 API 前后时间戳和本地事实。后续复测应同时保留真正的本地导出；本次判断依据是 EDR 中完整可见的测试进程区间和组策略旁证日志，不把缺失的本地文件当作证据。

旁证文件 `edr_group_policy_reg_relate.json` 的 469 条记录全部为 `RegEvents / RegSetValue / Child.RegGroupName=组策略`，但时间范围约早于本轮 7 天；键集中在 Windows Defender、Windows System 和 Software Restriction Policies 等已知敏感策略位置。由此更符合以下判断：腾讯 EDR 的“组策略”事件按受关注的真实策略键/值规则采集或分类，并不会因为注册表路径位于 `SOFTWARE\Policies` 就采集任意自定义测试键。当前样本能够证明 Windows 注册表写入成功，但其隔离键没有真实策略语义，不能稳定触发该产品的组策略遥测。

## 已实现的修复方案

已新增独立的 `known_policy_same_value` 子测试，并保留隔离键方法作为本地写入控制组：

1. 仅允许从显式白名单选择参考日志已证实受监控的真实策略键和值名；默认优先选择测试机上已经存在的项。
2. 读取并保存键是否存在、值类型和原始数据，然后用 `RegSetValueExW` 原样写回同一类型、同一数据。该操作能够产生真实策略路径上的写调用，同时不改变策略的有效值。
3. `auto` 找不到现存值时，仅允许临时创建 `Windows System\EnableSmartScreen` DWORD `1`；该值用于启用而不是削弱 SmartScreen，并在采证后精确删除。不会创建 Defender 禁用项、软件限制策略或其他安全配置。
4. 写入后立即独立复核值、记录 API 前后时间和 Actor 身份；清理阶段核对已有值未变化，或确认兜底值仍是本轮预置内容后恢复“不存在”状态。任何类型、数据或并发状态差异都使本地测试失败并告警。
5. BASELINE 按方法分别关联，继续以本地路径、值名、Actor 和时间为基准；`Child.RegGroupName=组策略`、`Common.MonitorName=组策略` 作为 EDR 必需字段。只有该真实策略方法可以作为腾讯产品的能力结论，隔离键方法只提供本地控制证据。

能力包和 BASELINE 风险等级均已提升为 L2；前端必须勾选高风险确认，CLI 必须传入 `--allow-high-risk`。
