# EDR Capability Validation Platform

面向 EDR 产品的终端遥测能力离线验证平台。平台在 Windows 测试机执行安全、可重复、可关联的能力程序，将本地事实保存到每轮独立的 SQLite 数据库；用户从 EDR 平台导出 JSON 日志后，由比较工具离线输出能力覆盖、字段完整性和关联证据。

## 当前状态

项目已实现可运行的首版 Windows 框架：能力包发现与校验、Controller 串行调度、每轮独立 SQLite、确定性本地 JSON 导出、云端日志映射和 BASELINE 离线比较已经形成闭环。Process Activity 六项真实能力样本均已实现；中文前端通过本机回环 API 直接编排 Runner、查看轮次并提交离线比较。

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
- 本地前后端与一键启动说明：[docs/LOCAL-CONTROL-PLANE.md](docs/LOCAL-CONTROL-PLANE.md)
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

## 一键启动（推荐）

准备好 .NET 8 SDK（或更高版本）、PowerShell 7、Node.js 22.13+ 和 pnpm 11.9+ 后，双击仓库根目录的 `启动平台.cmd`。脚本会构建框架与能力包、构建前端、启动本地服务并打开 `http://127.0.0.1:3000/`。

```powershell
pwsh -NoProfile -File scripts/Start-EdrTest.ps1

# 已有最新构建产物时可快速启动
pwsh -NoProfile -File scripts/Start-EdrTest.ps1 -SkipBuild

# 停止前后端及其子进程
pwsh -NoProfile -File scripts/Stop-EdrTest.ps1
```

前端只连接 `127.0.0.1` 上的 API；云端日志由用户自行从 EDR 平台导出后上传到本机 API，保存在 Git 忽略的 `import/`，比较报告保存在 `reports/`。平台不会连接腾讯 EDR API，也不会保存 EDR 凭据。

## 命令行框架

```powershell
dotnet restore EdrTest.sln
dotnet build EdrTest.sln

dotnet run --project src/EdrTest -- capabilities --root samples

dotnet run --project src/EdrTest -- serve --repo-root .

pwsh -NoProfile -File scripts/Build-ProcessActivitySamples.ps1

pwsh -NoProfile -File scripts/Test-ProcessActivitySamples.ps1

dotnet run --project src/EdrTest -- run `
  --capability win.process.create `
  --samples-root samples `
  --runs-dir runs `
  --next-delay-seconds 3

dotnet run --project src/EdrTest -- compare `
  --local .\runs\<date>\<run-id>\export\local-run.json `
  --cloud .\cloud-events.json `
  --mapping .\mappings\tencent-edr-proc-events-v1.yaml `
  --baseline .\baselines\windows\process_create.yaml `
  --out .\validation-result.json `
  --conclusion-out .\validation-conclusion.md
```

比较命令会同时生成结构化 `validation-result.json` 和中文 `validation-conclusion.md`；未指定 `--conclusion-out` 时，Markdown 结论自动写入 JSON 同目录。同一个 `EdrTest.exe` 还提供 `export` 和 `inspect` 子命令。运行 `dotnet run --project src/EdrTest -- help` 可查看完整参数。

前端包含三个路由：工作台 `/`、串行能力测试 `/test`、离线比较 `/compare`。测试页逐项显示能力进度、下一项等待倒计时和重点日志，完成项按能力进入“已完成队列”，点击后可查看 PID、路径、命令行、开始/结束时间、本地事实以及 Runner/Controller 输出；比较页按能力折叠展示 BASELINE，能力展开后默认折叠本地条件、展开 EDR 条件，并可查看按关联得分和时间距离排序的 EDR 原始完整日志。

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
