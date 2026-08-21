# 虚拟磁盘挂载能力样本

## 1. 范围与结论

能力 ID 为 `win.device.virtual_disk.mount`，同轮固定执行两个独立子测试：

| 方法 ID | 行为入口 | 直接本地证据 |
|---|---|---|
| `VDISK_POWERSHELL` | Windows PowerShell 5.1 `Mount-DiskImage` | `powershell.exe` PID/命令行、唯一 VHD 路径、物理磁盘路径、双端附加与卸载复核 |
| `VDISK_NATIVE_API` | `OpenVirtualDisk` + `AttachVirtualDisk` | Actor PID/命令行、唯一 VHD 路径、物理磁盘路径、双端附加与卸载复核 |

腾讯 EDR 当前字段基准与既有 run 中没有虚拟磁盘专属表、动作或对象字段。因此本实现追求两种方法的本地绝对基准完整通过，不要求当前产品离线比较通过；普通 PowerShell 脚本、进程创建、VHD 文件写入或其他磁盘侧面事件不能使本能力通过。

## 2. 安全模型

该能力标记为 `L2` 且要求管理员权限。Controller 为每个方法在其独立工作目录创建一个 16 MiB 动态 VHD，拒绝覆盖已有文件。镜像不创建分区、不初始化、不格式化、不分配盘符；两种方法都以只读和无盘符方式附加。

正常和错误路径共用精确清理：先停止本轮 Actor，再只对清单计划出的完整 VHD 路径执行对应卸载（PowerShell 方法使用 `Dismount-DiskImage`，原生方法使用 `DetachVirtualDisk`），确认物理路径消失后删除该镜像。路径必须位于本轮 `work` 目录，清理不使用通配符，也不枚举或修改系统已有磁盘。

## 3. 本地绝对基准

Controller 先创建并哈希 VHD，确认其“存在但未附加”，再启动 Actor：

1. Actor 执行指定挂载方法，并通过 `GetVirtualDiskPhysicalPath` 得到 `\\.\PhysicalDriveN`；PowerShell 子进程在调用 cmdlet 的前一语句输出 UTC 毫秒标记，避免把模块启动耗时计入 15 ms 强关联时间。
2. Actor 写入 ready 协议后保持附加状态。
3. Controller 重新 `OpenVirtualDisk`，通过自己的句柄取得同一物理路径；只有一致才放行。
4. Actor 卸载并确认物理路径消失，退出并写结果协议。
5. Controller 再次独立确认未附加，核对镜像 SHA-256 未变化。
6. Controller 写入两个独立的 `device/virtual_disk_mount` 事件、事实和清理结果。

方法分别使用 `InstanceIndex=0/1`、`Sequence=1/2` 和不同的工作目录、协议文件、VHD 文件，避免多子测试共享进程角色序号或文件句柄。

## 4. BASSLINE 与映射

`baselines/windows/device_virtual_disk_mount.yaml` 使用 `method_selection: best`，保留两个方法的独立本地要求和 EDR 要求。强关联时间为 15 ms，强锚点为镜像路径、物理设备路径和镜像 SHA-256，发起进程为中等锚点。

`mappings/generic-virtual-disk-activity-v1.yaml` 仅供比较器自测。腾讯规划路由只接受未来可能出现的 `DeviceEvents`、`VirtualDiskEvents` 或 `VDiskEvents` 中的 `VirtualDiskMount`/`VDiskMount`，映射到 canonical `device.*`。它不使用当前已有的通用进程、脚本或文件事件兜底，因此没有专属日志时结论应保持未匹配。

## 5. 构建与验证

```powershell
pwsh -NoProfile -File scripts/Build-VirtualDiskActivitySamples.ps1
```

管理员 PowerShell 中执行完整验证：

```powershell
pwsh -NoProfile -File scripts/Test-VirtualDiskActivitySamples.ps1
```

非管理员环境会完成编译和能力包生成，然后跳过真实挂载。完整验证覆盖两种本地行为、双端物理路径复核、清理结果、腾讯规划映射可通过，以及普通 `ScriptScan`/`FileWriteClose` 不能使能力通过。

## 6. API 依据

- [Mount-DiskImage](https://learn.microsoft.com/powershell/module/storage/mount-diskimage)
- [CreateVirtualDisk](https://learn.microsoft.com/windows/win32/api/virtdisk/nf-virtdisk-createvirtualdisk)
- [OpenVirtualDisk](https://learn.microsoft.com/windows/win32/api/virtdisk/nf-virtdisk-openvirtualdisk)
- [AttachVirtualDisk](https://learn.microsoft.com/windows/win32/api/virtdisk/nf-virtdisk-attachvirtualdisk)
- [GetVirtualDiskPhysicalPath](https://learn.microsoft.com/windows/win32/api/virtdisk/nf-virtdisk-getvirtualdiskphysicalpath)
- [DetachVirtualDisk](https://learn.microsoft.com/windows/win32/api/virtdisk/nf-virtdisk-detachvirtualdisk)
