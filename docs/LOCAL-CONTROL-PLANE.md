# 本地控制面与一键启动设计

## 1. 定位

前端页面是项目的默认入口，但不承担 EXE 调度、SQLite 写入或比较逻辑。页面只访问本机回环地址上的 `EdrTest serve` API；API 复用现有 `RunnerService`、`ExportService` 和 `CompareService`，因此页面与 CLI 使用同一套执行和验证语义。

```mermaid
flowchart LR
    U["用户 / 中文前端"] -->|"127.0.0.1 HTTP"| A["EdrTest 本地 API"]
    A --> R["RunnerService"]
    R --> C["Controller EXE"]
    C --> B["Actor / Target EXE"]
    R --> D[("每轮独立 SQLite")]
    D --> E["local-run.json"]
    U -->|"手动导入 EDR JSON/JSONL"| A
    A -->|"可选：标准输入传递当前任务凭据"| P["本机 Edge 自动化"]
    P -->|"下载、校验并绑定当前轮次"| A
    A --> M["Mapping + BASELINE"]
    E --> M
    M --> V["validation-result.json"]
```

## 2. 一键启动

根目录 `启动平台.cmd` 先提供两种启动方式；5 秒未选择时自动使用“增量启动”，也可选择“全量重构启动”。随后调用 `scripts/Start-EdrTest.ps1`，按顺序完成：

1. 检查 .NET、PowerShell 7、pnpm 与固定端口；
2. 计算 Runner、各能力域、前端依赖和前端产物的 SHA-256 构建指纹；
3. 有 .NET 构建项失效时统一还原一次解决方案，只构建 Runner 项目和指纹变化的能力域；独立能力脚本仍可自行还原，保留现场单项构建能力；
4. 将已实现能力包发布到本地 `samples/`，包括 Process、File、Account、Network、Hash、Registry、Scheduled Task、Service、Group Policy、Named Pipe、PowerShell、BITS、WMI、Virtual Disk、USB Device 和 Driver Activity；
5. 前端锁文件或运行时版本变化时安装依赖，前端源码、配置或 API 地址变化时构建 Vinext 生产包；
6. 隐藏启动 API 与前端，等待两个服务通过健康检查；
7. 写入 `.edr-test/services.json` 并打开浏览器。

构建指纹缓存位于 `.edr-test/build-cache/`。能力指纹包含能力源码、manifest、能力构建脚本、公共协议、Runner 公共代码、嵌入式数据库 Schema、.NET SDK 版本，以及适用时的预构建驱动和公开证书。缓存命中还要求对应能力目录、`capability.json` 和参与程序存在，程序 SHA-256 与 manifest 一致，且输出目录的文件数与总大小未变化；删除或破坏产物会自动触发重建。

`停止平台.cmd` 读取状态文件，先校验记录的仓库路径和进程命令行，再停止对应服务及其子进程。它不会按进程名批量终止其他 .NET、Node 或 PowerShell 进程。

可用参数：

```powershell
pwsh -NoProfile -File scripts/Start-EdrTest.ps1 `
  -ApiPort 4317 -WebPort 3000 -NoBrowser

# 忽略指纹并全量重建；构建完成后会更新指纹，供下一次增量启动使用
pwsh -NoProfile -File scripts/Start-EdrTest.ps1 -BuildMode Full

# 完全跳过构建；仅在人工确认框架、样本和 web/dist 均可用时使用
pwsh -NoProfile -File scripts/Start-EdrTest.ps1 -SkipBuild
```

运行日志位于 `.edr-test/logs/`。端口被占用时脚本会直接报错，不会自动漂移到其他端口。

## 3. 本地 API

默认地址为 `http://127.0.0.1:4317`。

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/api/health` | 健康状态、版本和能力包数量 |
| GET | `/api/capabilities` | 从本地 `samples/` 发现可执行能力 |
| GET | `/api/baselines` | 返回版本化 BASELINE 摘要 |
| GET | `/api/mappings` | 返回可选云端字段映射 |
| GET | `/api/runs` | 当前进程轮次与本机历史轮次 |
| POST | `/api/runs` | 启动一个或多个能力的真实 Runner 轮次 |
| GET | `/api/runs/{id}` | 查询轮次进度和终态 |
| POST | `/api/runs/{id}/cancel` | 取消并触发 Runner 清理 |
| GET | `/api/runs/{id}/local-export` | 下载 `local-run.json` |
| GET | `/api/runs/{id}/cloud-imports` | 查询当前轮次中校验成功的自动导入记录 |
| GET | `/api/runs/{id}/cloud-imports/{importId}/debug-log` | 下载当前轮次的脱敏云端自动化 JSONL 调试日志 |
| POST | `/api/compare` | multipart 导入云端日志并执行比较 |
| GET | `/api/comparisons/{comparison-id}/progress` | 查询逐能力比较进度和最近一项结论 |
| GET | `/api/reports/{comparison-id}/result` | 下载结构化 JSON 验证结果 |
| GET | `/api/reports/{comparison-id}/conclusion` | 下载中文 Markdown 验证结论 |

`POST /api/compare` 支持两种互斥的云端日志来源：上传 `cloud_file`，或同时提交 `operation_id` 与 `cloud_import_id` 使用该轮次已校验的自动导入记录。手动模式在 `operation_id` 与 `local_file` 中二选一；自动绑定模式固定使用对应轮次的本地导出。可选字段包括 `cloud_manifest`、`mapping_id`、`comparison_id` 和一个或多个 `baseline_id`；未指定 BASELINE 时，API 按本地导出中的能力 ID 自动选择。响应始终是原有的完整 JSON 报告，不混入进度事件。前端预先生成 `comparison_id`，在等待报告期间轮询独立进度接口；比较器每产生一项能力结论就同步更新完成数、参测总数、百分比和该项结论。进度严格按“已完成能力数 ÷ 参测能力总数 × 100%”计算。

比较结果中的每项能力还包含 `local_export_block`、`local_baseline_matches`，每条 `edr_candidates` 则包含独立的 `baseline_matches`。其中的 JSON Pointer 用于把 Canonical 字段准确映射回本地导出块或厂商原始字段，支持前端多候选切换和逐行高亮。

`POST /api/runs` 的 `inter_capability_delay_seconds` 用于设置相邻能力之间的等待时间，范围为 0–300 秒，默认 3 秒。Runner 始终按 `capability_ids` 顺序串行执行；轮次快照同时返回逐能力 `steps`、整体 `progress`、等待倒计时、最近详细 `logs` 和重点 `highlights`。

## 4. 安全边界

- 服务只允许绑定 `localhost`、`127.0.0.1` 或 `::1`；
- 请求 Host 必须是回环地址；浏览器 Origin 必须出现在启动参数允许列表；
- 可使用 `--token` 启用 `X-EDRTest-Token` 本地令牌；
- 单个上传文件上限 256 MB，仅接受 JSON/JSONL；
- 用户导入文件写入 `import/<comparison-id>/`，报告写入 `reports/<comparison-id>/`；
- `samples/`、`runs/`、`import/`、`reports/` 与 `.edr-test/` 均不纳入版本控制；
- 平台不调用腾讯 EDR API；可选自动化仅由本机 Edge 模拟控制台操作。账号和密码只驻留当前后台任务内存，并通过标准输入交给浏览器进程，不进入命令行、环境变量、数据库或运行制品；调试模式仅保存经过 Node/C# 双层凭据脱敏的结构化诊断事件。

## 5. 前端行为

前端始终展示规范化的 16 个活动域和 53 项能力，并拆分为工作台 `/`、进行测试 `/test` 和离线比较 `/compare` 三个路由。只有 API 实际发现的能力包可勾选，其余项目显示“样本待实现”。包含 L2/L3 能力时必须在页面显式确认高风险执行。测试页轮询真实逐能力进度，显示串行步骤、等待倒计时、重点日志和 Controller stdout/stderr；云端自动导入另有独立的百分比、当前阶段、阶段说明和最近事件。用户可启用可见 Edge 调试模式，在页面查看浏览器详细事件，并在终态下载本轮脱敏 JSONL；本地测试终态与云端下载状态分别展示，云端失败不改变本地结论。比较页会发现所选轮次中校验成功的自动导入记录：单份自动选中，多份默认最新并允许切换，没有记录时继续使用手动导入；比较期间每完成一项能力立即更新进度。比较完成后展示总体结论及每条 BASELINE 要求的期望值、实际值和满足状态，每项能力还提供可切换候选块的本地/EDR JSON 对照悬浮窗。
