# WMI 三项能力测试样本

## 1. 能力边界

本实现覆盖 Windows permanent WMI event subscription 的三个独立能力：

| 能力 ID | WMI 对象 | 本地动作 |
| --- | --- | --- |
| `win.wmi.filter` | `__EventFilter` | 创建唯一事件过滤器 |
| `win.wmi.consumer` | `LogFileEventConsumer` | 创建唯一无命令消费者 |
| `win.wmi.consumer_filter.bind` | `__FilterToConsumerBinding` | 创建 Filter、Consumer 并建立引用绑定 |

三项共用 C# Actor、Controller 和 repository 访问模块，使用 `System.Management` 直接操作 `ROOT\subscription`，不调用 PowerShell、AMSI 或脚本引擎。每项能力由 Runner 独立执行并产生单独的 SQLite/JSON 结论；绑定项内部创建必要的 Filter 和 Consumer，但只把 Binding 创建时刻作为该能力的行为时间。

## 2. 安全样本

Filter 名和 Consumer 名分别为：

```text
EDR_TEST_FILTER_<nonce>
EDR_TEST_CONSUMER_<nonce>
```

Filter 使用不会自然触发的查询：

```sql
SELECT * FROM __InstanceCreationEvent WITHIN 10
WHERE TargetInstance ISA 'Win32_Process'
AND TargetInstance.Name = 'EDR_TEST_NEVER_<nonce>.exe'
```

Consumer 采用 `LogFileEventConsumer`，日志目标位于本轮工作目录，文本模板只包含 nonce 和目标进程名插入标记。样本不使用 `CommandLineEventConsumer`，不会设置命令行、脚本、凭据或外部网络目标。由于 Filter 指向不存在的进程名，Consumer 正常情况下不会被触发写文件。

## 3. 本地绝对基准

Actor 创建对象后原子写入就绪协议并等待。Controller 使用独立 `ManagementScope` 再次查询 repository，只有对象路径和关键属性全部与 Actor 协议一致才放行；Actor 收到闸门后再次查询，再执行清理。

本地通过要求包括：

- 创建前目标对象不存在，`ManagementObject.Put(CreateOnly)` 成功；
- Filter 的名称、WQL、`WQL` 查询语言和 `root\cimv2` 事件命名空间完全一致；
- Consumer 的名称、`LogFileEventConsumer` 类、日志路径和文本模板完全一致；
- Binding 的 `Filter`、`Consumer` 引用分别解析到本轮精确对象；
- Actor 和 Controller 两次独立查询均通过；
- Actor 清理后及 Controller 兜底清理后对象均不存在。

本地事实完整保存对象类、名称、绝对 WMI 路径、引用、Actor PID/路径/命令行、紧邻目标 `Put` 的 UTC 时间和清理结果。它们是能力成立的绝对基准，EDR 是否采集不影响本地判断。

## 4. 清理与权限

permanent WMI subscription 需要管理员权限，三个清单均声明 `required_privilege=administrator`。非管理员运行时 Runner 会在行为发生前安全跳过；启动脚本和独立构建脚本会给出推荐使用管理员 PowerShell 的提示。

所有清理都仅按本轮 nonce 派生的精确名称执行，顺序固定为：

```text
__FilterToConsumerBinding
→ LogFileEventConsumer
→ __EventFilter
```

Actor 的正常路径、异常路径和 Controller 的兜底路径都会执行该顺序并重新查询不存在性。代码不会枚举删除其他 permanent subscription，也不会按类执行无条件批量清理。

## 5. BASSLINE 与映射规划

三份 BASSLINE 分别位于：

- `baselines/windows/wmi_filter.yaml`
- `baselines/windows/wmi_consumer.yaml`
- `baselines/windows/wmi_consumer_filter_bind.yaml`

强关联时间为 15 ms，主要强锚点是唯一 Filter/Consumer 名称、命名空间、查询或日志路径；Actor 路径为中等锚点。每项云端直接事件的 `cardinality.min` 都保持为 1，不因当前产品预期失败而降为 0。

Canonical `wmi.*` 字段覆盖命名空间、动作、对象类/名/路径、Filter 查询、Consumer 类/日志配置，以及 Binding 两端引用。通用映射用于自测夹具；腾讯规划映射只接受未来可能出现的精确动作：

- `WmiEventFilter`
- `WmiEventConsumer`
- `WmiEventConsumerToFilter`

现有 `WmiOperation` 记录主要是 `IWbemServices::ExecMethod`，不含 `ROOT\subscription`、`PutInstance` 或三类对象字段；PowerShell `ScriptScan` 也只是脚本意图。两者均不会映射成这三项能力的直接事件。

## 6. 当前产品结论

参考评估中的主动 PoC 已证明三类对象能够在本地创建和查询，但对应时间窗没有 direct WMI telemetry。因此当前预期闭环为：本地 `LOCAL_PASS`，云端因直接事件数量为 0 得到未通过。报告应写为：

> 当前产品版本及策略环境下，未观察到该行为的直接 EDR telemetry。

该结论不等同于宣称所有版本或策略下绝对不支持。未来只要产品导出符合规划字段的直接事件，无需放宽本地条件即可重新比较。

## 7. 构建与管理员复验

```powershell
pwsh -NoProfile -File scripts/Build-WmiActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-WmiActivitySamples.ps1
```

第一条命令构建并覆盖 `samples/win.wmi.*` 三个能力包。第二条命令在管理员 PowerShell 中执行真实对象创建、双端查询、严格清理、腾讯规划映射 PASS 和当前 `WmiOperation` 不得误判通过的完整验收；非管理员环境只验证构建并明确输出 `SKIP`。
