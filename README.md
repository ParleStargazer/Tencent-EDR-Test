# EDR Capability Validation Platform

面向 EDR 产品的终端遥测能力离线验证平台。平台在 Windows 测试机执行安全、可重复、可关联的能力程序，将本地事实保存到每轮独立的 SQLite 数据库；用户从 EDR 平台导出 JSON 日志后，由比较工具离线输出能力覆盖、字段完整性和关联证据。

## 当前状态

项目已实现可运行的首版 Windows 框架：能力包发现与校验、Controller 串行调度、每轮独立 SQLite、确定性本地 JSON 导出、云端日志映射和 BASELINE 离线比较已经形成闭环。Process Activity 六项真实能力样本均已实现；当前前端控制面尚未直接启动 EXE。

- 中文前端控制面：[web/README.md](web/README.md)
- 详细设计：[docs/DESIGN.md](docs/DESIGN.md)
- BASELINE 示例：[baselines/windows/file_create.yaml](baselines/windows/file_create.yaml)
- BASELINE JSON Schema：[schemas/baseline.schema.json](schemas/baseline.schema.json)
- 能力包 Schema：[schemas/capability-manifest.schema.json](schemas/capability-manifest.schema.json)
- 本轮数据库 DDL：[schemas/run-db.sql](schemas/run-db.sql)
- 本地导出 Schema：[schemas/run-export.schema.json](schemas/run-export.schema.json)
- 分类事件数据 Schema：[schemas/local-event-data.schema.json](schemas/local-event-data.schema.json)
- 本地信息采集设计：[docs/LOCAL-OBSERVATION-DESIGN.md](docs/LOCAL-OBSERVATION-DESIGN.md)
- 能力样本接入指南：[docs/SAMPLE-INTEGRATION.md](docs/SAMPLE-INTEGRATION.md)
- Process Activity 六项能力样本：[docs/PROCESS-ACTIVITY-SAMPLES.md](docs/PROCESS-ACTIVITY-SAMPLES.md)
- 能力包清单模板：[examples/capability-package/capability.json](examples/capability-package/capability.json)
- 进程创建本地 JSON 示例：[examples/local-run.process-create.example.json](examples/local-run.process-create.example.json)
- 验证结果 Schema：[schemas/validation-result.schema.json](schemas/validation-result.schema.json)
- 规范化事件 Schema：[schemas/normalized-event.schema.json](schemas/normalized-event.schema.json)
- 云端映射 Schema：[schemas/mapping-profile.schema.json](schemas/mapping-profile.schema.json)
- 腾讯 EDR 进程日志映射：[mappings/tencent-edr-proc-events-v1.yaml](mappings/tencent-edr-proc-events-v1.yaml)
- 环境配置示例：[configs/environments.example.yaml](configs/environments.example.yaml)
- 云端导出清单示例：[configs/cloud-export-manifest.example.json](configs/cloud-export-manifest.example.json)
- 第三方参考说明：[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

## 目标边界

首期覆盖 Windows 基础遥测：进程、文件、注册表和网络。平台不接入腾讯 EDR API，不保存 EDR 凭据；用户自行导入平台导出的 JSON。平台验证的是“EDR 是否提供对应遥测”，不评价拦截、告警研判、响应处置或 MDR 服务质量。

## 前端控制面

前端基于 Node.js、React 与 Vinext，提供能力选择、测试轮次规划、本地文件导入以及首个 `ProcessCreate` 离线比较链路。日志在浏览器本地解析，不会上传至服务端。

```powershell
cd web
pnpm install
pnpm dev
```

访问 `http://localhost:3000/`。当前运行环境使用 Node.js 22.13 或更高版本。

## 命令行框架

```powershell
dotnet restore EdrTest.sln
dotnet build EdrTest.sln

dotnet run --project src/EdrTest -- capabilities --root samples

pwsh -NoProfile -File scripts/Build-ProcessActivitySamples.ps1

pwsh -NoProfile -File scripts/Test-ProcessActivitySamples.ps1

dotnet run --project src/EdrTest -- run `
  --capability win.process.create `
  --samples-root samples `
  --runs-dir runs

dotnet run --project src/EdrTest -- compare `
  --local .\runs\<date>\<run-id>\export\local-run.json `
  --cloud .\cloud-events.json `
  --mapping .\mappings\tencent-edr-proc-events-v1.yaml `
  --baseline .\baselines\windows\process_create.yaml `
  --out .\validation-result.json
```

同一个 `EdrTest.exe` 还提供 `export` 和 `inspect` 子命令。运行 `dotnet run --project src/EdrTest -- help` 可查看完整参数。

## 仓库约定

```text
baselines/     版本化检验基准
configs/       非敏感环境配置模板
docs/          架构、设计和决策记录
mappings/      EDR 云端 JSON 到规范化事件的映射
schemas/       BASELINE 与规范化事件数据契约
sample-src/    可审计的能力样本源码与清单模板
scripts/       样本构建和端到端测试脚本
src/           自动化测试框架源码
tests/         单元、契约、集成和端到端测试
web/           中文前端控制面
```

本地 `reference/`、`samples/`、`EDR-Telemetry-main/` 和运行制品目录不纳入版本控制。

## 许可证

本项目采用 [MIT License](LICENSE)。
