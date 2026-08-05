# 框架源码占位

实现阶段采用 C# / .NET 8，按可执行程序和公共库组织：

- `EdrTest.Runner`：创建独立 Run DB，调度一个或多个能力 Controller；
- `EdrTest.Export`：把已封存 SQLite 数据库确定性导出为本地 JSON；
- `EdrTest.Compare`：导入用户提供的 EDR JSON，执行离线规范化和 BASELINE 比较；
- `EdrTest.Core`：Run、Capability、BASELINE、状态机和断言模型；
- `EdrTest.Storage.Sqlite`：SQLite Schema、事务和迁移；
- `EdrTest.CloudImport`：流式 JSON 读取与 Mapping Profile；
- `EdrTest.Reporting`：验证结果 JSON，以及后续可选 HTML/JUnit。

每项能力必须提供专属 Controller EXE 和至少一个 Actor EXE；需要被执行对象时再提供 Target EXE。能力包存放在被 Git 忽略的本地 `samples/` 工作区或独立制品库。

本文件只用于保留设计阶段的仓库骨架，不代表框架已经实现。
