# Service Activity 三项能力样本

## 1. 实现范围

本能力包实现 Windows 服务创建（Service Creation）、服务修改（Service Modification）和服务删除（Service Deletion）。三项都采用 Controller + Actor 编排：Controller 分配本轮唯一服务、记录进程与本地事实并负责兜底清理；Actor 调用原生 Service Control Manager（SCM）API 完成实际行为。

| 能力 | 原生行为 | 本地绝对基准 | 云端候选路径 |
| --- | --- | --- | --- |
| 服务创建 | `CreateServiceW` | 创建前不存在；创建后名称、显示名、Binary Path、禁用启动、LocalSystem、独立进程服务、停止状态全部一致 | SCM API Hook；Windows System 7045 |
| 服务修改 | `ChangeServiceConfigW` | 同一服务由手动启动改为禁用，同时修改显示名和 Binary Path 标记；服务保持停止 | SCM API Hook；Windows System 7040 |
| 服务删除 | `DeleteService` | 删除前完整存在；删除后 SCM 查询为不存在 | SCM API Hook |

创建和修改采用 `method_selection: best`：API Hook 与 System Event Log 是 EDR 可能采用的两条采集路径，比较器分别显示两种方法，并以通过情况最好的方法作为能力结论。删除没有与 7045/7040 对等且稳定的 System 删除事件，因此首版只把 `DeleteService` API Hook 作为直接能力证据。

## 2. 安全边界

- 三项能力均为 `L1`，要求管理员权限；非管理员运行时 Runner 会跳过，启动与构建脚本会显示中文提示。
- 服务名必须以 `EdrTestSvc_` 开头，只包含字母、数字和下划线；显示名必须以 `EDRTEST|` 开头。
- Binary Path 只允许系统目录下 `cmd.exe` 的 `/d /c exit 0` 或 `/d /c rem EDRTEST_...` 无害命令。
- 只允许 `demand` 与 `disabled` 两种启动类型。测试服务从不启动，也不会执行其 Binary Path。
- 修改、删除的预置服务以及最终清理都使用本轮精确服务名；不枚举、不停止、不修改其他服务。
- Controller 在 Actor 之后独立调用 `QueryServiceConfigW` 和 `QueryServiceStatusEx` 验证结果；清理后再次等待 SCM 确认对象消失。

## 3. 本地证据

每项能力写入一个 `service/<operation>` 本地事件、一个 Actor 程序实例、一个原子写入的行为协议文件和一条精确清理记录。`facts.service.*` 至少包括：

- 行为是否成功、开始/完成 UTC、原生 API；
- 服务名、显示名、Binary Path；
- Actor PID、可执行文件与命令行；
- 操作前后 `exists/display_name/binary_path/start_type/account/service_type/state`；
- 创建的 System 7045、修改的 System 7040 本机诊断结果与原始查询输出。

本地 SCM 查询是绝对基准。System 日志诊断用于解释 EDR 的采集路径，即使本机日志未开启、读取受限或事件尚未落盘，也不会覆盖已经成功的 SCM 本地结论。

## 4. 腾讯 EDR 首版映射

现有 `reference/` 导出中发现 8 条 `ServiceEvents / StartService / InjectHook` 记录，证明产品存在服务 API Hook 表，已确认的相关原始字段包括 `Child.ServiceName`、`Child.StartType`、`Child.FilePath` 及完整 Parent 调用链；但当前参考资料没有 `CreateService`、`ChangeServiceConfig`、`DeleteService`、7045 或 7040 目标事件。

因此 `tencent-edr-proc-events-v1` 当前同时提供：

1. `ServiceEvents` 下 SCM 原生 API 名的兼容路由；
2. 7045/7040 的 System Event Log 路由；
3. `ServiceEvents` 候选发现路由，便于在目标动作名未知时仍展示时间和服务名接近的低置信 JSON 块。

服务名称、显示名、Binary Path、启动类型、账号和服务类型配置了常见 Child 字段别名。这些目标动作与别名属于待实测校准项；拿到新一轮完整导出后，应优先根据真实 `Action.Name` 和实际字段收敛路由及 BASELINE，而不能把首版假设当成产品结论。

前端“可选消歧”预置以下 Action.Name，可由用户编辑并保存：

- 创建：`CreateService, CreateServiceW, RpcCreateService, ServiceInstall`
- 修改：`ChangeServiceConfig, ChangeServiceConfigW, ServiceConfigChange`
- 删除：`DeleteService, DeleteServiceW`

这些筛选只作用于 EDR 候选，不影响本地运行规则。

## 5. 构建与验证

```powershell
pwsh -NoProfile -File scripts/Build-ServiceActivitySamples.ps1
pwsh -NoProfile -File scripts/Test-ServiceActivitySamples.ps1
```

构建脚本会覆盖 `samples/win.service.*` 旧能力包并写入 EXE SHA-256。端到端脚本要求管理员权限，会真实执行三个安全样本，确认本地事实、精确清理以及通用/腾讯两套映射的离线比较闭环。

## 6. 参考

- Microsoft `CreateServiceW`、`ChangeServiceConfigW`、`DeleteService`、`QueryServiceConfigW` 文档
- Windows Security 4697：A service was installed in the system
- Windows System 7045：A service was installed in the system
- Windows System 7040：The start type of a service was changed
- 腾讯 EDR 行为采集说明与本仓库脱敏导出样本
