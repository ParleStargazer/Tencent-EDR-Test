# 预构建 USB UDE 驱动包

此目录交付 Release x64 的 `UsbUdeTest.sys`、INF、测试签名 CAT 和元数据。平台优先直接使用这里的包；仅当包缺失或校验失败时，才尝试使用可配置的 EWDK 环境重新构建。

签名对应的公开证书是 `drivers/cert/EdrTestDriverTest.cer`。仓库不包含私钥。目标机器必须由管理员将该证书导入 `LocalMachine\Root` 与 `LocalMachine\TrustedPublisher`，并在当前启动项开启 `testsigning`。
