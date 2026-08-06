# EDR 能力验证控制台

这是 EDR 能力离线验证平台的中文项目入口。当前版本提供：

- Windows 基础遥测能力选择与风险提示；
- 通过本机 API 启动/取消真实 Runner 轮次；
- 一轮一个 SQLite 数据库的状态、历史与本地 JSON 下载；
- Controller / Actor / Target 程序构成展示；
- 本地运行 JSON、EDR 云端 JSON 和导出清单导入；
- 后端统一执行映射、BASELINE 关联和验证；
- 验证结果 JSON 下载。

页面不会连接腾讯 EDR。导入文件只发送到同一台机器的 `127.0.0.1:4317`，保存在仓库的 Git 忽略目录中。

## 本地开发

```powershell
pwsh -NoProfile -File ..\scripts\Start-EdrTest.ps1
```

默认访问：<http://127.0.0.1:3000/>

`http://127.0.0.1:3000/` 与 `http://localhost:3000/` 均受支持。仓库通过 pnpm 版本化补丁修复 Vinext 0.0.50 在 Windows 生产服务器中无法解析 `/assets/` 路径的问题；升级 Vinext 时需重新确认该补丁是否仍有必要。

仅开发前端时可运行 `pnpm dev`，但轮次和比较功能仍要求本地 API 已启动。

## 验证

```powershell
pnpm test
pnpm lint
```
