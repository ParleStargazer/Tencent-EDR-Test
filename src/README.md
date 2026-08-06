# 框架源码

首版实现刻意保持为一个 .NET 8 可执行项目 `EdrTest`，通过子命令划分职责，避免在功能稳定前拆成多个空壳程序集：

- `capabilities`：发现并校验 `samples/**/capability.json`；
- `run`：创建轮次、串行启动 Controller、执行风险门禁、封存 SQLite 并自动导出；
- `export`：只读导出已经封存的运行数据库；
- `compare`：读取用户导入的云端 JSON，执行 YAML Mapping 和 BASELINE；
- `inspect`：只读查看轮次和能力终态。

主要文件：

| 文件 | 职责 |
| --- | --- |
| `CapabilityModels.cs` | 能力清单、程序路径/hash 和参数校验 |
| `RunnerService.cs` | 轮次目录、Controller 调度、超时和风险门禁 |
| `RunDatabase.cs` | SQLite v2 以及提供给 Controller 的写入 SDK |
| `ExportService.cs` | `local-run.json` 确定性业务数据导出 |
| `CompareService.cs` | 云端 Mapping、关联、断言和验证结果 |
| `Program.cs` | 中文 CLI 入口 |

每项能力必须提供专属 Controller EXE 和至少一个 Actor EXE；需要被执行对象时再提供 Target EXE。接入方式见 `docs/SAMPLE-INTEGRATION.md`。
