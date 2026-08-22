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
5. 测试结束后等待时间，默认 30 秒，范围 0–3600 秒。

本地测试完成后，页面独立显示 `等待云端日志入库 → 正在自动下载 → 已自动绑定/云端日志获取失败`。账号和密码在轮次创建成功后从前端状态清空。设备名、手动起始时间和凭据都不写入浏览器本地存储。

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
5. 添加“采集时间大于日志起始时间”；
6. 检索后打开导出对话框，选择全部字段和 JSON 格式；
7. 等待浏览器下载事件，将文件保存到当前 run。

生产脚本使用 `playwright-core` 驱动系统已安装的 Edge (`channel: msedge`)。定位器以参考页面的中文可访问名称为主，并为部分腾讯组件提供文本回退；若页面语义发生变化，自动化会停止并返回稳定错误码，不会尝试模糊点击未知控件。

腾讯云可能要求验证码、MFA 或风险验证。当前版本采用无头浏览器，不绕过这些验证；遇到交互式验证时应使用手动导出。平台也不会保存登录态供下一轮复用。

## 4. 凭据与日志安全

凭据的生命周期被限制在当前请求：

- 前端仅在内存状态中持有输入，API 接受轮次后立即清空账号和密码；
- 后端只在当前后台任务对象中持有凭据，任务结束的 `finally` 会清空字符串引用；
- 后端通过 Node 子进程标准输入传递 JSON 请求，不把账号或密码放入命令行参数、环境变量或文件；
- 自动化脚本的标准错误只输出固定步骤标记，后端只排空、不保存页面内容；
- `ApiRunSnapshot`、`cloud-import.json`、manifest、SQLite、本地导出和失败信息均没有账号/密码字段；
- 错误消息由稳定的本地文案生成，不回显页面、账号、密码或请求正文。

这是应用层的最小化措施，不等同于操作系统秘密保险库。运行期间，凭据仍会存在于当前进程内存和登录页面 DOM 中；测试机应保持隔离并遵守腾讯云账号安全策略。

## 5. 运行制品

成功或失败记录都位于对应 run，不写入中央 `import/`：

```text
runs/<date>/<run-id>/
├─ export/local-run.json
└─ import/cloud/<import-id>/
   ├─ cloud.json              # 下载成功后存在
   ├─ cloud-manifest.json     # 下载且解析成功后存在
   └─ cloud-import.json       # 成功/失败状态及完整性摘要
```

`cloud-import.json` 遵循 `schemas/cloud-import-record.schema.json`。成功记录保存格式、记录数、文件大小和 SHA-256；离线比较解析绑定时会重新计算这些值，任一不一致都拒绝使用。manifest 沿用 `schemas/cloud-export-manifest.schema.json`，记录查询窗口、设备筛选和源文件摘要。

## 6. 本地 API

- `POST /api/runs`：可选 `cloud_automation` 对象；凭据仅用于该后台轮次。
- `GET /api/runs/{operationId}`：`cloud_acquisition` 独立报告请求、等待、运行、成功或失败状态。
- `GET /api/runs/{operationId}/cloud-imports`：按导入时间倒序列出该 run 的绑定记录。
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

若前端显示“自动化运行时不可用”，重新运行 `pnpm install --frozen-lockfile` 或直接使用一键启动脚本。若显示“云端日志获取失败”，先确认网络、腾讯云登录/MFA、设备名和页面字段，再使用手动导出作为回退；失败不会要求重跑本地能力测试。
