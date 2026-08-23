# 驱动测试证书

这里只允许保存公开证书 .cer。私钥由 New-DriverTestCertificate.ps1 创建在当前用户证书存储区，禁止导出或提交 PFX/PVK。平台启动时仅在公开证书与签名包元数据指纹一致且用户明确确认后，才将证书导入 LocalMachine\Root 和 LocalMachine\TrustedPublisher；平台不会自动开启 testsigning。
