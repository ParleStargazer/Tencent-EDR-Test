# USB 设备挂载与卸载能力样本

## 1. 能力边界

本模块接入两项设备操作能力：

- USB 设备挂载（USB Device Mount）：`win.device.usb.mount`
- USB 设备卸载（USB Device Unmount）：`win.device.usb.unmount`

这里的“挂载/卸载”指 USB 设备在 Windows PnP 树中的接入与移除，不等同于磁盘卷挂载。样本不会模拟 U 盘、不会创建文件系统、盘符或挂载点，也不会模拟键盘鼠标输入。`volume_guid`、`drive_letter` 和 `mount_point` 因此应为 `null`，不能用文件写入或卷事件替代本能力。

当前腾讯 EDR 已知字段与运行日志中没有 USB/PnP 设备接入、移除的直接事件。平台仍完整采集本地 ground truth，并为未来直接遥测定义映射；现阶段即使出现 `LoadDriver`、PnP 安装、注册表、进程或文件事件，也只能作为侧面记录，不能使 USB 能力判为 `PASS`。

## 2. 样本架构

模块由三层组成：

1. `UsbUdeTest.sys`：最小 x64 KMDF/UDE 驱动，根硬件 ID 为 `ROOT\USB_UDE_TEST`。驱动创建一个 UDE Host Controller，并仅开放管理员/SYSTEM 可访问的 Attach、Detach、Query 三个 IOCTL。
2. Actor：`UsbDeviceMount.Actor.exe` 或 `UsbDeviceUnmount.Actor.exe`。Actor 只向驱动发送受控 IOCTL，不安装驱动、不查询 BASELINE、不直接写数据库。
3. Controller：安装仓库驱动包、编排 Actor、使用 SetupAPI 独立查询 USB PnP Instance、写入 SQLite、导出证据并执行精确清理。

模拟 USB 子设备使用固定标识：

| 属性 | 值 |
| --- | --- |
| VID | `ED1D` |
| PID | `0001` |
| USB Class | vendor-specific interface，无数据端点 |
| 序列号 | `EDR_USB_` + 本轮 nonce 的 16–24 位十六进制片段 |
| 预期 Instance ID | `USB\VID_ED1D&PID_0001\EDR_USB_<nonce>` |

运行时序列号使不同轮次可以稳定区分，同时固定 VID/PID 便于 EDR 字段映射和人工复核。

## 3. 两项测试流程

### 3.1 USB 设备挂载

1. Controller 校验管理员权限、x64、`testsigning`、公开证书信任、SYS 签名与 SHA-256。
2. 将 INF/SYS/CAT 安装到 Driver Store，创建受控 ROOT devnode，等待 UDE 控制接口出现。
3. 确认本轮序列号对应的 USB PnP Instance 不存在。
4. 启动操作 Actor，Actor 发送 Attach IOCTL；驱动调用 `UdecxUsbDevicePlugIn()`。
5. Controller 使用 SetupAPI 独立确认固定 VID/PID 和本轮序列号对应的 Instance 出现。
6. 写入事件、事实、Actor 原始 JSON 和清理结果。
7. Detach 子设备，删除 ROOT devnode，并仅在本轮新暂存了 INF 时删除对应 Driver Store 包。

本地通过必须同时满足 `before_present=false`、`after_present=true`、Actor IOCTL 成功和 Controller PnP 复核成功。

### 3.2 USB 设备卸载

1. 完成与挂载相同的环境检查和 UDE 驱动安装。
2. 启动准备 Actor（`instance_index=0`）执行 Attach，并由 Controller 确认 Instance 已出现。
3. 等待准备/操作隔离窗口，驱动保持加载。
4. 启动操作 Actor（`instance_index=1`）发送 Detach IOCTL；驱动调用 `UdecxUsbDevicePlugOutAndDelete()`。
5. Controller 使用 SetupAPI 独立确认同一 Instance 消失。
6. 写入操作事件和事实；准备阶段只能作为本地前置证据，不能冒充卸载云端事件。
7. 删除 ROOT devnode和本轮新暂存的 Driver Store 包。

本地通过必须同时满足准备 Attach 成功、`before_present=true`、`after_present=false`、Detach IOCTL 成功和 Controller PnP 复核成功。

准备 Actor 与操作 Actor 使用不同的 `program_instance.instance_index`，避免同一 `case_run_id` 内程序实例唯一键冲突。

## 4. BASELINE 与映射

BASELINE 文件：

- `baselines/windows/device_usb_mount.yaml`
- `baselines/windows/device_usb_unmount.yaml`

本地绝对基准至少包括：环境可用、驱动签名有效、方法 ID、操作起止时间、Instance ID、Class GUID、VID/PID、序列号、操作前后状态、IOCTL 结果、Actor PID/路径/命令行以及 Controller 独立复核结果。

时间锚点以 Actor 发起 Attach/Detach IOCTL 前的 `usb.occurred_at_utc` 为准，强关联默认 15 ms。直接云端事件还必须匹配 `device.instance_id`、`device.serial_number`、`device.vendor_id` 和 `device.product_id`；任何缺少 USB 设备直接语义的侧面事件均不能满足 required assertions。

映射文件：

- `mappings/generic-usb-device-activity-v1.yaml`：测试与未来适配使用的通用直接事件。
- `mappings/tencent-edr-proc-events-v1.yaml`：仅规划明确 USB/PnP 事件表和接入/移除动作；不把 `LoadDriver`、`ProcessCreate`、`FileWriteClose` 或注册表事件路由成 USB 能力。

## 5. 预构建驱动与证书

仓库分发：

- `drivers/UsbUdeTest/prebuilt/x64/UsbUdeTest.sys`
- `drivers/UsbUdeTest/prebuilt/x64/UsbUdeTest.inf`
- `drivers/UsbUdeTest/prebuilt/x64/UsbUdeTest.cat`
- `drivers/UsbUdeTest/prebuilt/x64/usb-driver-package.json`
- `drivers/cert/EdrTestDriverTest.cer`

`.cer` 只包含公钥，私钥保留在驱动构建机的证书存储中且不可导出。普通测试机无需安装 EWDK；平台启动时优先验证仓库预构建包，缺失或损坏时才尝试 `F:\EWDK`，该路径也可通过 `-EwdkRoot` 覆盖。

平台不会自动开启 `testsigning`。启动时会报告管理员、当前启动项 `testsigning`、包签名和证书信任状态；导入公开证书需要用户确认。条件不满足时，两个 USB 能力包会被跳过，其他能力继续可用。

## 6. 构建与验证

构建能力包：

```powershell
pwsh -NoProfile -File .\scripts\Build-UsbDeviceActivitySamples.ps1
```

在驱动开发机重建并更新仓库预构建包：

```powershell
pwsh -NoProfile -File .\script\driver\Build-UsbUdeDriverPackage.ps1 `
  -EwdkRoot F:\EWDK `
  -CertificateThumbprint <CurrentUser\My 中带私钥的证书指纹> `
  -UpdatePrebuilt
```

执行完整验证：

```powershell
pwsh -NoProfile -File .\scripts\Test-UsbDeviceActivitySamples.ps1
```

若当前会话缺少管理员权限、`testsigning` 或双信任区证书，验证脚本会完成构建与静态检查后明确 `SKIP` 原生 PnP 测试，不会修改 BCD 或自动导入证书。环境就绪时，脚本依次运行挂载和卸载、核对 SQLite/JSON 本地证据、验证严格清理、用通用直接事件得到两项 `PASS`，再确认腾讯侧只有驱动加载等侧面证据时两项均不能 `PASS`。

## 7. 安全与清理约束

- 仅支持 x64 Windows 10/Server 2019 及以上。
- 两项能力均为 L3，Runner 必须收到显式高风险授权。
- IOCTL 设备接口仅允许 Administrators 和 SYSTEM。
- 驱动只模拟单个 vendor-specific USB 设备，不提供存储、输入、网络或任意内核读写能力。
- 正常和异常路径都必须执行 Detach、ROOT devnode 删除和本轮 Driver Store 暂存包清理。
- 若清理后仍存在目标 Instance、ROOT devnode 或控制接口，能力状态必须是 `CLEANUP_ERROR`，不能用本地通过覆盖清理失败。
