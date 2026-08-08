# 腾讯 EDR 260808 全字段目录

## 1. 目的与产物

`reference/260808210300run` 的 EDR 导出包含当前样本所见的完整导出字段集合，但 `reference/` 按仓库约定不进入版本控制。为便于后续能力开发，仓库保存两项可审计产物：

- `docs/reference/tencent-edr-260808-field-catalog.json`：834 条事件、228 个唯一字段的结构化目录；
- `scripts/Export-TencentEdrFieldCatalog.ps1`：从原始导出重新生成目录的脚本。

目录同时提供全局字段统计和按 `Action.Type + Action.Name` 分组的字段名清单。每个全局字段包含出现数、非空数、出现率、JSON 类型和最多三个示例。示例保留数据形态，但会脱敏主机/账号、IP/MAC、路径/命令行、事件/进程/租户标识、调用栈、SID 和文件哈希。

重新生成：

```powershell
pwsh -NoProfile -File scripts/Export-TencentEdrFieldCatalog.ps1

# 也可以指定另一份导出和输出位置
pwsh -NoProfile -File scripts/Export-TencentEdrFieldCatalog.ps1 `
  -InputPath .\reference\new-run\logs.json `
  -OutputPath .\docs\reference\tencent-edr-new-field-catalog.json
```

脚本不修改原始导出。目录中的 `source.sha256` 用于判断参考输入是否变化。

## 2. 260808 导出概况

| 项目 | 数值 |
| --- | ---: |
| 事件总数 | 834 |
| 唯一字段 | 228 |
| `FileWriteClose` | 465 |
| `FileRename` | 13 |
| `FileDelete` | 5 |
| `ProcessCreate` | 86 |
| 其他事件种类 | 27 |

文件写关闭事件中的 `Child.FileCreateOpName` 进一步分为：新建文件 457 条、打开文件 6 条、覆盖写文件 2 条。因此五类文件能力采用以下生产映射：

| 平台能力 | 腾讯条件 | 关键字段 |
| --- | --- | --- |
| 文件创建 `file/create` | `File / FileWriteClose / 新建文件` | `Child.FilePath/FileSize/FileMd5/FileTotalWrite` |
| 文件打开 `file/open` | `File / FileWriteClose / 打开文件` | `Child.FilePath/FileMd5/FileTotalRead/FileTotalWrite` |
| 文件修改 `file/modify` | `File / FileWriteClose / 覆盖写文件` | `Child.FilePath/FileSize/FileMd5/FileTotalWrite` |
| 文件删除 `file/delete` | `File / FileDelete` | 删除前 `Child.FilePath/FileSize/FileMd5` |
| 文件重命名 `file/rename` | `File / FileRename` | 目标 `Child.FilePath`、源 `Child.OldFilePath`、`Child.FileMd5` |

所有文件事件以 `Parent.*` 表示行为进程，以 `Child.*` 表示被操作文件。事件时间 `Common.EventTime` 为 Unix 毫秒，文件的创建、修改、访问时间为 Unix 秒。

## 3. 使用约束

- 该目录证明的是 260808 参考导出中出现过的字段，不等于厂商对未来版本的兼容承诺。
- BASELINE 只使用厂商无关的 Canonical 字段；腾讯原字段只写在 Mapping Profile 中。
- 新导出出现字段增删、类型变化或动作枚举变化时，应生成新目录、更新映射版本并执行契约测试。
- 目录示例已脱敏，不能用于还原真实主机、账号、路径、网络地址或内部 ID。
