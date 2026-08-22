# EDR 能力验证控制台

这是 EDR 能力离线验证平台的中文项目入口。当前版本提供：

- Windows 基础遥测能力选择与风险提示；
- 通过本机 API 启动/取消真实 Runner 轮次；
- 一轮一个 SQLite 数据库的状态、历史与本地 JSON 下载；
- Controller / Actor / Target 程序构成展示；
- 本地运行 JSON、EDR 云端 JSON 和导出清单导入；
- 可选使用本机 Edge 在测试结束后自动下载、校验并绑定腾讯 EDR 云端日志；
- 后端统一执行映射、BASELINE 关联和验证；
- 每项能力可打开本地运行 JSON 与 EDR 导出 JSON 对照窗，在多个候选块之间切换并高亮 BASELINE 一致字段；
- 验证结果 JSON 下载。

页面不调用腾讯 EDR API。手动导入文件只发送到同一台机器的 `127.0.0.1:4317`；可选自动化使用本机 Edge 模拟控制台筛选与下载，并把文件绑定到当前轮次。账号和密码只用于当前后台任务，不写入命令行、环境变量、日志、数据库或运行制品；所有导入文件均保存在 Git 忽略目录中。

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
