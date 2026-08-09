# Process Activity 能力样本

## 1. 已实现范围

Process Activity 已实现六个 Windows 能力包。每个能力包对外均提供独立命名的 Controller、Actor 和 Target 三个 EXE；镜像加载能力还会启动系统 `dotnet.exe` 作为 Helper。源码共享协议层与行为实现，避免六套重复代码产生漂移。

| 能力 ID | 中文 / English | 真实行为 | 风险 |
| --- | --- | --- | --- |
| `win.process.create` | 进程创建 / Process Creation | Actor 创建带 nonce 的 Target，Controller 核验 PID、父子关系与存活状态 | L0 |
| `win.process.terminate` | 进程终止 / Process Termination | Actor 对受控 Target 调用 `TerminateProcess`，Controller 核验退出码 | L1 |
| `win.process.access` | 进程访问 / Process Access | Actor 使用 `OpenProcess` 和 `QueryFullProcessImageName` 访问受控 Target | L0 |
| `win.process.image_load` | 加载镜像或动态库 / Image/Library Loaded | Target 执行三种原生 DLL 加载；`dotnet.exe` Helper 加载刚落盘的唯一命名托管程序集；Controller 对每个子项独立枚举 | L0 |
| `win.process.remote_thread` | 远程线程创建 / Remote Thread Creation | Actor 在受控 Target 中创建执行 `LoadLibraryW` 的远程线程 | L2 |
| `win.process.tampering` | 进程篡改活动 / Process Tampering Activity | Actor 在受控 Target 中分配缓冲区、写入 nonce 数据、回读哈希并释放内存 | L2 |

L2 样本默认不会执行，必须由用户显式提供 `--allow-high-risk`。远程线程和进程内存写入只接受本轮 Controller 创建的 Target PID，不提供任意进程选择参数。

## 2. 源码与能力包

仓库追踪可审计源码和清单模板：

```text
sample-src/ProcessActivity/
  ProcessActivity.Protocol/       # Controller/Behavior 的 JSON 协议
  ProcessActivity.Controller/     # 编排、独立观察、SQLite 写入和清理
  ProcessActivity.Behavior/       # Actor/Target 及受控 Win32 行为
  manifests/<capability-id>/      # 六份中英双语能力清单
```

编译后的包生成到被忽略的 `samples/`：

```powershell
pwsh -NoProfile -File scripts/Build-ProcessActivitySamples.ps1
```

构建脚本会先校验目标位于 `samples/` 根目录内，再删除对应旧能力目录并重新发布。因此版本升级会直接覆盖旧能力包，不会混用旧 EXE、DLL 或清单。

每个生成包形如：

```text
samples/win.process.create/
  capability.json
  ProcessCreate.Controller.exe
  ProcessCreate.Actor.exe
  ProcessCreate.Target.exe
  ProcessActivity.Controller.dll
  ProcessActivity.Behavior.dll
  ProcessActivity.Protocol.dll
  EdrTest.dll
  ...运行依赖
```

独立 EXE 名称用于 EDR 日志检索和能力关联；共享 DLL 是同版本实现，不影响三个进程拥有各自 PID、命令行、角色和哈希记录。镜像加载能力的 Helper 使用真实 `dotnet.exe` 路径、PID 和命令行单独记录。构建脚本会把三个 EXE 的 SHA-256 写入生成后的 `capability.json`，Runner 在启动前强制校验。

## 3. 一键端到端测试

以下命令会构建六个能力包，在同一轮中串行运行全部能力，验证 SQLite/JSON 证据，再用通用自测云端夹具执行六份 BASELINE：

```powershell
pwsh -NoProfile -File scripts/Test-ProcessActivitySamples.ps1
```

通过条件包括：

1. 轮次状态为 `COMPLETED`，六个能力均为 `LOCAL_PASS`；
2. 保存 19 个程序实例：每项各一个 Controller、Actor、Target，`image_load` 另有一个 `dotnet.exe` Helper；
3. 共保存 9 条高置信本地事件；其中 `image_load` 有 4 条子项事件，其余五项各 1 条；
4. 每项都有行为协议证据、nonce 事实和成功清理记录；
5. 进程篡改样本必须确认远程缓冲区已释放；
6. 六份 BASELINE 对自测夹具全部返回 `PASS`，不能出现 `PARTIAL`、`FAIL` 或 `INCONCLUSIVE`。

运行产物保存在：

```text
artifacts/process-activity-e2e/
  test-summary.json
  synthetic-cloud.process-activity.json
  validation-result.synthetic.json
  runs/<date>/<run-id>/<run-id>.db
  runs/<date>/<run-id>/export/local-run.json
```

`samples/`、`artifacts/` 和 `runs/` 均不纳入版本控制。

## 4. BASELINE 与真实 EDR 日志

六份厂商无关 BASELINE 位于 `baselines/windows/process_*.yaml`。`mappings/generic-process-activity-v1.yaml` 仅用于框架自测，文件内也明确标注不是厂商生产映射。

腾讯 EDR 映射已包含 `ProcessCreate`、`NtOpenProcess`、`LoadDll`、`RemoteThread` 和 `WriteProcessMemory` 路由。映射以当前参考导出的字段结构为准；产品版本或导出页面变化后，应先核对 `Action.Type`、`Action.Name` 和父子对象字段。平台不会直连腾讯 EDR，也不会把合成夹具结果表述为真实产品检出结果。

### 4.1 DLL 加载子项

`win.process.image_load@0.3.0` 一次执行四个子项，分别保存独立发生时间、目标角色与 PID、文件路径、文件名、加载方法、基址、大小和 SHA-256：

1. `system_loadlibrary`：使用绝对路径加载 `System32\winhttp.dll`，保留与旧版本相同的系统 DLL 场景；
2. `application_local_loadlibrary`：把 `version.dll` 复制为带本轮 nonce 的唯一文件名，再用 `LoadLibraryW` 从能力工作目录加载；
3. `application_local_loadlibrary_ex`：把 `dbghelp.dll` 复制为另一唯一文件名，再使用带安全搜索标志的 `LoadLibraryExW` 加载；
4. `managed_assembly_load_context`：由真实 `dotnet.exe` Helper 把 `ProcessActivity.Protocol.dll` 复制为 `edrtest_<nonce>_managed.dll`，再通过独立 `AssemblyLoadContext` 动态加载。

后三个临时 DLL 在 Target/Helper 停止后删除。唯一文件名能减少系统 DLL 白名单或高频模块降噪造成的干扰，也便于在 EDR 导出中直接按本轮路径检索。第 4 个子项复现 `reference` 中 0807 导出所见的产品检出形态：父进程为 `dotnet.exe`，被加载文件是刚写入磁盘的托管 DLL。离线 BASELINE 为四个子项分别定义关联锚点和发生时间，并输出四种方法的独立通过情况；能力结论默认采用结果最好的方法，未被采用的失败或低置信结果仍会保留，且不会被其他 DLL 日志替代。

比较器要求本地导出的 `capability_version` 与 BASELINE 的 `capability.version` 完全一致。旧运行（例如 `0.1.0`）没有对应基准时显示 `NOT_COMPARED` 和版本提示，不会再把新版新增条件误报为本地采集失败。

使用真实导出进行比较：

```powershell
dotnet run --project src/EdrTest -- compare `
  --local artifacts/process-activity-e2e/runs/<date>/<run-id>/export/local-run.json `
  --cloud import/tencent-edr.json `
  --mapping mappings/tencent-edr-proc-events-v1.yaml `
  --baseline baselines/windows/process_create.yaml `
  --out reports/validation-result.json
```

在没有真实云端导出时，本地 `LOCAL_PASS` 只证明行为成功发生且被 Controller 独立观察，不等于 EDR 产品能力已验证通过。
