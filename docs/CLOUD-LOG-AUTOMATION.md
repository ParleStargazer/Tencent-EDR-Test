# 腾讯 EDR 云端日志自动下载与导入

## 1. 功能边界

平台在保留手动导入的前提下，提供可选的浏览器自动化流程：本地能力测试结束后，等待 EDR 数据入库，使用本机 Microsoft Edge 登录腾讯云控制台，按设备与时间筛选全部事件，下载 JSON，并将其绑定到当前 run。该功能模拟用户在网页上的导出操作，不调用腾讯 EDR API，也不改变本地测试结论。

本地结果与云端获取是两个独立状态：

- Runner 仍以 SQLite 和 `export/local-run.json` 决定本地轮次状态；
- 登录失败、二次验证、页面结构变化、超时或下载解析失败只会得到“云端日志获取失败”；
- 云端失败不会把 `LOCAL_PASS` 改成失败，也不会阻止稍后手动导入；
- 成功文件必须通过 256 MiB 上限、JSON 对象记录、记录数、大小和 SHA-256 校验后才允许被离线比较选择。

## 2. 用户流程

在“进行测试”页启用“测试后自动下载并导入云端日志”，填写：

1. 腾讯云子账号；
2. 登录密码；
3. 设备名称，默认使用本机计算机名；
4. 可选日志起始时间，留空时使用轮次开始时间前 10 秒；
5. 测试结束后等待时间，默认 30 秒，范围 0–3600 秒；
6. 可选“调试模式（浏览器界面可见）”。启用后使用可见 Edge，页面实时展示详细自动化事件，并在本轮目录保存完整的脱敏 JSONL 调试日志。

本地测试完成后，页面独立显示 `等待云端日志入库 → 正在自动获取 → 已自动绑定/云端日志获取失败`。云端区域包含 0–100% 进度条、当前阶段、阶段说明、更新时间和最近事件；每次进入新的阻塞操作前都会先上报阶段，因此长时间不动时可以直接判断卡在登录、筛选、导出还是文件校验。账号和密码在轮次创建成功后从前端状态清空。设备名、手动起始时间、调试开关和凭据都不写入浏览器本地存储。

进度单调递增，主要检查点如下：

| 进度 | 阶段 | 含义 |
|---:|---|---|
| 0–8% | `waiting_ingestion` / `prepare_import` / `runtime_ready` / `start_automation_process` | 等待 EDR 入库，创建当前轮次目录并检查 Node.js、Playwright 和自动化脚本 |
| 10–42% | `launch_browser` → `submit_login` | 启动 Edge、创建隔离上下文、打开登录页、填写并提交登录表单 |
| 48–71% | `select_domain` → `apply_time_filter` | 处理域选择，进入全部事件，应用主机和采集时间筛选 |
| 79–91% | `prepare_export` → `save_download` | 选择全部字段与 JSON，等待下载并保存到当前轮次 |
| 94–100% | `validate_download` → `completed` | 校验 JSON/大小/SHA-256、生成 manifest、绑定导入记录 |

如果某一步失败，进度停留在最后完成的百分比，当前阶段保留失败位置，并追加 `acquisition_error` 或对应阶段的错误事件，不会伪造 100%。

在“离线比较”页选择本地轮次后，平台读取该 run 下解析成功的云端导入：

- 一份：自动选择；
- 多份：默认选择最新一份，并允许切换；
- 零份：保持手动导入模式；
- 手动选择云端 JSON/JSONL 时，自动绑定选择被清空；
- 自动绑定日志只能和所属 `operation_id` 的本地结果比较，不能跨轮次拼接。

## 3. 浏览器自动化基准

实现依据 `reference/edr-log-extration-browser-automation` 中的页面操作基准，固定执行以下语义步骤：

1. 打开腾讯云登录页并选择子用户登录；
2. 如出现域选择，选择“默认域”；
3. 进入 EDR 全部事件视图；
4. 添加“主机名称（系统环境信息）等于设备名称”；
5. 添加“采集时间大于日志起始时间”，先点击时间输入框，填写后按 Enter，再点击筛选弹层中的“检索”；
6. 检索后打开导出对话框，选择全部字段和 JSON 格式；
7. 等待浏览器下载事件，将文件保存到当前 run。

自动化采用逐步骤状态等待，而不是假定页面会在固定短时间内完成加载：普通控件最多等待 60 秒；登录提交后的域选择或 EDR 事件视图最多等待 180 秒；每个主要步骤完成后默认预留 800 毫秒页面稳定时间；提交时间筛选后固定等待 10 秒让检索结果加载。页面导航与文件下载仍分别允许最多 5 分钟，Node 自动化子进程的总执行窗口为 10 分钟。

域选择步骤会同时等待域下拉图标 `#ioa-v1 #chevron-down`、兼容文本定位器和 EDR 全部事件视图。只有已经看到事件视图时，才判定当前账号不需要选择域；如果域选择器稍后出现，则必须完成默认域选择并确认，并等待事件视图可用后才能继续。等待超时会停在 select_domain 并返回稳定错误码，不再静默跳过后续步骤。调试日志中的 wait_policy 会记录本轮等待参数，step_settle_wait 会标明每次页面稳定等待，query_result_wait 会标明检索结果等待。

生产脚本使用 `playwright-core` 驱动系统已安装的 Edge (`channel: msedge`)。定位器以参考页面的中文可访问名称为主，并为部分腾讯组件提供文本回退；若页面语义发生变化，自动化会停止并返回稳定错误码，不会尝试模糊点击未知控件。

腾讯云可能要求验证码、MFA 或风险验证。常规模式采用无头浏览器，不绕过这些验证；调试模式会打开可见 Edge，用户可以在当前自动化超时范围内完成交互式验证并观察页面状态。平台不会保存登录态供下一轮复用；若仍无法完成，应使用手动导出。

调试模式除常规进度外，还采集浏览器控制台警告/错误、页面脚本异常、失败请求和 HTTP 4xx/5xx。网络事件只记录请求方法、资源类型、失败原因、状态码和来源 origin，不保存完整 URL、请求头、Cookie 或正文；内存快照和前端展示限制条数，单条落盘消息限制长度，避免页面噪声无限占用运行内存。

## 4. 凭据与日志安全

凭据的生命周期被限制在当前请求：

- 前端仅在内存状态中持有输入，API 接受轮次后立即清空账号和密码；
- 后端只在当前后台任务对象中持有凭据，任务结束的 `finally` 会清空字符串引用；
- 后端通过 Node 子进程标准输入传递 JSON 请求，不把账号或密码放入命令行参数、环境变量或请求文件；
- Node 的标准错误使用逐行结构化事件协议；后端解析后更新轮次进度，调试模式下同时追加到 JSONL；
- Node 和 C# 两层都会将本轮账号、密码替换为 `[REDACTED]`；Node 还会遮蔽常见的 password、authorization、cookie、token 值，C# 落盘前再次执行精确凭据脱敏，单条消息最多保留 4096 个字符；
- 浏览器事件不采集请求头、响应正文、Cookie、页面 DOM、截图或 trace；
- `ApiRunSnapshot`、`cloud-import.json`、manifest、SQLite 和本地导出均没有账号/密码字段；失败信息也经过同一脱敏器后才进入页面或日志。

这是应用层的最小化措施，不等同于操作系统秘密保险库。运行期间，凭据仍会存在于当前进程内存和登录页面 DOM 中；测试机应保持隔离并遵守腾讯云账号安全策略。

## 5. 运行制品

成功或失败记录都位于对应 run，不写入中央 `import/`：

```text
runs/<date>/<run-id>/
├─ export/local-run.json
└─ import/cloud/<import-id>/
   ├─ cloud.json                         # 下载成功后存在
   ├─ cloud-manifest.json                # 下载且解析成功后存在
   ├─ cloud-import.json                  # 成功/失败状态及完整性摘要
   └─ cloud-automation-debug.jsonl       # 仅调试模式，成功或失败均尽量保留
```

`cloud-import.json` 遵循 `schemas/cloud-import-record.schema.json`。成功记录保存格式、记录数、文件大小和 SHA-256；离线比较解析绑定时会重新计算这些值，任一不一致都拒绝使用。manifest 沿用 `schemas/cloud-export-manifest.schema.json`，记录查询窗口、设备筛选和源文件摘要。

调试 JSONL 每行是一条 `ApiCloudProgressEntry`，包含 UTC 时间、级别、阶段、说明、累计百分比和 `detailed` 标记。API 内存快照仅保留最近 250 条，浏览器页面在调试模式显示最近 80 条，但 JSONL 保留本轮写入的完整序列；历史轮次重新打开时会从文件恢复最近 250 条。该文件不改变 `cloud-import.json` schema，也不参与离线比较。

## 6. 本地 API

- `POST /api/runs`：可选 `cloud_automation` 对象；`debug_mode: true` 启用可见浏览器和详细日志，凭据仅用于该后台轮次。
- `GET /api/runs/{operationId}`：`cloud_acquisition` 独立报告状态，并返回 `progress`、`stage`、`stage_message`、`updated_at_utc`、最近 `logs`、`debug_mode` 和 `debug_log_available`。
- `GET /api/runs/{operationId}/cloud-imports`：按导入时间倒序列出该 run 的绑定记录。
- `GET /api/runs/{operationId}/cloud-imports/{importId}/debug-log`：仅在对应调试文件存在且导入记录属于该轮次时下载 JSONL；不接受任意文件路径。
- `POST /api/compare`：继续支持 `cloud_file`；也支持 `operation_id + cloud_import_id`，两种来源互斥。

自动绑定比较会使用同目录 `cloud-manifest.json`，不允许再上传另一份 manifest。手动导入保持原行为，文件保存到 Git 忽略的中央 `import/<comparison-id>/`。

## 7. 启动、验证与排错

`Start-EdrTest.ps1` 会检查 Node.js，并在 `playwright-core` 缺失时执行锁定安装；后端通过 `--node-path` 使用启动脚本解析到的确切 Node.js 路径。浏览器本体不随 npm 依赖下载，使用 Windows 自带/已安装的 Edge。

验证命令：

```powershell
dotnet build EdrTest.sln --configuration Release --no-restore
dotnet run --project tests/EdrTest.Tests --configuration Release --no-build

Push-Location web
pnpm test
Pop-Location
```

若前端显示“自动化运行时不可用”，重新运行 `pnpm install --frozen-lockfile` 或直接使用一键启动脚本。若进度长时间不变：

1. 先看当前阶段和最后一条常规事件，确认卡在登录、筛选、等待下载还是校验；
2. 下一轮启用“调试模式（浏览器界面可见）”，直接观察 Edge，并查看页面中的详细事件；
3. 轮次成功或失败后点击“下载调试日志”，按时间查找最后一个 `detailed: false` 主阶段及其后的 warning/error；
4. 若涉及腾讯云登录/MFA、设备名或页面字段变化，修正后重试云端获取，或使用手动导出回退。本地能力测试结果已经封存，不需要重跑。

调试日志可能包含腾讯云页面和网络故障诊断信息，即使已做凭据脱敏，也应按内部测试数据管理，不要公开上传。
