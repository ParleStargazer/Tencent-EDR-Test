# UsbUdeTest

`UsbUdeTest` 是 USB 挂载/卸载能力的最小 KMDF UDE 测试驱动。驱动作为根枚举的虚拟 USB Host Controller 保持加载，只接受管理员或 SYSTEM 发出的三个受限请求：

- Attach：创建固定 `VID_ED1D&PID_0001`、带本轮 `EDR_USB_<nonce>` 序列号的虚拟 USB 设备并调用 `UdecxUsbDevicePlugIn`；
- Detach：仅对自身创建的设备调用 `UdecxUsbDevicePlugOutAndDelete`；
- Query：返回是否已附加及当前序列号。

驱动不实现存储、文件系统、HID 输入、任意内存/进程/文件访问，也不能操作物理 USB 设备。用户态测试通过 SetupAPI 独立确认对应 PnP Instance ID 的出现或消失。

仓库交付 `prebuilt/x64` 下的 Release x64 测试签名包。公开证书位于 `drivers/cert/EdrTestDriverTest.cer`，只含公钥；签名私钥不得进入仓库。目标机器需要管理员权限、当前启动项开启 `testsigning`，并信任该公开证书。
