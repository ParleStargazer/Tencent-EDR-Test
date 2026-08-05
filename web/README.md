# EDR 能力验证控制台

这是 EDR 能力离线验证平台的前端控制面。当前版本提供：

- Windows 基础遥测能力选择与风险提示；
- 一轮一个 SQLite 数据库的运行计划展示；
- Controller / Actor / Target 程序构成展示；
- 本地运行 JSON、EDR 云端 JSON 和导出清单的本地导入；
- 腾讯 EDR `ProcessCreate` 事件的浏览器端离线比较；
- 验证结果 JSON 和执行计划 JSON 下载。

当前页面不会连接腾讯 EDR，也不会把导入日志发送到服务器。真实 EXE 调度和 SQLite 读写接口将在后续 Runner 实现中接入。

## 本地开发

```powershell
pnpm install
pnpm dev
```

默认访问：<http://localhost:3000/>

## 验证

```powershell
pnpm test
pnpm lint
```
