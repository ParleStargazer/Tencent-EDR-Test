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

命名管道与组策略能力另有 Windows 行为测试：

```powershell
pwsh -NoProfile -File scripts/Test-NamedPipeActivitySamples.ps1

# 需要管理员 PowerShell；标准用户运行会明确拒绝修改 HKLM
pwsh -NoProfile -File scripts/Test-GroupPolicyActivitySamples.ps1
```

命名管道测试同时验证通用映射和腾讯 `FileEvents / NamedPipe` 映射。组策略脚本必须在管理员 PowerShell 中运行，并会显式启用 L2 风险确认；它会验证同值回写前后的原生数据哈希和长度完全一致，以及已有策略不变或安全兜底值已恢复。组策略双方法的通用/腾讯 `RegEvents / RegSetValue` 比较闭环已包含在框架测试中。
