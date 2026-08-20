# 腾讯 EDR BASELINE 字段基准

## 1. 基准定位

`docs/reference/tencent-edr-field-catalog.json` 是后续腾讯 EDR BASELINE 设计使用的唯一厂商字段基准，已经替代原先只分析 `260808210300run` 的 field-catalog。

新基准的字段范围来自本地 `reference/EDR日志-字段表.txt`，并遍历当前 `reference/` 下所有顶层为腾讯 EDR 原始事件的 JSON 导出，补充：

- 中文含义；
- 实际观测状态、出现数和 JSON 类型；
- 最多三个保持数据形态的脱敏示例；
- 出现过的事件表、`Action.Name` 和参考 run；
- `time_anchor`、`candidate_filter`、`correlation_anchor`、`capability_assertion` 等 BASELINE 角色；
- 每个字段的使用约束。

`reference/EDR日志-字段表.txt` 是相同内容的 TSV 人工阅读镜像。`reference/` 按仓库约定不追踪，因此代码、评审和远端协作一律引用可追踪的 JSON 基准。

当前基准统计：

| 项目 | 数量 |
| --- | ---: |
| 字段 | 133 |
| 已分析 EDR 导出 | 14 |
| 去重事件 | 10,906 |
| 已观测字段 | 131 |
| 未观测字段 | 2 |

暂未观测到 `Child.DisabledPrivilegeList` 和 `Child.EnabledPrivilegeList`；两者保留产品字段语义，但在取得真实记录前不能用于必需断言。

## 2. 生成方式

```powershell
pwsh -NoProfile -File scripts/Export-TencentEdrFieldCatalog.ps1
```

脚本会：

1. 从纯字段列表或已生成的 TSV 第一列读取稳定字段集合；
2. 递归扫描 `reference/**/*.json`；
3. 只接收根对象或根数组中同时具有 `@table` 和 `Action.Name` 的原始 EDR 记录，不读取本地导出或验证结果中的嵌套候选；
4. 优先用 `Common.EventUUId`、其次用 `Uuid` 去重；
5. 更新可追踪 JSON 和本地 TXT 镜像。

可指定其他位置：

```powershell
pwsh -NoProfile -File scripts/Export-TencentEdrFieldCatalog.ps1 `
  -ReferenceRoot .\reference `
  -FieldListPath .\reference\EDR日志-字段表.txt `
  -OutputPath .\docs\reference\tencent-edr-field-catalog.json `
  -TextOutputPath .\reference\EDR日志-字段表.txt
```

## 3. BASELINE 使用规则

设计新能力时必须先查字段基准，再设计 mapping 和 BASELINE：

1. `observed=true` 只证明该字段在已知 run 中真实出现过；不代表厂商未来版本保证兼容。
2. `observed=false` 的字段属于产品可能字段，但当前没有样例。获得真实导出前，不能直接设为 `required`。
3. `Common.EventTime` 是云端事件发生时间，应映射为 `event.created` 并与本地 `occurred_at_utc` 计算时间差。
4. `@timestamp`、`@collection` 是平台处理或收集时间，只用于诊断，不能替代事件时间。
5. `Action.Name` 只允许作为可选 EDR 消歧条件，不影响本地规则，也不得成为唯一候选入口。
6. `Parent.*` 通常表示行为主体或发起进程，`PParent.*` 表示其上级进程；必须结合具体表和动作确认，不能只凭前缀推断通过。
7. `Child.*` 的含义随动作变化；账号字段只在账号/登录语义明确时用于断言。
8. BASELINE 只引用厂商无关的 Canonical 字段；腾讯字段只能写入 Mapping Profile 或前端可选原始字段筛选。

## 4. 脱敏约束

可追踪示例会替换主机与账号、SID/登录标识、IP/MAC、路径与命令行、事件/进程/租户 ID 和文件哈希。枚举值、类型、时间精度、数值形态和动作上下文予以保留。

字段基准不能包含真实用户名、主机名、内部 IP、租户 ID、终端 ID、完整命令行或真实文件哈希。新增 run 后重新生成目录，并通过契约测试确认字段数量、含义、脱敏和 BASELINE 策略没有退化。
