# EdrTestDriver

用于驱动活动三项能力验证的最小 x64 WDM 驱动。驱动仅实现 DriverEntry 和 DriverUnload，不创建设备、不暴露 IOCTL、不注册回调，也不访问用户数据。

- 开发构建默认使用 F:\EWDK，可通过脚本参数覆盖。
- 私钥与 PFX 不进入仓库；测试证书私钥只保留在证书存储区。
- prebuilt\x64 只接受由项目构建脚本生成并附带 driver-package.json 的受控包。
- 实际加载/卸载只允许在隔离测试机或快照虚拟机中执行。
