# 驱动活动三项能力设计与实现

## 1. 实现边界

三项能力具有相同的工程完整度：每项都有 L3 能力清单、Controller、Actor、本地事件、SQLite facts、清理证据、BASSLINE、Canonical 映射和理论测试。差异只体现在当前腾讯 EDR 的预期结果：

| 能力 | 本地绝对基准 | 云端直接证据 | 当前预期 |
| --- | --- | --- | --- |
| 驱动加载 | SCM 为 running，且 `K32EnumDeviceDrivers` 找到同名模块和基址 | `ModuleEvents` + `Action.Name=LoadDriver` | 尽量复现并通过 |
| 驱动修改 | 从未加载的工作副本，前后 MD5/SHA256/大小/时间改变且标记存在 | 未来专属 DriverModify/ModifyDriver | 本地通过，EDR 失败 |
| 驱动卸载 | 预置加载已确认；等待至少 2 秒后 STOP；模块消失而服务暂留 stopped | 未来专属 DriverUnload/UnloadDriver | 本地通过，EDR 失败 |

普通 `FileWriteClose`、服务创建/删除、预置 `LoadDriver` 均不能替代驱动修改或卸载的直接事件。三个 BASSLINE 的 `cardinality.min` 都保持为 1，不在代码中硬编码产品失败。

## 2. 最小驱动

`drivers/EdrTestDriver` 是 x64 WDM 项目。仓库直接版本化保存已签名的 x64 SYS/CAT、INF、元数据和仅含公钥的 CER，普通测试机无需安装 EWDK。`F:\EWDK` 只用于维护预构建样本，或者在仓库样本缺失/损坏时作为启动流程的后备构建环境。内核代码只包含：

- `DriverEntry` 返回成功并登记 `DriverUnload`；
- `DriverUnload` 不执行资源操作；
- 不创建设备和符号链接；
- 不暴露 IOCTL，不注册进程、对象、注册表、文件系统或网络回调；
- 不读取、修改或持久化用户数据。

驱动源码与 INF 可审计；`bin`、`obj`、PDB 和私钥不提交。`prebuilt/x64` 只保存 SYS、INF、CAT 和不含秘密的 `driver-package.json`。PFX、PVK、PEM、私钥及密码禁止进入仓库。

## 3. 本地编排

所有资源都绑定本轮 nonce：服务名为 `EdrTestDrv_<nonce>_<operation>`，镜像位于该 case 的 `work` 目录。Actor 在执行前重新验证：

1. 服务名只能使用 `EdrTestDrv_` 前缀和字母数字/下划线；
2. 镜像必须是当前 `work` 目录内的 `.sys`；
3. Controller 从包复制后立即复核 SHA256；
4. 清理只操作精确服务名和精确工作副本，不做前缀扫描或批量删除。

加载通过 `CreateServiceW(SERVICE_KERNEL_DRIVER, DEMAND_START)` 后在调用 `StartServiceW` 前记录本地关联时间。卸载在调用 `ControlService(STOP)` 前记录时间。修改在追加确定性 ASCII 标记前记录时间。Controller 使用独立 SCM 查询、模块枚举和文件哈希复核 Actor 结果。

模块枚举的 P/Invoke 固定使用 `kernel32.dll + K32EnumDeviceDrivers/K32GetDeviceDriverBaseNameW/K32GetDeviceDriverFileNameW`。`psapi.dll` 只导出不带 `K32` 前缀的兼容入口，禁止把 `K32*` 名称声明到 `psapi.dll`，否则运行时会触发 `EntryPointNotFoundException`。

卸载使用两个 Actor 实例，`instance_index=0` 负责预置加载，`instance_index=1` 负责卸载，避免 `program_instance` 唯一键冲突。预置加载完成后至少等待 2000 ms，默认 2200 ms，使 `LoadDriver` 落在默认 1 秒候选范围之外。

## 4. 环境不就绪语义

以下任一条件不满足时，Controller 返回退出码 10，并把能力封存为 `SKIPPED / ENVIRONMENT_NOT_READY`，不计作 EDR 能力失败：

- Windows x64、管理员权限和 L3 显式确认；
- 能力包中 SYS 与 `driver-package.json` 的 SHA256 一致；
- 加载/卸载所用包已签名；
- 公开证书同时存在于 `LocalMachine\Root` 和 `LocalMachine\TrustedPublisher`；
- 元数据要求测试签名时，当前启动项已经启用 `testsigning` 并完成必要重启。

修改样本不加载驱动，因此只要求管理员、精确包和哈希，不要求测试签名模式已生效。

## 5. 腾讯 EDR BASSLINE

`reference/driver_and_usb/edr_log_loaddriver.json` 的稳定特征为：

- `@table=ModuleEvents`、`Action.Type=Module`、`Action.Name=LoadDriver`；
- `Common.Source=KernelMon`、`Common.MonitorName=加载驱动`；
- 关联时间使用 `Common.EventTime`，而不是 `@timestamp` 或 `@collection`；
- `Parent.ProcPid=0`、`Parent.FilePath=SystemIdle`，所以不要求测试 Actor PID/路径；
- 核心字段为 `Child.FilePath`、`FileName`、`FileMd5`、`FileSize`、`ModuleBase`、`ModuleSize`；本地分别保存文件字节数与 PE `SizeOfImage`，不得把两种大小混用；
- `Child.ModuleBase` 是有符号 int64，映射时按二进制位保持不变并输出 16 位无符号 `0x` 十六进制；
- 签名主体和状态作为推荐/信息字段，不替代路径、MD5、大小、基址和 15 ms 时间证据。

加载的默认前端 `Action.Name` 消歧值为 `LoadDriver`。修改与卸载默认留空，保持“留空不筛选”的既有逻辑。

## 6. 构建与启动环境检查

普通测试机直接使用仓库预构建包：

```text
drivers\EdrTestDriver\prebuilt\x64\EdrTestDriver.sys
drivers\EdrTestDriver\prebuilt\x64\EdrTestDriver.cat
drivers\cert\EdrTestDriverTest.cer
```

平台启动顺序为：先验证仓库 SYS/CAT/元数据/公开证书及指纹；仓库包不可用时才探测 `-EwdkRoot`（默认 `F:\EWDK`）并在 `.edr-test\driver-fallback` 尝试本地签名构建；两条路径都失败时，只删除并跳过三个驱动能力包，平台其他能力继续启动。

维护者的理论构建（不加载驱动，也不会覆盖仓库预构建包）：

```powershell
pwsh -File .\script\driver\Build-DriverPackage.ps1 -EwdkRoot F:\EWDK -Configuration Release
pwsh -File .\scripts\Build-DriverActivitySamples.ps1 -Configuration Release
pwsh -File .\scripts\Test-DriverActivitySamples.ps1 -EwdkRoot F:\EWDK
```

更新仓库测试证书和签名包不需要修改 BCD，但会在维护者当前用户证书存储区创建不可导出的私钥：

```powershell
$cert = & .\script\driver\New-DriverTestCertificate.ps1
& .\script\driver\Build-DriverPackage.ps1 -EwdkRoot F:\EWDK `
  -CertificateThumbprint $cert.Thumbprint -UpdatePrebuilt
```

启动平台时会检测管理员权限、当前启动项的 `testsigning`、驱动包签名以及公开证书信任状态：

```powershell
pwsh -File .\scripts\Start-EdrTest.ps1
```

当签名包、`drivers\cert\EdrTestDriverTest.cer` 和元数据指纹一致，但证书尚未完整导入时，交互式启动会询问：

```text
是否导入测试用证书到 LocalMachine\Root 和 LocalMachine\TrustedPublisher？[y/N]
```

用户确认后才会导入公开证书。自动化环境可以用 `-DriverCertificateImportMode Always` 明确允许导入，或用 `Never` 禁止询问和导入；默认值为 `Prompt`。平台不会执行 `bcdedit /set testsigning on`、安装 INF 或自动重启。

`testsigning` 必须由测试机管理员在隔离环境中自行开启并重启，例如：

```powershell
bcdedit /set testsigning on
```

若未以管理员身份启动、未开启 `testsigning`、驱动包未签名、公开证书缺失或证书不受信任，启动过程仍会继续，但会明确提示驱动能力不可用；Controller 运行时仍以 `SKIPPED / ENVIRONMENT_NOT_READY` 封存，不会计作 EDR 检测失败。Secure Boot 或组织策略可能阻止测试签名模式，平台不会尝试绕过这些安全策略。

## 7. 手工验收

建议只在有快照的专用 VM 中执行，选择三项能力并显式确认 L3。验收顺序：

1. 以管理员身份启动平台，确认启动日志显示 `testsigning=已开启`、`测试证书=已导入`；
2. 导入同一主机、同一测试时间窗的 EDR JSON；
3. 加载应优先找到路径、MD5、大小和时间均一致的 `LoadDriver`；
4. 修改和卸载应显示本地条件通过，但在当前产品日志中保持 EDR 未满足；
5. 每项结束后确认服务不存在、驱动未加载、工作副本已删除；任何清理失败都必须是 `CLEANUP_ERROR`，并停止继续执行高风险步骤。
