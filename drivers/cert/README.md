# 驱动测试证书

这里只允许保存公开证书 .cer。私钥由 New-DriverTestCertificate.ps1 创建在当前用户证书存储区，禁止导出或提交 PFX/PVK。将公开证书导入 LocalMachine 信任区属于管理员操作，只能通过初始化脚本的 -Apply 显式执行。
