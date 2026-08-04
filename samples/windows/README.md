# Windows 测试样本

此目录用于存放 Windows 测试样本及其 `sample.yaml` 清单。当前仅初始化目录约定，尚未提供可执行样本。

首批计划：

- `process/create`、`process/terminate`、`process/parent_child`
- `file/create`、`file/modify`、`file/rename`、`file/delete`
- `registry/create`、`registry/modify`、`registry/delete`
- `network/dns`、`network/tcp`、`network/http`

每个样本必须实现 `precheck / execute / self_verify / cleanup` 生命周期，并遵循 [详细设计](../../docs/DESIGN.md) 中的风险分级和安全护栏。
