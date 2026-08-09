# 能力样本接入指南

## 1. 接入边界

框架不追踪编译后的测试样本包。每个样本包放在本地 `samples/<capability-id>/` 中，由一个 Controller 和一个或多个 Behavior EXE 组成：

```text
samples/win.process.create/
  capability.json
  ProcessCreate.Controller.exe
  ProcessCreate.Actor.exe
  ProcessCreate.Target.exe
  *.dll                         # EXE 的运行依赖，可选
```

`samples/` 已被 `.gitignore` 排除。仓库中的 `examples/capability-package/capability.json` 是可复制的清单模板，不包含可执行样本；官方 Process Activity、File Manipulation、User Account Activity 与 Network Activity 样本的可审计源码分别位于 `sample-src/` 对应目录，构建方式见各能力样本文档，其中网络能力的三程序编排见 [NETWORK-ACTIVITY-SAMPLES.md](NETWORK-ACTIVITY-SAMPLES.md)。

Runner 只启动 Controller。Controller 使用 `EdrTest` 程序集提供的 SDK 打开本轮数据库、启动和观察 Behavior、记录事实并执行清理；Actor、Target、Helper 不直接访问数据库。

## 2. Controller 固定参数

Runner 会在清单 `controller.arguments` 后追加：

```text
--run-id <uuid-v7>
--case-run-id <uuid-v7>
--nonce <128-bit-hex>
--run-db <absolute-path>
--work-dir <absolute-path>
--manifest <absolute-capability-json-path>
--package-dir <absolute-package-path>
--parameters <absolute-json-path>
--timeout-ms <integer>
```

Controller 可直接解析：

```csharp
using EdrTest;

var invocation = ControllerInvocation.Parse(args);
using var database = RunDatabase.OpenReadWrite(invocation.RunDb);
var parameters = File.ReadAllText(invocation.ParametersPath);
```

参数通过单独 JSON 文件传递，避免命令行转义和敏感值意外进入进程命令行。当前参数模型只允许字符串、整数和布尔值。Controller 应从 `PackageDirectory` 解析 Actor/Target/Helper，从 `WorkDir` 创建所有临时工件，不能依赖进程当前目录。

## 3. 最小写入流程

Controller 至少完成以下操作：

```csharp
var started = DateTimeOffset.UtcNow;
var controller = ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller");
database.AddProgram(controller);

// 启动 Actor/Target 后分别构造 ProgramObservation，并记录实际 PID、父 PID、
// 绝对路径、完整命令行、开始时间、SHA-256 和启动结果。
database.AddProgram(actor);
database.AddProgram(target);

database.AddEvent(new LocalEventObservation
{
    CaseRunId = invocation.CaseRunId,
    EventType = "process",
    EventAction = "create",
    Nonce = invocation.Nonce,
    OccurredAtUtc = occurredAt,
    ObservedAtUtc = DateTimeOffset.UtcNow,
    MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
    Source = "process_create_controller",
    CollectionMethod = "process_handle_query",
    ActorProgramId = actor.ProgramInstanceId,
    TargetProgramId = target.ProgramInstanceId,
    Data = eventData
});

database.AddFact(new LocalFactObservation
{
    CaseRunId = invocation.CaseRunId,
    Key = "process.create_succeeded",
    Value = JsonValue.Create(true),
    ObservedAtUtc = DateTimeOffset.UtcNow,
    Source = "process_handle_query"
});

database.AddCleanup(cleanup);
database.CompleteCapability(
    invocation.CaseRunId,
    "LOCAL_PASS",
    DateTimeOffset.UtcNow,
    stopwatch.ElapsedMilliseconds);
return 0;
```

`LocalEventObservation.Data` 必须符合 `schemas/local-event-data.schema.json`；数据库还会强制检查 `data.kind == event_type`、`data.operation == event_action`。

## 4. Controller 退出码

| 退出码 | Runner 回退状态 | 含义 |
| --- | --- | --- |
| `0` | `LOCAL_PASS` | Controller 未写终态时，Runner 按成功封存 |
| `10` | `SKIPPED` | 前置条件不满足 |
| `20` 或其他非零值 | `SAMPLE_ERROR` | 执行、观察或协议错误 |
| `30` | `CLEANUP_ERROR` | 清理失败，Runner 停止后续能力 |

Controller 应优先调用 `CompleteCapability` 写入准确的终态、错误码和错误摘要。退出码是进程异常退出时的回退机制，不是唯一结果接口。

## 5. 项目引用与构建

样本使用 .NET 时，可直接引用框架项目：

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\EdrTest\EdrTest.csproj" />
</ItemGroup>
```

Controller、Actor、Target 应各自生成独立 EXE。调试阶段可以省略清单中的 `sha256`；固定版本交付时应使用：

```powershell
(Get-FileHash .\ProcessCreate.Controller.exe -Algorithm SHA256).Hash.ToLowerInvariant()
```

将摘要写入 `controller.sha256` 和每个 `participants[].sha256` 后，Runner 会在启动前强制校验。

## 6. 本地使用

```powershell
dotnet run --project src/EdrTest -- capabilities --root samples

dotnet run --project src/EdrTest -- run `
  --capability win.process.create `
  --samples-root samples `
  --runs-dir runs
```

一轮可重复传入多个 `--capability` 或多个 `--manifest`，Runner 默认串行执行。L2/L3 样本默认跳过，只有显式提供 `--allow-high-risk` 才会运行。

## 7. 接入验收

样本接入完成必须同时满足：

1. `capabilities` 能发现并校验清单和程序路径；
2. Controller 不依赖当前工作目录，所有路径均使用固定参数中的绝对路径；
3. 成功轮次至少记录 Controller、Actor，以及能力需要的 Target/Helper；
4. 每条事件包含 nonce、发生时间、观察时间、来源、方法和可信度；
5. API 返回成功之外，还存在独立观察事实；
6. 清理无论成功或失败都有前后状态；
7. 重复导出得到相同的业务数组和数据库 SHA-256；
8. 云端比较只读取用户导入的 JSON，不连接 EDR API。
