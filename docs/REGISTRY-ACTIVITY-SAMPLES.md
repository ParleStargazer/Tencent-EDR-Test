# Registry Activity 三项能力样本

## 1. 已实现范围

已实现三个 Windows 注册表活动能力包：

| 能力 ID | 中文 / English | Actor 行为 |
| --- | --- | --- |
| `win.registry.create` | 键/值创建 / Key/Value Creation | 创建本轮唯一键，并写入唯一 `REG_SZ` 值 |
| `win.registry.modify` | 键/值修改 / Key/Value Modification | 修改 Controller 预置值，前后内容不同 |
| `win.registry.delete` | 键/值删除 / Key/Value Deletion | 删除指定值，再删除空键 |

每个能力包包含独立命名的 Controller 和 Actor 两个 EXE。源码共享协议、行为和控制实现，构建时直接覆盖 `samples/win.registry.*` 旧能力包。

## 2. 安全边界与本地绝对基准

所有操作只允许当前用户配置单元：

```text
HKCU\Software\EdrTest\Runs\<nonce-tag>\<operation>
```

- 不访问 `HKLM`、服务、启动项或系统策略，无需管理员权限；
- 每轮键名由 nonce 派生，执行前拒绝复用已有测试键；
- Controller 只负责预置、独立复核和精确清理，Actor 执行真正被测行为；
- 清理函数再次验证路径前缀，只删除本轮精确子树；
- 值数据包含 nonce，但不包含凭据或隐私信息。

Controller 将以下高可用信息写入本轮 SQLite 和 `local-run.json`：

- Actor PID、EXE、命令行、开始/结束时间；
- 紧邻注册表 API 调用采集的 `occurred_at_utc`；
- Hive、完整键路径、值名、注册表视图；
- 操作前后的键/值存在状态、值类型、值数据及数据 SHA-256；
- Actor 原子协议 JSON、独立注册表复核和清理结果。

## 3. BASSLINE 设计

三份 BASSLINE 位于：

- `baselines/windows/registry_create.yaml`
- `baselines/windows/registry_modify.yaml`
- `baselines/windows/registry_delete.yaml`

本地条件是绝对前置基准。EDR 候选先按完整键路径、值名、Actor 程序、PID 和时间关联；单事件强关联时间默认不超过 15 ms。云端必需字段为 `registry.key`、`registry.value_name`、`process.pid`、`process.executable`，值数据和用户名为推荐项，避免产品默认隐藏敏感注册表数据时直接误判核心能力。

键/值删除的 Actor 会依次触发值删除和空键删除。当前能力结论只要求找到至少一条能以本地键路径、值名和 Actor 身份证明删除行为的 EDR 记录；候选窗口允许同一行为产生多条记录，并全部在 JSON 对照窗口展示。

## 4. 腾讯 EDR 映射依据

腾讯官方文档确认：

- 注册表内核事件表为 `RegEvents`，包括创建、设置、删除动作；
- 设置值的动作示例为 `Action.Name = RegSetValue`；
- 值数据示例字段为 `Child.RegValData`。

参考：[行为采集范围说明](https://cloud.tencent.com/document/product/1092/128451)、[威胁狩猎常用 SQL](https://cloud.tencent.com/document/product/1092/107833)。

因此 `mappings/tencent-edr-proc-events-v1.yaml` 增加了 `RegEvents` 的创建、修改、删除和候选发现路由。历史 `260808210300run` 全字段导出没有注册表事件，无法从本地参考文件确认键路径和值名字段。映射暂按优先级兼容 `Child.RegKeyPath`、`Child.RegPath`、`Child.RegistryPath`、`Child.RegObject` 以及相应值名别名；比较报告会保留实际命中的原始字段指针。拿到新一轮 `RegEvents` 导出后，应据此收敛并校准字段优先级，但不需要修改样本或 BASSLINE 本地规则。

## 5. 构建和验证

构建并覆盖三个本地能力包：

```powershell
pwsh -NoProfile -File scripts\Build-RegistryActivitySamples.ps1 -Configuration Release
```

平台启动脚本已自动执行该构建。三个能力使用标准用户权限，可直接在前端“注册表活动”大类中选择并运行。
