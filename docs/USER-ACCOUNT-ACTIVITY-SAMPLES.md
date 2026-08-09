# User Account Activity 五项能力样本

## 1. 范围

当前版本实现以下五项 Windows 用户账号活动：

| 能力 ID | 中文 / English | 目标 Windows 行为 | 主要安全事件 |
| --- | --- | --- | --- |
| `win.account.local.create` | 本地账号创建 / Local Account Creation | `NetUserAdd` | 4720 |
| `win.account.local.modify` | 本地账号修改 / Local Account Modification | `NetUserSetInfo` level 1007 修改 comment | 4738 |
| `win.account.local.delete` | 本地账号删除 / Local Account Deletion | `NetUserDel` | 4726 |
| `win.account.login` | 账号登录 / Account Login | `LogonUserW`，网络登录类型 | 4624 |
| `win.account.logoff` | 账号注销 / Account Logoff | 关闭 `LogonUserW` 返回的登录令牌 | 4634 或 4647 |

五项能力均为 `L1`，清单声明 `required_privilege: administrator`。非管理员运行时，Runner 在启动 Controller 前完成权限预检并将能力标记为 `SKIPPED`，不会创建账号。

## 2. 程序编排

每个能力包包含两个角色：

- `*.Controller.exe`：生成唯一账号与短期密码、启动 Actor、独立读取本机账号状态、写入 SQLite、保存无密码证据并执行清理。
- `*.Actor.exe`：只读取本轮请求，调用 Windows 原生 API 产生目标行为并返回结构化结果。

临时账号名固定满足 `edrt` 前缀、ASCII 字母数字和 20 字符上限，后 12 位由本轮 nonce 派生。Controller 在执行前确认该账号不存在；清理时也只允许删除满足该规则且与本轮精确名称一致的账号。

密码使用密码学随机数生成，只写入能力工作目录中的短生命周期请求文件。密码不会出现在 Actor 命令行、SQLite、本地导出、行为结果或证据制品中。请求文件在每项能力的清理阶段删除。

## 3. 本地绝对基准

所有能力至少保存以下高置信事实：

- 目标账号名、SID、计算机域和账号类型；
- Actor PID、绝对可执行路径和命令行；
- 原生 API 调用前后的账号存在状态；
- API 调用开始与返回之间的时间中点，作为 `account.occurred_at_utc`；
- Win32 结果、证据文件引用和清理前后状态。

修改能力额外保存变更字段及 comment 前后值。登录与注销额外保存从 `TOKEN_STATISTICS.AuthenticationId` 取得的 Logon ID、登录类型 3、认证包和令牌验证结果。注销能力先建立并短暂保持登录令牌，再以关闭令牌调用的时间中点作为本地基准。

## 4. EDR 关联规则

五份 BASELINE 位于 `baselines/windows/account_*.yaml`。候选事件优先按本地账号名、SID、登录 AuthenticationId、Actor PID 与精确时间进行关联，时间强证据阈值为 15 ms。Windows 安全事件 ID 是云端断言，不参与本地行为是否成功的判断。

腾讯映射使用 `Action.EventLogId` 识别 4720、4738、4726、4624、4634/4647，不依赖 `Action.Name`。目标账号字段来自 `Child.TargetUserName`、`Child.TargetSid` 或 `Child.TargetUserSid`；登录会话字段来自 `Child.TargetLogonId`、`Child.LogonType` 和 `Child.AuthenticationPackageName`。

## 5. 构建与验收

仅构建能力包：

```powershell
pwsh -NoProfile -File scripts/Build-UserAccountActivitySamples.ps1
```

在隔离测试机的管理员 PowerShell 中执行完整本地行为、SQLite/JSON 检查以及通用/腾讯合成日志比较：

```powershell
pwsh -NoProfile -File scripts/Test-UserAccountActivitySamples.ps1
```

验收脚本会确认五项能力全部 `LOCAL_PASS`、五项清理全部成功、没有受控临时账号残留、清单期望事实齐全，并分别使用通用映射和腾讯映射得到五项 `PASS`。
