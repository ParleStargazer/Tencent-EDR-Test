# EDR Capability Validation Platform

面向 EDR 产品的终端遥测能力离线验证平台。平台在 Windows 测试机执行安全、可重复、可关联的能力程序，将本地事实保存到每轮独立的 SQLite 数据库；用户从 EDR 平台导出 JSON 日志后，由比较工具离线输出能力覆盖、字段完整性和关联证据。

## 当前状态

项目已实现可运行的首版 Windows 框架：能力包发现与校验、Controller 串行调度、每轮独立 SQLite、确定性本地 JSON 导出、云端日志映射和 BASELINE 离线比较已经形成闭环。Process Activity 六项、File Manipulation 五项、User Account Activity 五项、Network Activity 五项、Hash Algorithms 三项、Registry Activity 三项、Scheduled Task Activity 三项、Service Activity 三项、Group Policy Modification 一项、Named Pipe Activity 两项、PowerShell Activity 一项、BITS Job Activity 一项、WMI Activity 三项、Virtual Disk Mount 一项、USB Device Mount/Unmount 两项和 Driver Activity 三项真实能力样本均已实现；中文前端通过本机回环 API 直接编排 Runner、查看轮次并提交离线比较，还可在测试结束后使用本机 Edge 自动下载、校验并绑定腾讯 EDR 云端日志。

- 中文前端控制面：[web/README.md](web/README.md)
- 详细设计：[docs/DESIGN.md](docs/DESIGN.md)
- BASELINE 示例：[baselines/windows/file_create.yaml](baselines/windows/file_create.yaml)
- BASELINE JSON Schema：[schemas/baseline.schema.json](schemas/baseline.schema.json)
- 能力包 Schema：[schemas/capability-manifest.schema.json](schemas/capability-manifest.schema.json)
- 本轮数据库 DDL：[schemas/run-db.sql](schemas/run-db.sql)
- 本地导出 Schema：[schemas/run-export.schema.json](schemas/run-export.schema.json)
- 分类事件数据 Schema：[schemas/local-event-data.schema.json](schemas/local-event-data.schema.json)
- 本地信息采集设计：[docs/LOCAL-OBSERVATION-DESIGN.md](docs/LOCAL-OBSERVATION-DESIGN.md)
- 新能力开发与接入规范：[docs/SAMPLE-INTEGRATION.md](docs/SAMPLE-INTEGRATION.md)
- Process Activity 六项能力样本：[docs/PROCESS-ACTIVITY-SAMPLES.md](docs/PROCESS-ACTIVITY-SAMPLES.md)
- File Manipulation 五项能力样本：[docs/FILE-MANIPULATION-SAMPLES.md](docs/FILE-MANIPULATION-SAMPLES.md)
- User Account Activity 五项能力样本：[docs/USER-ACCOUNT-ACTIVITY-SAMPLES.md](docs/USER-ACCOUNT-ACTIVITY-SAMPLES.md)
- Network Activity 五项能力样本：[docs/NETWORK-ACTIVITY-SAMPLES.md](docs/NETWORK-ACTIVITY-SAMPLES.md)
- Hash Algorithms 三项能力样本：[docs/HASH-ALGORITHMS-SAMPLES.md](docs/HASH-ALGORITHMS-SAMPLES.md)
- Scheduled Task Activity 三项能力样本：[docs/SCHEDULED-TASK-ACTIVITY-SAMPLES.md](docs/SCHEDULED-TASK-ACTIVITY-SAMPLES.md)
- Service Activity 三项能力样本：[docs/SERVICE-ACTIVITY-SAMPLES.md](docs/SERVICE-ACTIVITY-SAMPLES.md)
- Registry Activity 三项能力样本：[docs/REGISTRY-ACTIVITY-SAMPLES.md](docs/REGISTRY-ACTIVITY-SAMPLES.md)
- Group Policy Modification 能力样本：[docs/GROUP-POLICY-ACTIVITY-SAMPLES.md](docs/GROUP-POLICY-ACTIVITY-SAMPLES.md)
- Named Pipe Activity 两项能力样本：[docs/NAMED-PIPE-ACTIVITY-SAMPLES.md](docs/NAMED-PIPE-ACTIVITY-SAMPLES.md)
- BITS Job Activity 双方法能力样本：[docs/BITS-ACTIVITY-SAMPLES.md](docs/BITS-ACTIVITY-SAMPLES.md)
- WMI Activity 三项能力样本：[docs/WMI-ACTIVITY-SAMPLES.md](docs/WMI-ACTIVITY-SAMPLES.md)
- Virtual Disk Mount 双方法能力样本：[docs/VIRTUAL-DISK-ACTIVITY-SAMPLES.md](docs/VIRTUAL-DISK-ACTIVITY-SAMPLES.md)
- USB Device Mount/Unmount UDE 能力样本：[docs/USB-DEVICE-ACTIVITY-SAMPLES.md](docs/USB-DEVICE-ACTIVITY-SAMPLES.md)
- Driver Activity 三项能力样本：[docs/DRIVER-ACTIVITY-SAMPLES.md](docs/DRIVER-ACTIVITY-SAMPLES.md)
- 腾讯 EDR BASELINE 字段基准说明：[docs/TENCENT-EDR-FIELD-CATALOG.md](docs/TENCENT-EDR-FIELD-CATALOG.md)
- 腾讯 EDR 字段含义与脱敏示例：[docs/reference/tencent-edr-field-catalog.json](docs/reference/tencent-edr-field-catalog.json)
- 本地前后端与一键启动说明：[docs/LOCAL-CONTROL-PLANE.md](docs/LOCAL-CONTROL-PLANE.md)
- 腾讯 EDR 云端日志自动下载与导入：[docs/CLOUD-LOG-AUTOMATION.md](docs/CLOUD-LOG-AUTOMATION.md)
- 轮次云端导入记录 Schema：[schemas/cloud-import-record.schema.json](schemas/cloud-import-record.schema.json)
- 能力包清单模板：[examples/capability-package/capability.json](examples/capability-package/capability.json)
- 进程创建本地 JSON 示例：[examples/local-run.process-create.example.json](examples/local-run.process-create.example.json)
- 验证结果 Schema：[schemas/validation-result.schema.json](schemas/validation-result.schema.json)
- 规范化事件 Schema：[schemas/normalized-event.schema.json](schemas/normalized-event.schema.json)
- 云端映射 Schema：[schemas/mapping-profile.schema.json](schemas/mapping-profile.schema.json)
- 腾讯 EDR 进程、文件、账号与网络日志映射：[mappings/tencent-edr-proc-events-v1.yaml](mappings/tencent-edr-proc-events-v1.yaml)
- 环境配置示例：[configs/environments.example.yaml](configs/environments.example.yaml)
- 云端导出清单示例：[configs/cloud-export-manifest.example.json](configs/cloud-export-manifest.example.json)
- 第三方参考说明：[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

## 目标边界

首期覆盖 Windows 基础遥测：进程、文件、注册表和网络。平台不接入腾讯 EDR API，不持久化 EDR 凭据；用户可自行导入平台导出的 JSON，也可选择由本机 Edge 模拟控制台操作，在当前轮次结束后自动下载并导入。平台验证的是“EDR 是否提供对应遥测”，不评价拦截、告警研判、响应处置或 MDR 服务质量。

## 一键启动

准备好 .NET 8 SDK（或更高版本）、PowerShell 7、Node.js 22.13+ 和 pnpm 11.9+ 后，双击仓库根目录的 `启动平台.cmd`。脚本会构建框架与能力包、构建前端、启动本地服务并打开 `http://127.0.0.1:3000/`。

五项用户账号活动、三项计划任务活动、三项服务活动、组策略修改、三项 permanent WMI subscription 活动、虚拟磁盘挂载和驱动三项需要管理员权限。计划任务、组策略修改与虚拟磁盘挂载为 L2，驱动三项为 L3，都必须在前端确认高风险或向 CLI 传入 `--allow-high-risk`。计划任务的非 RPC 子测试会保存并临时启用“其他对象访问事件”成功审核，读取对应 4698/4702/4699 后恢复原策略。驱动默认直接使用仓库中已签名的 SYS/CAT 与仅含公钥的 CER，不要求测试机安装 EWDK；仅当仓库包缺失或校验失败时，启动脚本才探测 `-EwdkRoot` 并尝试后备构建，两者都不可用时只跳过对应驱动能力。启动脚本会检测包内 SYS/INF/CAT/CER 完整性、SYS/CAT 当前 Windows 信任链、管理员权限与当前启动项的 `testsigning`；若签名包对应的公开测试证书尚未受信任，会询问是否导入到 `LocalMachine\Root` 和 `LocalMachine\TrustedPublisher`。平台不会自动开启 `testsigning`，不开启时会明确提示驱动能力不可用。驱动 Controller 仍会复核驱动哈希、签名和证书信任；不满足时封存为 `SKIPPED / ENVIRONMENT_NOT_READY`，不会尝试加载驱动或计作 EDR 失败。

```powershell
pwsh -NoProfile -File scripts/Start-EdrTest.ps1

# 非交互环境明确允许或禁止导入与签名包匹配的公开测试证书
pwsh -NoProfile -File scripts/Start-EdrTest.ps1 -DriverCertificateImportMode Always
pwsh -NoProfile -File scripts/Start-EdrTest.ps1 -DriverCertificateImportMode Never

# 已有最新构建产物时可快速启动
pwsh -NoProfile -File scripts/Start-EdrTest.ps1 -SkipBuild

# 停止前后端及其子进程
pwsh -NoProfile -File scripts/Stop-EdrTest.ps1
```

前端只连接 `127.0.0.1` 上的 API。手动导入日志保存在 Git 忽略的中央 `import/`；可选浏览器自动化使用本机 Edge 模拟腾讯控制台筛选与下载，成功文件保存在对应 `runs/<date>/<run-id>/import/cloud/` 并自动绑定，比较报告保存在 `reports/`。平台不调用腾讯 EDR API；账号和密码只用于当前后台任务，通过标准输入交给浏览器进程，不写入配置、日志、数据库或运行制品。

## 命令行框架

```powershell
dotnet restore EdrTest.sln
dotnet build EdrTest.sln

dotnet run --project src/EdrTest -- capabilities --root samples

dotnet run --project src/EdrTest -- serve --repo-root .

pwsh -NoProfile -File scripts/Build-ProcessActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-FileManipulationSamples.ps1
pwsh -NoProfile -File scripts/Build-UserAccountActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-NetworkActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-HashAlgorithmsSamples.ps1
pwsh -NoProfile -File scripts/Build-RegistryActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-ScheduledTaskActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-ServiceActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-GroupPolicyActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-NamedPipeActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-PowerShellActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-BitsActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-WmiActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-VirtualDiskActivitySamples.ps1
pwsh -NoProfile -File scripts/Build-DriverActivitySamples.ps1

pwsh -NoProfile -File scripts/Test-ProcessActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-FileManipulationSamples.ps1
pwsh -NoProfile -File scripts/Test-UserAccountActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-NetworkActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-HashAlgorithmsSamples.ps1
pwsh -NoProfile -File scripts/Test-RegistryActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-ScheduledTaskActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-ServiceActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-GroupPolicyActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-NamedPipeActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-PowerShellActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-BitsActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-WmiActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-VirtualDiskActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-DriverActivitySamples.ps1 -EwdkRoot F:\EWDK

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
  --strong-correlation-time-ms 15 `
  --candidate-time-limit-ms 1000 `
  --out .\validation-result.json `
  --conclusion-out .\validation-conclusion.md
```

这些活动构建会直接清理并覆盖 `samples/` 下的同名旧能力包。哈希能力中 MD5、SHA 创建合法 `.json` 文件，IMPHASH 创建真实 `.exe` PE 副本且不会执行；三项都只操作本轮工作目录并立即精确清理。注册表三项只操作 `HKCU\Software\EdrTest\Runs` 下的本轮临时键。组策略修改先执行 `HKLM\SOFTWARE\Policies\EdrTest\Runs\<nonce>` 隔离控制组，再对真实白名单策略值执行同类型、同原始字节回写；若没有现存值，临时预置安全增强值 `EnableSmartScreen=1`，由 Actor 采证后精确恢复原状态。命名管道两项只创建 `\\.\pipe\EdrTest_<nonce>_<operation>` 短生命周期管道，由 Actor/Helper 完成双向 nonce 握手。BITS 双方法只从进程内回环 HTTP 服务下载 nonce JSON，完成或按精确 Job ID 取消任务，不使用通知命令和系统范围重置。WMI 三项只创建本轮唯一 `__EventFilter`、`LogFileEventConsumer` 和 Binding，并严格按 Binding、Consumer、Filter 顺序清理，不使用命令型 Consumer。虚拟磁盘双方法分别调用 `Mount-DiskImage` 和 VirtDisk API，只附加本轮未初始化的 16 MiB 动态 VHD，保持只读、无盘符并在双端复核后精确卸载删除。驱动三项仅使用本轮唯一 SCM 名称与 `.sys` 工作副本；最小驱动无设备、IOCTL 或回调，加载和卸载后精确清理，修改只作用于从未加载的副本。计划任务安全审计子测试只临时启用“其他对象访问事件”成功审核，读取 4698/4702/4699 后恢复原策略；所有任务均使用本轮唯一资源且不会实际执行。服务样本也只操作本轮唯一资源且不会启动服务。网络五项只使用本机回环端点；`win.process.image_load@0.3.0` 包含三个原生 DLL 加载子项和一个托管程序集加载子项。比较器仅使用与本地能力版本完全匹配的 BASELINE。

比较命令会同时生成结构化 `validation-result.json` 和中文 `validation-conclusion.md`；未指定 `--conclusion-out` 时，Markdown 结论自动写入 JSON 同目录。同一个 `EdrTest.exe` 还提供 `export` 和 `inspect` 子命令。运行 `dotnet run --project src/EdrTest -- help` 可查看完整参数。

前端包含三个路由：工作台 `/`、串行能力测试 `/test`、离线比较 `/compare`。测试页逐项显示能力进度、下一项等待倒计时和重点日志，可选在本地完成后等待并自动获取云端日志；云端导入单独显示百分比、当前阶段和最近事件，调试模式会打开可见 Edge、展示详细诊断并保存可下载的脱敏 JSONL，云端成功或失败均不改变本地结论。完成项按能力进入“已完成队列”，点击后可查看 PID、路径、命令行、开始/结束时间、本地事实以及 Runner/Controller 输出；比较页会自动发现所选轮次中校验成功的云端日志，一份时自动选择、多份时默认最新且允许切换、没有时回退手动导入，并按能力折叠展示 BASELINE，能力展开后默认折叠本地条件、展开 EDR 条件。每项能力都可打开 JSON 对照悬浮窗，同屏查看该能力的本地运行导出块和 EDR 原始候选块，绿色高亮 BASELINE 一致字段，并在多条候选间切换。

## 仓库约定

```text
baselines/     版本化检验基准
configs/       非敏感环境配置模板
docs/          架构、设计和决策记录
drivers/       可审计的最小测试驱动源码、INF 与公开预构建包
mappings/      EDR 云端 JSON 到规范化事件的映射
schemas/       BASELINE 与规范化事件数据契约
sample-src/    可审计的能力样本源码与清单模板
scripts/       样本构建和端到端测试脚本
script/        驱动签名与测试机环境初始化脚本
src/           自动化测试框架源码
tests/         单元、契约、集成和端到端测试
web/           中文前端控制面
```

本地 `reference/`、`samples/`、`EDR-Telemetry-main/` 和运行制品目录不纳入版本控制。

## 许可证

本项目采用 [MIT License](LICENSE)。
