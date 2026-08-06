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

框架构建与端到端测试：

```powershell
dotnet build EdrTest.sln
dotnet run --project tests/EdrTest.Tests/EdrTest.Tests.csproj --no-build
```

端到端测试会在系统临时目录组装一个受控能力包，真实启动独立 Controller 子进程，并验证 `Runner → SQLite → Export → Mapping → BASELINE → PASS`。测试结束后自动删除临时制品。
