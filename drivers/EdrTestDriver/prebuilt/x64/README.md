# 仓库预构建驱动包

本目录版本化保存已签名的 `EdrTestDriver.sys`、`EdrTestDriver.cat`、INF 和元数据，普通测试机直接使用，不需要安装 EWDK。对应的仅含公钥证书位于 `drivers\cert\EdrTestDriverTest.cer`。

维护者可用 `script\driver\Build-DriverPackage.ps1 -UpdatePrebuilt` 更新本包。不得提交 PFX、PVK、私钥或证书密码；私钥只允许作为不可导出密钥保存在维护者的 CurrentUser 证书存储区。
