# Network Activity 五项能力样本

## 1. 范围与原则

当前版本实现五项 Windows 网络活动能力：

| 能力 ID | 中文 / English | Actor 行为 | Helper 行为 |
| --- | --- | --- | --- |
| `win.network.tcp` | TCP 连接 / TCP Connection | 建立 TCP 连接并交换 nonce | 随机端口监听、确认来源端点并返回 ACK |
| `win.network.udp` | UDP 连接 / UDP Connection | 连接 UDP 端点并交换 nonce 数据报 | 随机端口接收并返回 ACK |
| `win.network.url` | URL 访问 / URL | 原始 HTTP `GET` 请求 | 返回含 nonce 的 JSON |
| `win.network.dns` | DNS 查询 / DNS Query | 发送标准 DNS A 查询 | 在 `127.0.0.1:53/UDP` 返回 `192.0.2.123` |
| `win.network.file_download` | 文件下载 / File Downloaded | HTTP GET 后落地二进制文件 | 返回 8192 字节确定性载荷 |

五项均为 `L0 / standard_user`，只使用 IPv4 回环地址，不解析或访问公网地址，不依赖代理、系统 DNS 或外部 HTTP 服务。DNS 使用 `.invalid` 保留顶级域和 `192.0.2.0/24` 文档地址，数据不会离开测试机。清单中的 `network.required=false` 表示不需要外部网络。

## 2. 三程序编排

每个能力包包含三个独立 EXE：

- `*.Controller.exe`：生成 nonce，先启动 Helper，读取其实际监听端点，再启动 Actor；独立交叉验证双方结果，写入 SQLite，并清理下载文件和遗留进程。
- `*.Actor.exe`：产生需要 EDR 采集的网络行为，记录操作前的高精度 UTC 时间、实际本地/远端端点、发送/接收字节数及协议结果。
- `*.Helper.exe`：只监听本机回环地址，向 Controller 报告实际端点，并从对端收到 nonce 后返回受控响应。

Actor 与 Helper 是同一份可审计 Behavior 源码发布出的两个独立程序副本，通过 `--role` 选择职责。二者都不访问 SQLite；Controller 只有在 Actor、Helper 的 nonce、端点和协议结果互相一致时才写入 `LOCAL_PASS`。

## 3. 本地绝对基准

五项共同保存以下高置信本地事实：

- `network.occurred_at_utc` 与 `network.completed_at_utc`；
- Actor/Helper 的实际 PID、绝对路径和 Actor 完整命令行；
- 传输协议、方向、本地/远端 IP 与端口；
- 发送/接收字节数和本轮 `correlation.nonce`；
- Actor 与 Helper 两份协议 JSON 制品及其 SHA-256。

URL 能力额外保存完整 URL、host、path、method 和 HTTP 状态码。DNS 能力保存问题名、Query Type、答案和解析器端点。下载能力保存 HTTP 发生时间、文件写入发生时间、受控落地绝对路径、大小、MD5 与 SHA-256；Controller 在写入 SQLite 前重新读取落地文件并独立计算哈希。

`occurred_at_utc` 在 Actor 调用连接或发送 API 的紧邻时刻采集。五份 BASELINE 的时间强证据阈值统一为 15 ms，EDR 时间以本地时间为零点展示提前或延后。

## 4. EDR BASELINE

BASELINE 位于 `baselines/windows/network_*.yaml`：

| 能力 | 必需云端字段 | 推荐字段 | 腾讯导出语义 |
| --- | --- | --- | --- |
| TCP | Actor PID/路径、TCP、源端口、目标 IP/端口 | 方向、源 IP | `NetworkEvents / SocketRequest / tcp` |
| UDP | Actor PID/路径、UDP、源端口、目标 IP/端口 | 方向 | `NetworkEvents / SocketRequest / udp` |
| URL | Actor PID/路径、完整 URL、目标 IP/端口 | host、GET | `NetworkEvents / HttpRequest` |
| DNS | Actor PID/路径、UDP、目标 IP/53 | DNS 问题名 | `SocketRequest / udp / 53` |
| 文件下载 | HTTP 事件和文件创建事件均必须存在 | GET、文件 MD5 | `HttpRequest` + `FileWriteClose / 新建文件` |

本地规则不依赖 `Action.Name`。腾讯映射只有在导出记录已经满足厂商结构时才将 `SocketRequest`、`HttpRequest` 等字段规范化；离线比较仍优先使用本地 Actor、端点、路径和时间关联候选。

260808 全字段导出没有 DNS 问题名字段，因此腾讯日志只能用 Actor、UDP/53 端点和时间推断 DNS 查询。此时必需条件可以通过，但推荐的 `dns.question` 缺失，结论为 `PARTIAL`，用于如实提示“发现了 DNS 网络行为，但导出字段不足以还原问题名”。通用映射包含问题名时可得到 `PASS`。

文件下载不是“URL 或文件任一命中”关系。BASELINE 要求同一能力轮次同时找到：

1. 与本地完整 URL、Actor、目标端点及时间关联的 HTTP GET；
2. 与本地落地路径、Actor、大小及文件写入时间关联的文件创建。

## 5. 数据与前后端接入

Controller 按现有 `RunDatabase` 接口写入 `program_instance`、`local_event`、`local_fact`、`artifact` 和 `cleanup_result`。事件 `data` 遵循 `schemas/local-event-data.schema.json` 的 `network` 分支；云端规范化结果使用 `network`、`url`、`http`、`dns` 和 `file` 字段。

构建后，五个 `capability.json` 会出现在 `samples/win.network.*`。本机 API 自动发现它们，前端已有的“网络活动”五项会由不可运行状态变为可选；测试日志、已完成能力队列、本地 BASELINE 与 JSON 对照窗沿用现有通用渲染，无需网络能力专用前端分支。

## 6. 构建与验收

构建并覆盖五个旧能力包：

```powershell
pwsh -NoProfile -File scripts/Build-NetworkActivitySamples.ps1
```

运行真实回环行为、SQLite/JSON 契约检查，以及通用与腾讯字段形态的离线比较：

```powershell
pwsh -NoProfile -File scripts/Test-NetworkActivitySamples.ps1
```

验收脚本要求五项均为 `LOCAL_PASS`，共记录 5 个 Controller、5 个 Actor、5 个 Helper、5 条网络事件、10 份协议制品和 5 条成功清理结果。腾讯形态的 URL 记录会刻意使用一个未知 `Action.Name`，确认网络候选仍能依靠本地 URL、Actor、端点和时间召回。通用映射预期 `5 PASS`；腾讯 260808 字段标准预期 `4 PASS + 1 PARTIAL`，唯一 `PARTIAL` 是缺少查询名的 DNS 能力。

DNS Helper 需要独占 `127.0.0.1:53/UDP` 数秒。如果测试机运行了本地 DNS 服务器并占用该端点，应先停止该服务或在隔离测试机执行；样本不会改动系统 DNS 配置。
