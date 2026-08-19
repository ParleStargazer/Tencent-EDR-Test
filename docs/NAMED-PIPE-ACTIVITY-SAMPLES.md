# 命名管道活动能力样本

## 编排

两个能力都由 Controller、Actor、Helper 组成，并使用 `\\.\pipe\EdrTest_<nonce>_<operation>`：

- `win.named_pipe.create@0.1.0`：Actor 调用 `CreateNamedPipeW`，Helper 调用 `CreateFileW`；
- `win.named_pipe.connect@0.1.0`：Helper 调用 `CreateNamedPipeW`，Actor 调用 `CreateFileW`。

服务端创建内核管道后原子写入就绪协议，客户端才允许打开。客户端发送 32 字节本轮 nonce，服务端返回 `ACK` 加 nonce。双方各自原子写入结果；Controller 交叉检查管道名、角色、PID、API、时间、双向字节数和 nonce。程序退出后内核对象自然释放，清理记录还会确认两个参与进程均已结束。

样本直接调用 Win32 API，没有使用 `NamedPipeServerStream`，避免把 .NET 运行时自带的 `dotnet-diagnostic-*` 管道混入被测行为。

## EDR BASELINE

腾讯导出字段为 `@table=FileEvents`、`Action.Name=NamedPipe`，并使用：

- `Child.PipeOpName=创建管道` 或 `打开管道`；
- `Child.PipeName`、`Child.NodeName`、`Child.Type=管道`；
- `Parent.ProcPid`、`Parent.FilePath`、`Parent.ProcCmdline`；
- `Common.EventTime`。

创建项要求完整管道名、Actor PID/路径和创建语义。既有打开事件偶尔只导出 `\\`，因此连接项的完整管道名是推荐要求，Actor PID/路径、打开语义和 15 ms 时间是必需要求。两项默认 `Action.Name=NamedPipe` 仅用于可选消歧。

## 构建与验证

```powershell
pwsh -NoProfile -File scripts/Build-NamedPipeActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-NamedPipeActivitySamples.ps1
```
