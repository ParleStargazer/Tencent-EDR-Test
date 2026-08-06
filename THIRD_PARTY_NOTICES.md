# 第三方参考说明

设计阶段参考了工作区中的 `tsale/EDR-Telemetry` 资料，包括遥测分类、状态模型、评分权重思想和遥测生成器说明。

该参考项目声明采用 Creative Commons Attribution-NonCommercial 4.0（CC BY-NC 4.0）。为避免来源、体积和许可边界混淆：

- 工作区中的 `EDR-Telemetry-main/` 只作为本地参考，不纳入本仓库版本控制；
- 本项目不复制其厂商能力数据、生成数据集或实现代码；
- 若未来引入其代码、数据或衍生内容，必须先完成许可证与公司用途审查，并保留署名和变更说明；
- 本项目自身代码和文档采用 MIT License；该许可不覆盖第三方参考项目。

参考项目主页：<https://github.com/tsale/EDR-Telemetry>

## 运行时依赖

框架使用以下 NuGet 依赖，精确版本及传递依赖记录在 `packages.lock.json`：

| 组件 | 版本 | 用途 | 许可证 |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite | 8.0.29 | SQLite ADO.NET 接口 | MIT |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | 已修复 CVE-2025-6965 的 SQLite 原生绑定 | Apache-2.0 / SQLite public domain |
| YamlDotNet | 18.1.0 | Mapping Profile 与 BASELINE YAML 读取 | MIT |

`Microsoft.Data.Sqlite` 会传递引入 SQLitePCLRaw 和 SQLite 原生库。发布制品必须保留 NuGet 包内附带的许可证和版权文件；SQLite 核心本身为 public domain。

- <https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.29>
- <https://www.nuget.org/packages/YamlDotNet/18.1.0>
