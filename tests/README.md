# 测试目录

实现阶段按以下层次组织：

- `unit`：领域模型、断言、评分、脱敏；
- `contract`：SQLite、Capability Manifest、BASELINE、Mapping 和 JSON Schema 契约；
- `integration`：Runner → SQLite → Export → 离线 Compare 端到端；
- `e2e`：隔离 Windows VM 运行能力包，并使用人工导出的 EDR JSON 实测。

当前本地运行数据契约测试：

```powershell
node --test tests/contract/local-run-contract.test.mjs
```
