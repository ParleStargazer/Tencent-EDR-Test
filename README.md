# EDR Capability Validation Platform

面向 EDR 产品的终端遥测能力离线验证平台。平台在 Windows 测试机执行安全、可重复、可关联的能力程序，将本地事实保存到每轮独立的 SQLite 数据库；用户从 EDR 平台导出 JSON 日志后，由比较工具离线输出能力覆盖、字段完整性和关联证据。

## 当前状态

项目已完成总体设计与首版中文前端控制面。Windows Runner、SQLite 导出器及完整能力样本仍在开发中，当前页面中的轮次进度用于验证编排交互，不会直接启动 EXE。

- 中文前端控制面：[web/README.md](web/README.md)
- 详细设计：[docs/DESIGN.md](docs/DESIGN.md)
- BASELINE 示例：[baselines/windows/file_create.yaml](baselines/windows/file_create.yaml)
- BASELINE JSON Schema：[schemas/baseline.schema.json](schemas/baseline.schema.json)
- 能力包 Schema：[schemas/capability-manifest.schema.json](schemas/capability-manifest.schema.json)
- 本轮数据库 DDL：[schemas/run-db.sql](schemas/run-db.sql)
- 本地导出 Schema：[schemas/run-export.schema.json](schemas/run-export.schema.json)
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

## 规划中的命令行运行方式

```text
EdrTest.Runner.exe run --suite windows-smoke --environment lab
EdrTest.Export.exe --db <run-id>.db --out local-run.json
EdrTest.Compare.exe compare --local local-run.json --cloud cloud-events.json --out validation-result.json
```

命令仅用于表达目标接口，当前尚未实现。

## 仓库约定

```text
baselines/     版本化检验基准
configs/       非敏感环境配置模板
docs/          架构、设计和决策记录
mappings/      EDR 云端 JSON 到规范化事件的映射
schemas/       BASELINE 与规范化事件数据契约
src/           自动化测试框架源码
tests/         单元、契约、集成和端到端测试
web/           中文前端控制面
```

本地 `reference/`、`samples/`、`EDR-Telemetry-main/` 和运行制品目录不纳入版本控制。

## 许可证

本项目采用 [MIT License](LICENSE)。
