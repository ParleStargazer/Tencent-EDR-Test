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
