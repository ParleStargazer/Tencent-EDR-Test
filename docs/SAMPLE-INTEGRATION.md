# 新能力开发与接入规范

本文是新增或扩展 EDR 遥测能力的强制开发规范。开发顺序固定为“先设计本地绝对基准与云端 BASELINE，再实现样本，最后用真实导出日志校准”；真实日志只能校准字段映射和候选排序，不能反向把某次日志中的固定动作名、PID 或路径写成行为成立的前提。

## 1. 完整交付物

每项新能力至少包含以下内容：

1. 中英文能力名称、稳定的 `capability_id`、能力大类、风险等级和权限要求；
2. `sample-src/<Domain>/` 下可审计的 Controller、Behavior 和 Protocol 源码；
3. `sample-src/<Domain>/manifests/<capability-id>/capability.json`；
4. `baselines/windows/<capability>.yaml`；
5. EDR 原始字段到 Canonical 字段的 mapping route；
6. `scripts/Build-<Domain>Samples.ps1` 及一键启动脚本中的构建接入；
7. 框架测试、契约测试，以及需要时的前端测试；
8. `docs/<DOMAIN>-SAMPLES.md` 设计记录；
9. 对真实 EDR 导出字段的脱敏字段目录与示例。原始 `reference/` 只用于本地研究，不纳入 Git。

能力名称以中文为主并同时保存英文，例如“文件创建（File Creation）”。ID 使用稳定的小写点分格式，例如 `win.file.create`；已经发布的 ID 不因产品字段名变化而修改。

## 2. 先设计 BASELINE

### 2.1 本地条件是绝对基准

本地运行日志由 Controller、Behavior 和独立观察共同产生，应视为高可用、确定性的事实来源。设计样本前先列出能够精确采集的本地事实，至少包括：

- Actor、Target、Helper 的实际 PID、父 PID、绝对路径、完整命令行和进程起止时间；
- 紧邻行为 API 调用的 `occurred_at_utc`，以及 Controller 的观察时间；
- 行为对象的唯一名称或路径、操作前快照、操作后快照；
- 文件大小与哈希、注册表原始值哈希、端点、任务路径等领域专属证据；
- API 返回值之外的独立复核结果；
- 本轮 nonce 及其与对象、命令行或协议握手的关系。

`local_requirements` 只引用本地导出中的确定字段。不得使用 EDR `Action.Name`、表名或厂商字段判断本地行为是否成立。本地字段异常应明确显示为失败，但比较器仍应利用剩余本地锚点继续寻找云端候选，不能因为单个字段缺失而提前终止整个能力的比较。

### 2.2 云端候选和断言

云端 `cloud_expectations` 的设计顺序为：

1. 先用本地行为时间建立候选时间窗；
2. 再以对象路径、程序路径、PID、父子关系等本地锚点关联；
3. 最后检查能力专属字段、哈希、状态或动作语义；
4. 保留时间相近且程序、对象部分吻合的低置信候选，供人工查看完整 JSON；
5. 多个候选按综合置信度排序，时间越接近应得到更高分。

单事件强关联时间默认 `max_time_difference_ms: 15`，前端允许调整。无强关联事件的候选搜索上限默认 1 秒，也允许调整。时间差以本地行为时间为零点：负值表示 EDR 事件提前，正值表示延后。

`Action.Name` 是 EDR 日志的可选消歧条件，可以为同一能力配置多个值；它不能代替时间、对象和进程关联，也不影响本地规则。`Child.FileCreateOpName` 只用于五项文件能力。两者留空时不得改变原有候选搜索规则。

### 2.3 多方法结论

先区分两类“方法”：

- 执行子测试：不同文件类型、API 或触发路径会产生不同本地行为，必须分别执行和采集；
- EDR 检测方法：同一个本地行为可能同时由 API Hook 和 Windows Event Log 证明，只需一份本地行为，但要有多个云端 expectation。

执行子测试必须拥有独立的：

- 稳定方法 ID 和中英文名称；
- 本地事实前缀；
- 云端 expectation；
- 本地要求、EDR 要求、候选列表和方法结论。

执行子测试必须按声明顺序严格串行触发。框架级 `inter_subtest_delay_ms` 默认 1000 ms，可在 0–10000 ms 内调整；Controller 必须在上一子测试的行为结果、本地事件和事实已经保存后调用 `SubtestTiming.WaitBetween`，等待结束后才放行下一行为。Actor、Target 或 Helper 可以为了独立观察继续驻留，但驻留期间必须处于等待状态，不能提前触发下一子测试。实际间隔由 Runner 保存为 `execution.inter_subtest_delay_ms`；`SUBTEST_WAITING` Controller 日志应包含已完成方法、下一方法和等待毫秒数。能力清单的执行超时必须显式计入 `(子测试数 - 1) × 最大间隔`，不得由 Runner 无条件放宽所有能力的超时。

EDR 检测方法可以共享同一组本地事实和时间锚点，不能为了界面分组伪造额外 Actor 或本地事件。两类方法都必须拥有独立的云端 expectation 和方法结论。

BASELINE 使用 `method_selection: { strategy: best }` 时，能力结论默认采用通过情况最好的方法，并在界面明确提示。不得把多个方法的要求混成一组后共同失败。

## 3. 程序编排边界

Runner 只启动 Controller。Controller 负责参数解析、启动测试、监控进程、保存 SQLite、独立验证和清理；Actor 负责执行能力行为；Target 是被操作对象；Helper 提供受控服务端、客户端或系统侧辅助证据。Actor、Target、Helper 不直接访问运行数据库。

编译后的能力包位于本地 `samples/<capability-id>/`，不纳入 Git：

```text
samples/win.process.create/
  capability.json
  ProcessCreate.Controller.exe
  ProcessCreate.Actor.exe
  ProcessCreate.Target.exe
  *.dll
```

Controller、Actor、Target、Helper 应生成可区分的 EXE；确需由同一 Behavior EXE 承担多个角色时，必须在命令行、`role`、`instance_name` 和本地事实中区分。

多个“日志检查方法”不一定需要启动多个进程。例如镜像加载由一组长期存活的 Actor、Target、Helper 执行多个加载动作，可以只记录一组程序实例并为每次加载写独立事件。若每种方法都重新启动 Actor，则每个 Actor 都必须分配独立实例序号。

## 4. Controller 固定参数与路径

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

Controller 使用 SDK 解析并打开数据库：

```csharp
var invocation = ControllerInvocation.Parse(args);
using var database = RunDatabase.OpenReadWrite(invocation.RunDb);
var package = CapabilityCatalog.Load(invocation.ManifestPath);
var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))!.AsObject();
```

参数通过独立 JSON 文件传递。程序必须从 `PackageDirectory` 解析参与者，从 `WorkDir` 创建临时文件，不能依赖当前目录。所有行为对象使用 nonce 派生的本轮唯一名称；操作前若对象已存在，应拒绝覆盖或使用经过审计的可逆方案。

## 5. SQLite 唯一序号规范

每项能力对应一个 `case_run_id`。以下唯一键由数据库强制执行，开发时不得依赖模型属性的默认序号：

| 数据 | 唯一键 | 编号规则 |
| --- | --- | --- |
| 能力 | `(run_id, sequence_number)` | Runner 按参测顺序从 1 分配 |
| 程序实例 | `(case_run_id, role, instance_index)` | 每个角色分别从 0 开始，稳定方法顺序递增 |
| 本地事件 | `(case_run_id, sequence_number)` | 整项能力共用一个序列，从 1 开始 |
| 本地事实 | `(case_run_id, fact_key)` | 方法或阶段必须进入事实键前缀 |
| 工件 | `(case_run_id, relative_path)` | 文件名必须包含方法或阶段 ID |
| 清理结果 | `(case_run_id, sequence_number)` | 整项能力共用一个序列，从 1 开始 |

同一个方法中的 `actor:0`、`target:0`、`helper:0` 不冲突，因为角色不同。同一能力启动第二个 Actor 时必须使用 `actor:1`；`instance_name` 记录稳定方法 ID，便于导出和排查。Controller 本身由 `CaptureCurrent(..., "controller")` 写入一次，使用 `controller:0`。

推荐的多方法写法：

```csharp
foreach (var (method, methodIndex) in methods.Select((value, index) => (value, index)))
{
    var actor = ObserveActor(method, instanceIndex: methodIndex);
    database.AddProgram(actor);

    database.AddEvent(new LocalEventObservation
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = methodIndex + 1,
        // 其余字段省略
    });

    database.AddCleanup(new CleanupObservation
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = methodIndex + 1,
        // 其余字段省略
    });
}
```

若一种方法产生多个事件，应先设计整项能力的全局事件序列，显式分配每个阶段，允许为语义保留序号但禁止重复。异常清理也必须知道哪些清理结果已经入库，不能在 `catch` 中再次写入相同序号。

## 6. 本地写入与自验证

最小写入流程为：

1. 写 Controller 程序实例；
2. 执行各方法并写 Actor/Target/Helper；
3. 保存原子协议结果和证据工件；
4. 写本地事件与方法级事实；
5. 用 Controller 的独立观察验证结果；
6. 精确清理并记录清理前后状态；
7. 写能力级事实和最终状态。

`LocalEventObservation.Data` 必须符合 `schemas/local-event-data.schema.json`，且 `data.kind == event_type`、`data.operation == event_action`。事件应指向实际 `ActorProgramId`，需要时同时指向 `TargetProgramId` 和工件 ID。

事实键使用稳定层级，例如：

```text
file.json.actor_pid
registry.run_key_native.occurred_at_utc
group_policy.known_policy_same_value.after.value_data_sha256
```

能力级 `*.operation_succeeded` 不能替代方法级事实。API 返回成功也不能单独判定本地通过，必须读取对象状态、进程状态、文件内容、双端握手或系统查询进行独立复核。

## 7. 跨进程协议

EDR 或防病毒软件可能在结果 JSON 生成后短暂独占文件。所有官方样本统一复用 `sample-src/Common/ReliableProtocolFile.cs`：

- 写入唯一临时文件并落盘，再原子替换目标；
- 读取使用 `FileShare.ReadWrite | FileShare.Delete`；
- 对共享冲突、权限冲突和尚未完整解析的 JSON 有界重试；
- 失败路径尽力删除协议临时文件。

不得自行用命名互斥锁解决外部扫描器的文件占用；互斥锁只能协调主动遵守同一协议的进程，无法约束 EDR 或防病毒文件句柄。

## 8. 安全、权限和清理

风险等级必须反映真实副作用：

- L0：普通用户、无持久系统改动；
- L1：可控且可逆的用户态改动；
- L2：管理员权限、系统配置或其他高权限可逆行为；
- L3：高破坏性或难以可靠恢复的行为，原则上不进入默认套件。

L2/L3 默认由 Runner 跳过，只能显式允许高风险后执行。构建/启动脚本检测到非管理员运行时，应提示账号、服务、计划任务、组策略、WMI、虚拟磁盘和驱动等能力可能不可用。驱动能力还必须在 Controller 内验证签名包 SHA256、证书信任和 testsigning；环境不就绪应封存为 `SKIPPED / ENVIRONMENT_NOT_READY`，不能计作 EDR 失败。完整约束见 `docs/DRIVER-ACTIVITY-SAMPLES.md`。

清理必须满足：

- 只删除本轮 nonce 对象，不使用宽泛路径或通配符；
- 设备类样本只操作 Controller 创建并验证完整路径的本轮对象；虚拟磁盘不得初始化、格式化或分配盘符，错误路径必须先按精确镜像路径卸载再删除；
- 修改前保存类型、原始字节、长度和哈希，恢复后再次读取确认；
- 对已有系统对象优先同值回写，无法证明可逆时跳过；
- 进程树、句柄、监听端点和临时文件都要纳入清理；
- 清理失败写 `CLEANUP_ERROR`，Runner 停止后续能力；
- 错误路径与正常路径使用同一套幂等清理函数。

## 9. EDR 字段研究与映射

平台不连接 EDR API，用户自行导入 EDR 平台导出的 JSON。实现新能力前应先从已有导出中整理：

- 表名和 `Action.Type`、`Action.Name`；
- 事件时间、主机、Actor、Target、对象、结果和哈希字段；
- 同一字段的空值、格式差异和多个示例；
- 一条原始记录能够直接证明什么，不能证明什么。

字段目录保存到 `docs/reference/`，必须脱敏且保留字段类型和代表性示例。Mapping 将厂商字段转换为 Canonical 字段；BASELINE 只引用 Canonical 字段。候选发现 route 可以放宽，但直接证明能力的 route 必须保留明确语义，不能把“同一时间出现过相关日志”伪装成能力通过。

真实日志校准时只调整已有设计能够解释的字段、时间选点和格式归一化。不得仅因为某次导出中出现 `Action.Name` 就让它成为唯一候选入口；可将其保存为前端默认消歧值。

### 9.1 强制字段基准

`docs/reference/tencent-edr-field-catalog.json` 是设计后续腾讯 EDR BASELINE 时唯一允许引用的厂商字段基准，替代旧的 260808 field-catalog；`reference/EDR日志-字段表.txt` 是不追踪的人工阅读镜像。每个字段均保存中文含义、脱敏示例、实际出现的表/动作、是否已观测和 `baseline_role`。

新增 BASELINE 前必须执行：

1. 在字段基准中确认原字段存在，并阅读 `meaning_zh` 和实际 `action_names`；
2. `observed=false` 时先取得包含该字段的新 EDR 导出，不能直接写 `required`；
3. 按 `baseline_role` 判断它适合时间锚点、候选过滤、关联锚点、能力断言还是仅诊断；
4. 在 Mapping Profile 中映射到 Canonical 字段，BASELINE 不直接引用腾讯原字段；
5. 新 run 出现类型、枚举或语义变化时，先运行 `scripts/Export-TencentEdrFieldCatalog.ps1` 更新基准，再调整 mapping 和 BASELINE。

## 10. 前后端接入

后端应保持通用的清单发现、SQLite 导出和 BASELINE 比较。只有出现新的事件类型、操作、字段规范化或多事件关系时，才扩展 Schema、mapping 和比较器。

前端接入至少检查：

- 能力大类和中英文名称是否正常展示；
- 测试队列是否展示各方法的本地 PID、起止时间、对象和结果；
- BASELINE 与比较结果是否按能力大类、能力和方法分层；
- 最佳方法是否默认展开并给出提示；
- EDR 要求能独立滚动，长 JSON 不被截断；
- 候选块能打开左右 JSON 对比窗，高亮命中字段和两侧时间戳；
- 默认 `Action.Name` 和文件专属 `Child.FileCreateOpName` 是否只作用于 EDR 筛选；
- 进度是否按完成能力数除以参测能力总数实时更新。

通用界面能自动读取清单和 BASELINE 时不要增加能力硬编码。确需默认消歧、中文文案或特殊多事件展示时，应同时增加前端测试。

## 11. 项目引用、构建和版本

.NET 样本直接引用框架项目：

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\EdrTest\EdrTest.csproj" />
</ItemGroup>
```

调试阶段可省略清单程序 `sha256`；固定交付时填写 Controller 和参与者摘要。能力行为、事实键或参数变化时提升 manifest 版本，并同步 BASELINE 的 `capability.version`；BASELINE 规则自身变化时提升 BASELINE `version`。构建脚本应覆盖本地旧能力包，避免测试机混用旧 EXE 和新清单。

本地运行示例：

```powershell
dotnet run --project src/EdrTest -- capabilities --root samples

dotnet run --project src/EdrTest -- run `
  --capability win.process.create `
  --samples-root samples `
  --runs-dir runs
```

一轮可选择一个或多个能力，Runner 串行执行并为每轮生成独立 `.db`。

## 12. 必须通过的验收

提交前至少执行：

```powershell
dotnet build EdrTest.sln --configuration Release --no-restore
dotnet run --project tests/EdrTest.Tests/EdrTest.Tests.csproj --configuration Release --no-build
node --test tests/contract/local-run-contract.test.mjs
```

修改前端时还要运行前端测试；修改样本后必须运行对应 `Build-<Domain>Samples.ps1`，确认生成包中的 EXE 是最新版本。

验收清单：

1. 清单、BASELINE、mapping 和本地事实使用同一能力/方法 ID；
2. 多方法的程序实例、事件、工件、事实和清理均无唯一键冲突；
3. 本地成功由独立观察确认，不由 EDR 字段决定；
4. 本地失败、Actor 异常和超时都能执行幂等清理；
5. 原始协议与详细日志完整保存，不截断；
6. 强匹配、弱候选、多个候选和完全无日志四种情况均能输出结论；
7. 最佳方法选择不会折叠或隐藏其他方法；
8. 构建在全新机器的允许依赖策略下可完成；
9. `git diff --check` 通过，`reference/`、`samples/` 和本地运行目录未被追踪。

## 13. 既有多子测试序号审计

2026-08-20 对当前已实现的多子测试能力完成如下审计：

| 能力 | 程序实例 | 事件/清理 | 结论 |
| --- | --- | --- | --- |
| 加载镜像或动态库 | 一组 Actor/Target/Helper 执行多个加载动作；原生四方法完成后才放行托管 Helper | 每个加载动作使用独立事件序号和统一方法间隔 | 无重复程序入库、无行为交叉 |
| 五项文件能力 | TXT/JSON Actor 使用 0/1 | 事件和清理使用 1/2 | 符合规范 |
| URL、DNS 等网络方法 | 各方法 Actor/Helper 使用相同方法索引 0/1 | 各角色分别唯一；事件和清理显式编号 | 符合规范 |
| 三项注册表能力 | 隔离键/Run Key Actor 使用 0/1 | 事件和清理使用 1/2 | 符合规范 |
| 三项计划任务能力 | COM Actor 使用 0；原生 CLI Actor/Helper 使用 1；安全审计 Actor/Helper 使用 2 | 事件和清理使用 1/2/3 | 符合规范 |
| 三项服务能力 | 单次 SCM 行为只写一个 Actor；API Hook/Event Log 是 EDR 检测方法 | 单个本地事件和清理 | 无重复入库 |
| 三项哈希能力 | 每项只有一个文件行为 Actor；JSON/EXE 是能力间或单方法标签 | 单个本地事件和清理 | 无重复入库 |
| 组策略修改 | 隔离策略/L2 Actor 使用 0/1 | 事件和清理使用 1/2 | 已修复默认序号冲突 |

契约测试会检查这些 Controller 是否继续显式分配序号并调用统一间隔；框架测试会实际向 SQLite 写入同角色多程序、多事件和 Runner 间隔事实，防止以后回归。
