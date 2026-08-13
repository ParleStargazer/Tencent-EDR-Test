# Registry Activity 三项能力样本

## 1. 真实导出结论

本轮以 `reference/reg/edr_reg_relate.json` 为校准依据。文件 SHA-256 为
`b86c8f69ebfdec08e3cf31970af956485fd0fcd4aac1227404b9a9d5821fb37e`，包含 2,168 条、135 个字段的腾讯 EDR 注册表记录。

关键发现：

- 全部记录均为 `@table = RegEvents`、`Action.Name = RegSetValue`；
- 全部记录都有 `Child.RegGroupName`，并被归类为系统服务、组策略、应用卸载设置、浏览器设置、文件关联、启动项等重点区域；
- 路径、分组、新旧值类型、Parent PID/路径/命令行及毫秒事件时间均为 100% 存在；
- `Child.RegOldValType = 0` 的 1,958 条记录没有旧值，可作为新值创建的强证据；旧类型非 0 的 110 条记录全部有旧值，可与本地修改前值直接比较；
- 当前隔离样本写入 `HKCU\Software\EdrTest\...`，不属于导出中任何重点分组。这说明未采集的主要原因更可能是产品侧重点路径过滤，而不是本地调用失败；同时换用 Win32 API 可消除 .NET 封装调用方式的剩余不确定性。

可复用的字段统计见 [tencent-edr-reg-events-field-profile.json](reference/tencent-edr-reg-events-field-profile.json)。

## 2. 双方法测试设计

三个能力均在同轮运行以下两个独立子测试，并在前端分别展示结果：

| 方法 ID | 中文名称 | 路径与调用方式 |
| --- | --- | --- |
| `isolated_key` | 隔离 HKCU 键（.NET API） | `HKCU\Software\EdrTest\Runs\<nonce>\<operation>`，保留原有低干扰测试 |
| `run_key_native` | 启动项（Win32 API，推荐） | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，直接调用 `RegSetValueExW` / `RegDeleteValueW` |

`run_key_native` 不覆盖已有启动项：值名为本轮唯一的 `EdrTest_<nonce-tag>_<operation>`，Controller 在执行前确认不存在，结束后只删除这个精确值。若当前用户原本没有 `Run` 键，则清理仅在该键仍为空时恢复为“不存在”；若原本已有该键则绝不删除。测试不会触发登录或重启，故该值不会实际启动程序。隔离方法则只删除本轮唯一子树。

本地绝对基准对两种方法都必须通过；云端使用 `method_selection: best`，默认选取 EDR 结果最好的一种方法形成能力结论。这样既能验证重点注册表采集，也能保留产品对普通路径覆盖范围的观察结果。

## 3. BASSLINE 校准

三份 BASSLINE 已升级为 0.2.0：

- 创建：完整键路径、值名、Actor PID/EXE、新值为必需项；启动项方法额外要求 `Child.RegOldValType = 0`、新值类型和 `Child.RegGroupName = 启动项`；
- 修改：将 `Child.RegOldValData` 与本地修改前值比较，将 `Child.RegValData` 与本地修改后值比较；新旧类型与启动项分组也是启动项方法的必需项；
- 删除：参考导出没有任何删除动作，不能据此臆测其字段形态。比较器仍召回时间、路径、值名和程序相近的 JSON 块，但只有映射为 `delete` 的真实删除动作才能通过，不会用 `RegSetValue` 代替删除能力证据。

三类方法都继续以本地发生时间为基准，默认 15 ms 为强关联阈值。`Action.Name` 仍只用于 EDR 候选消歧，不影响本地运行规则；创建和修改的默认值为 `RegSetValue`，删除保持留空，等待真实删除日志后再校准。

腾讯映射现在优先读取已由真实导出确认的字段：

- `Child.RegKeyPath` → `registry.key`
- `Child.RegValName` → `registry.value_name`
- `Child.RegValData` → `registry.value_data`
- `Child.RegOldValData` → `registry.old_value_data`
- `Child.RegValType` → `registry.value_type`
- `Child.RegOldValType` → `registry.old_value_type`
- `Child.RegGroupName` → `registry.group_name`

键路径断言使用 `registry_hive_path` 归一化：它只把 `HKCU`、`HKEY_CURRENT_USER` 与腾讯可能导出的当前用户 `HKEY_USERS\<SID>` 表达统一，后续子路径仍须完全一致；进程路径仍使用普通 `windows_path`，不会放宽。

## 4. 构建和验证

构建会直接覆盖三个旧能力包：

```powershell
pwsh -NoProfile -File scripts\Build-RegistryActivitySamples.ps1 -Configuration Release
```

端到端测试会运行 3 个 Controller、6 个 Actor、6 个本地事件和 6 次精确清理，并同时验证通用映射与腾讯映射：

```powershell
pwsh -NoProfile -File scripts\Test-RegistryActivitySamples.ps1 -Configuration Release
```

腾讯官方文档对 `RegEvents` 和 `RegSetValue` 的说明仍作为产品语义参考：[行为采集范围说明](https://cloud.tencent.com/document/product/1092/128451)、[威胁狩猎常用 SQL](https://cloud.tencent.com/document/product/1092/107833)。
