# File Manipulation 五项能力样本

## 1. 实现范围

已实现文件创建、打开、删除、修改和重命名五个 Windows L0 能力包。每个能力在同一轮依次执行 TXT 与有效 JSON 两个子项，用于观察 EDR 对不同文件类型的采集敏感度。每个包包含独立命名的 Controller 和 Actor 两个 EXE；五个包共享协议、行为和控制源码，构建时直接覆盖 `samples/win.file.*` 的旧能力包。

| 能力 | Actor 行为（TXT 与 JSON 各一次） | Controller 独立检验 |
| --- | --- | --- |
| 文件创建 | `CreateNew` 写入 8 KiB nonce 载荷并落盘 | 前不存在、后存在，大小及 MD5/SHA-256 |
| 文件打开 | `Open + ReadWrite`，完整读取并原样回写 | 前后 MD5/SHA-256 相同，读写字节数一致 |
| 文件删除 | 删除预置 nonce 文件 | 删除前大小/哈希，删除后不存在 |
| 文件修改 | `Create` 截断并覆盖写新载荷 | 前后哈希不同，后大小与写入字节数一致 |
| 文件重命名 | `File.Move` 到唯一目标名 | 源消失、目标出现、内容哈希不变 |

“文件打开”不是纯只读测试。260808 导出中的“打开文件”属于 `FileWriteClose`，因此样本使用读写打开和原样回写，既形成典型可导出行为，又由本地前后哈希证明内容语义未改变。

JSON 子项不是只修改扩展名：载荷本身是可解析的 JSON 对象，并包含版本、nonce、操作和定长 payload。Controller 会独立解析操作后仍存在的 JSON 文件；删除子项则在 Actor 启动前校验预置 JSON，因此无效 JSON 不会被记为本地通过。

## 2. 数据库和本地导出

Controller 复用通用 SQLite Schema，无需新增专用表：

- `program_instance`：每项记录 Controller，以及 TXT/JSON 两个 Actor 的 PID、父 PID、绝对路径、命令行、开始/结束时间、退出码及三种哈希；
- `local_event`：每项写两条 `file/<operation>` 事件，并用 `data.subtest` 和 `data.file_extension` 区分 TXT/JSON；
- `local_fact`：保存 BASELINE 直接引用的路径、Actor PID、发生时间、大小、哈希、读写字节数及状态断言；
- `artifact`：保存 Actor 的 `behavior-result.json` 哈希和相对路径；
- `cleanup_result`：记录精确临时文件路径的清理前后状态。

每个路径都位于 Runner 分配的轮次工作目录。清理函数再次校验目标没有越出该目录，拒绝删除任意外部文件。

## 3. 云端映射和 BASELINE

生产映射 `mappings/tencent-edr-proc-events-v1.yaml` 已增加五个 FileEvents 路由，并保留原 profile ID 兼容已有前端选择。Canonical `file` 对象补充旧路径、三类时间、操作名、内容/格式/驱动/加密信息及读写字节数。

五份版本完全匹配的 BASELINE 位于：

- `baselines/windows/file_create.yaml`
- `baselines/windows/file_open.yaml`
- `baselines/windows/file_delete.yaml`
- `baselines/windows/file_modify.yaml`
- `baselines/windows/file_rename.yaml`

强关联锚点为 JSON 文件路径和对应 Actor 可执行路径；PID 为中等锚点；事件时间使用 `facts.file.json.occurred_at_utc`，五项 BASELINE 均要求 EDR 时间差不超过 10 ms。10 ms 内的时间差计为强证据，但必须与至少一个文件、程序或 PID 锚点共同出现。TXT 与 JSON 两项本地行为都必须成功，但云端只把 JSON 子项设为必检，避免已知不敏感的 TXT 事件拉低产品能力结论。路径、Actor 身份、操作枚举和文件大小是 required；腾讯导出支持 MD5 但少量记录为空，因此 MD5 为 recommended。本地仍保存 TXT/JSON 两套 SHA-1/SHA-256 供其他产品映射复用。

腾讯映射保留已知的 `File/FileRename` 精确语义，同时增加不依赖 `Action.Name` 的候选发现路由。因此 `InjectHook/MoveFileExW` 或后续未知名称的记录也会先进入时间窗，再依据本地源/目标路径、Actor 路径/PID和时间距离评分；不包含固定 PID、固定目录或固定 nonce，避免针对单次日志过拟合。

如果同一 JSON 文件行为关联出多条强候选，可在离线比较页为对应能力填写一个或多个原始 `Action.Name`，五项文件能力还可填写一个或多个原始 `Child.FileCreateOpName`。单字段多值为“任选其一”；两个字段均填写时，候选必须同时满足。默认规则为：创建 `FileWriteClose + 新建文件`、修改 `FileWriteClose + 覆盖写文件`、打开 `FileWriteClose + 打开文件`、重命名 `FileRename`，删除留空。该设置只筛选 EDR 候选，不会改变本地运行规则、`LOCAL_PASS`，也不会代替本地路径、Actor、PID 和时间条件；清空任一字段后不会对该字段进行筛选，未选中的强候选仍会显示完整 JSON。

## 4. 构建和验收

```powershell
pwsh -NoProfile -File scripts/Build-FileManipulationSamples.ps1
pwsh -NoProfile -File scripts/Test-FileManipulationSamples.ps1
```

端到端脚本会在同一轮串行执行五项能力，检查 SQLite 导出的 5 项能力、15 个程序实例、10 条事件、10 项清理和清单声明事实，再从每项 JSON 子测试生成厂商无关与腾讯格式云端夹具，通过五份 BASELINE 做离线比较。重命名腾讯夹具使用 `InjectHook/MoveFileExW`，防止该真实事件类型再次被路由遗漏。夹具只验证框架闭环，不代表真实 EDR 检出。

前端和 API 无需为能力写死新接口：构建后的包由 `/api/capabilities` 自动发现，BASELINE 与映射也按目录自动发现；测试页和离线比较页会复用现有 SQLite 证据、完整候选 JSON 和字段高亮组件。
