# Network Activity 五项能力样本

## 1. 能力范围

当前实现五项 Windows 网络活动能力。每项均由 Controller 编排，Actor 产生待检测行为；需要受控对端时由 Helper 提供回环服务。

| 能力 ID | 中文 / English | 测试方法 |
| --- | --- | --- |
| `win.network.tcp` | TCP 连接 / TCP Connection | Actor 与回环 TCP Helper 建立连接并双向校验 nonce |
| `win.network.udp` | UDP 连接 / UDP Connection | Actor 与回环 UDP Helper 交换 nonce 数据报 |
| `win.network.url` | URL 访问 / URL | 原始套接字 HTTP、WinINet `InternetOpenUrlW` 两个子测试 |
| `win.network.dns` | DNS 查询 / DNS Query | 受控原始 UDP DNS、Windows `DnsQuery_W`/Dnscache 两个子测试 |
| `win.network.file_download` | 文件下载 / File Downloaded | 同一 HTTP 下载行为按“连接→文件写入”做有序二轮验证 |

TCP、UDP、URL 和下载仅访问 IPv4 回环地址。Windows DNS Client 子测试查询带本轮唯一 nonce 的 `*.dns.msftncsi.com`，因此 DNS 能力清单声明 `network.required=true`；它不修改系统 DNS 配置，也不停止或重启 Dnscache 服务。

## 2. 编排与本地绝对基准

每个能力包包含三个独立 EXE：

- `*.Controller.exe`：生成 nonce，启动并观察参与程序，交叉验证结果，写入 SQLite，最后只清理本轮拥有的进程和文件；
- `*.Actor.exe`：在 API 调用紧邻时刻记录高精度 UTC 时间，执行实际网络行为；
- `*.Helper.exe`：提供受控回环端点并独立记录收到的 nonce、来源端点和载荷。

Actor 与 Helper 使用不同的结果文件名并采用“写临时文件后原子替换”的协议，Controller 只在文件句柄释放后读取，避免并发读写导致 `IOException`。对于 Windows DNS Client 方法，Helper 位置由系统 Dnscache 服务替代；Controller 通过 SCM 查询其 `svchost.exe` PID，将它以 `external_system_service` 角色记录，并明确标记清理阶段不得终止。

本地运行日志是绝对基准。所有能力至少保存 API 发生/完成时间、Actor PID/路径/命令行、实际端点、传输协议、字节数、nonce、协议制品及 SHA-256。时间强证据阈值为 15 ms，EDR 时间差以本地行为为零点：负数表示 EDR 提前，正数表示 EDR 延后。

## 3. TCP/UDP 的 260809 实测校准

仅 TCP、UDP 按 `reference/260809213600run` 中可以直接证明相应行为的腾讯 EDR 记录校准。两者均命中 `NetworkEvents / NetBind`，来源为 `KernelMon`，并在本地 API 时间前后 10 ms 内出现。

| Canonical 字段 | TCP 导出值 | UDP 导出值 | 要求 |
| --- | --- | --- | --- |
| `process.pid` | Actor PID | Actor PID | 必需，与本地 PID 相同 |
| `process.executable` | Actor 完整路径 | Actor 完整路径 | 必需，与本地路径相同 |
| `network.transport` | `tcp` | `udp` | 必需 |
| `network.source_ip` | `0.0.0.0` | `0.0.0.0` | 必需 |
| `network.source_port` | `0` | `0` | 必需 |
| `network.endpoint_name` | `tcp_0.0.0.0:0` | `udp_0.0.0.0:0` | 推荐 |
| `event.provider` | `KernelMon` | `KernelMon` | 推荐 |

本次导出没有目的 IP、目的端口和方向，因此它们仍作为可靠的本地事实保存，但不作为 TCP/UDP 当前腾讯导出的云端必需条件。BASELINE 不依赖用户填写的 `Action.Name` 才能召回候选；映射使用 `NetBind + Protocol` 规范化为 `tcp_connect` 或 `udp_connect`。

## 4. URL 的双方法验证

URL 能力同轮运行两个相互独立的子测试：

1. `raw_socket`：Actor 自行构造 HTTP `GET`，Helper 验证完整路径和 nonce；
2. `wininet`：Actor 通过 `InternetOpenW`、`InternetOpenUrlW`、`InternetReadFile` 访问另一个唯一回环 URL，Helper 同样验证完整路径和 nonce。

两种方法分别形成本地事实和云端候选集合，比较器采用 `method_selection.strategy: best`：默认用证据最完整的方法形成结论，同时保留另一方法的完整比较信息。Actor、PID、TCP 和 15 ms 时间差是必需证据；完整 URL、host、GET 是推荐证据。只有侧面的 `NetBind` 时最多得到 `PARTIAL`，不会把“发现同进程网络活动”误判成“直接检测到 URL”。

## 5. DNS 的双方法验证

DNS 能力也同轮运行两个子测试：

1. `raw_udp`：Actor 向回环 Helper 的 53/UDP 端口发送标准 A 查询，Helper 返回文档地址 `192.0.2.123`；
2. `windows_dns_client`：Actor 调用 `DnsQuery_W`，使用跳过缓存/hosts 的选项查询唯一 FQDN；Controller 从 SCM 读取 Dnscache 所在 `svchost.exe` 的 PID 和路径，作为云端进程关联基准。

原始方法验证 Actor；系统方法验证 Dnscache `svchost.exe`。两种方法都要求进程身份、UDP 和时间关联，问题名与目的端口 53 是直接语义的推荐证据。若 EDR 只导出高度相关的 UDP/NetBind 记录而没有 DNS 问题名，结论为 `PARTIAL`；候选原始 JSON 仍会完整展示。

## 6. 文件下载的有序二轮验证

文件下载不能由单条连接记录或单条文件记录独立证明。一次真实 HTTP GET 在本地生成两个同属标准 `file_download` 能力的事件，通过 `stage.sequence` 区分：

1. 第一轮“连接验证”：以 Actor PID/路径、TCP、URL 和连接发生时间关联网络事件；
2. 第二轮“文件写入验证”：以同一 Actor、落地绝对路径、大小、MD5 和文件写入发生时间关联文件事件，并声明 `depends_on: download-connection-stage`。

比较器对两轮分别召回、评分、展示候选，并检查所选 EDR 文件事件时间不得早于所选连接事件时间。最终通过必须同时满足：

- 本地连接、文件写入、哈希复核和本地阶段顺序全部成立；
- 云端两轮各自至少有一条满足必需条件的记录；
- 云端两条所选记录的先后顺序正确。

任一轮缺失或不满足必需字段，整项能力都不会判为 `PASS`。前端“有序二轮验证”区域分别展示两轮状态、候选数、命中字段、时间差和顺序检查，且可切换查看每轮的多个原始 JSON 块。

## 7. 数据、前后端与映射

Controller 按现有 `RunDatabase` 接口写入 `program_instance`、`local_event`、`local_fact`、`artifact` 和 `cleanup_result`。本地事件遵循 `schemas/local-event-data.schema.json` 的 `network` 分支；URL/DNS 方法由 `subtest` 标识，下载阶段由 `stage` 标识，不扩展既有 53 项标准动作枚举。

腾讯映射新增两条高优先级 NetBind 路由，只负责 TCP/UDP 实测字段校准；原有 HttpRequest、SocketRequest 和文件映射继续负责 URL、DNS、下载候选的规范化。比较器始终先使用本地时间、程序、PID、URL/路径等绝对事实关联，事件类型与动作只作为排序和语义提示。

前端按能力展示最佳方法、全部方法和下载阶段结果。即使 EDR 缺少某些直接字段，也会继续展示时间接近且程序、文件或端点相关的低置信候选，方便人工判断侧面证据。

## 8. 构建与验收

构建并覆盖五个本地能力包：

```powershell
pwsh -NoProfile -File scripts/Build-NetworkActivitySamples.ps1
```

执行真实网络行为、SQLite/JSON 契约检查及通用/腾讯两种字段形态的离线比较：

```powershell
pwsh -NoProfile -File scripts/Test-NetworkActivitySamples.ps1
```

验收要求：5 项均为 `LOCAL_PASS`；URL 与 DNS 各有 2 个子测试；下载有 2 个有序本地事件；通用映射为 `5 PASS`；合成腾讯字段形态为 `3 PASS + 2 PARTIAL`（URL/DNS 在只有侧面网络证据的方法上如实降级）；下载二轮的 `stage_flow` 必须为 `ordered_all / PASS`。
