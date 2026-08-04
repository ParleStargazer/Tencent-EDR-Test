# EDR Capability Validation Platform

面向腾讯 EDR 的终端遥测能力自动化验证平台。平台通过执行安全、可重复、可关联的 Windows 测试样本，采集 EDR 侧事件，与版本化 BASELINE 进行比对，最终输出能力覆盖、字段完整性、上报延迟和异常证据。

## 当前状态

项目处于设计与仓库初始化阶段，尚未提供可用于生产环境的执行器或测试样本。

- 详细设计：[docs/DESIGN.md](docs/DESIGN.md)
- BASELINE 示例：[baselines/windows/file_create.yaml](baselines/windows/file_create.yaml)
- BASELINE JSON Schema：[schemas/baseline.schema.json](schemas/baseline.schema.json)
- 规范化事件 Schema：[schemas/normalized-event.schema.json](schemas/normalized-event.schema.json)
- 环境配置示例：[configs/environments.example.yaml](configs/environments.example.yaml)
- 第三方参考说明：[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

## 目标边界

首期覆盖 Windows 基础遥测：进程、文件、注册表和网络。平台验证的是“EDR 能否采集并提供对应遥测”，不评价拦截、告警研判、响应处置或 MDR 服务质量。

## 规划中的运行方式

```text
edr-validate plan --suite windows-smoke --environment lab
edr-validate run  --suite windows-smoke --environment lab
edr-validate report <run-id> --format html,json
```

命令仅用于表达目标接口，当前尚未实现。

## 仓库约定

```text
baselines/     版本化检验基准
configs/       非敏感环境配置模板
docs/          架构、设计和决策记录
schemas/       BASELINE 与规范化事件数据契约
src/           自动化测试框架源码
tests/         单元、契约、集成和端到端测试
```

本地 `reference/`、`samples/` 和 `EDR-Telemetry-main/` 目录不纳入版本控制。

## 许可证

本项目采用 [MIT License](LICENSE)。
