# 哈希算法三项能力样本

## 1. 实现范围

本能力包实现 MD5 哈希（MD5）、SHA 哈希（SHA）和导入表哈希（IMPHASH）。三项都采用 Controller + Actor 编排：Actor 负责创建本轮唯一受测文件并计算摘要，Controller 重新读取文件、复算摘要、记录 SQLite 本地事实并精确清理。

| 能力 | 受测文件 | 本地绝对基准 | EDR 核心字段 |
| --- | --- | --- | --- |
| MD5 | 合法 `.json`，默认 8192 字节 | 完整文件 MD5，32 位小写十六进制 | `Child.FileMd5` → `file.hash.md5` |
| SHA | 合法 `.json`，默认 8192 字节 | 同时保存 SHA-1、SHA-256、SHA-512；以 SHA-256 形成能力结论 | `Child.FileSha` + `Child.FileShaType=3` → `file.hash.sha256` |
| IMPHASH | 真实 `.exe` PE，是 Actor 可执行文件的字节级副本 | PE 标头、导入项数量、导入序列和 32 位 IMPHASH；源/目标 SHA-256 必须一致 | 假定 `Child.FileImpHash` 等字段 → `file.hash.imphash` |

MD5、SHA 不使用改扩展名的二进制伪装文件；IMPHASH 也不使用 JSON 或手工伪造摘要。三项都由 `CreateNew` 创建此前不存在的唯一文件，因此可以复用文件创建事件的路径、进程和时间关联标准。

## 2. 本地事实与 SQLite 对接

每项能力产生一条 `hash/<operation>` 本地事件、一个 Actor 程序实例、一个原子写入的行为协议制品和一条精确清理记录。通用事实键为：

- `hash.operation_succeeded`、`hash.occurred_at_utc`；
- `hash.actor_pid`、`hash.actor_executable`、`hash.actor_command_line`；
- `hash.extension`、`hash.path`、`hash.file_size_bytes`；
- `hash.algorithm`、`hash.digest`；
- `hash.md5`、`hash.sha1`、`hash.sha256`、`hash.sha512`、`hash.imphash`；
- `hash.is_portable_executable`、`hash.import_count`、`hash.source_pe_path`、`hash.source_pe_sha256`、`hash.source_matches_target`。

不适用于当前文件类型的摘要事实允许为 `null`，但对应能力的核心摘要必须存在。Runner 将事实写入每轮独立 SQLite，导出工具按既有 `local-run.json` 契约输出；本次没有引入专用数据库表或更改数据库版本。

Controller 在成功采证后只删除 `work/<case-run-id>/` 下的本轮精确文件。导出的事实、事件和行为协议制品仍保留，可用于前端“已完成能力队列”和离线比较。

## 3. BASELINE 与候选关联

三份 BASELINE 位于：

- `baselines/windows/hash_md5.yaml`
- `baselines/windows/hash_sha.yaml`
- `baselines/windows/hash_imphash.yaml`

本地运行结果是绝对基准。EDR 候选按以下证据关联和排序：

1. 文件绝对路径一致；
2. Actor 可执行文件一致；
3. Actor PID 一致；
4. EDR 事件相对本地 `hash.occurred_at_utc` 的时间差，15 ms 内作为强证据。

候选事件必须是 `file/create`。文件路径、PID、Actor 路径、文件大小和目标摘要均为必需断言；`file.operation_name=create_new` 是推荐断言。SHA 额外推荐 `file.hash.sha_type=3`，IMPHASH 额外推荐文件格式包含 `PE`。即使某个 EDR 字段缺失，比较器仍保留时间和关联字段相近的候选 JSON 块，并明确标记未满足的摘要断言。

前端为三项能力预置 `Action.Name=FileWriteClose`，该规则只筛选 EDR 候选，不影响本地事实。`Child.FileCreateOpName` 的用户自定义筛选仍只面向五项 File Manipulation 能力，哈希能力不会被加入该范围。

## 4. 腾讯字段校准说明

`reference/260808210300run` 的全字段导出确认：

- `Child.FileMd5` 大量存在，可直接验证 MD5；
- `Child.FileSha` 为 64 位十六进制值；同类记录的 `Child.FileShaType` 示例为 `3`，当前按 SHA-256 解释；
- 当前导出没有 IMPHASH 字段。

因此腾讯映射增加 `Child.FileSha`/`Child.FileSha256`、`Child.FileShaType`/`Child.FileSha256Type` 兼容别名。IMPHASH 按能力假设预留 `Child.FileImpHash`、`Child.FileImphash`、`Child.ImpHash` 三个别名；它仍是必需字段，不会因产品当前缺失而自动降级为通过。获得真实导出后，应校准实际字段名和算法约定。

IMPHASH 实现按 PE 导入描述符顺序，将小写 DLL 名去除 `.dll/.sys/.ocx` 后与小写导入函数名组合，再对逗号连接串计算 MD5；序号导入保留为 `ord<序号>`。受测 Actor PE 具有真实导入表，Controller 还会验证源 EXE 与目标 EXE 的 SHA-256 完全一致，避免对任意内容计算伪 IMPHASH。

## 5. 前后端与构建入口

前端能力目录将三项标记为 `Controller · Actor`。一键启动脚本会调用哈希能力包构建脚本并覆盖 `samples/win.hash.*` 旧包；MD5、SHA 和 IMPHASH 默认 Action.Name 都是 `FileWriteClose`。规范化事件 Schema 已支持 `file.hash.sha512`、`file.hash.imphash` 和 `file.hash.sha_type`，JSON 对照窗可据原始字段指针高亮摘要匹配。

```powershell
pwsh -NoProfile -File scripts/Build-HashAlgorithmsSamples.ps1
pwsh -NoProfile -File scripts/Test-HashAlgorithmsSamples.ps1
```

端到端脚本会实际运行三个能力，验证 `.json/.json/.exe` 后缀、本地事实、PE 导入表、精确清理，并分别用通用映射和腾讯字段映射生成三项通过的合成离线比较结果。

## 6. 安全边界

- 三项均为 `L0`，无需管理员权限和网络。
- 只在本轮工作目录创建新文件，不打开或修改用户已有文件。
- IMPHASH 只复制本能力包 Actor 自身，不执行复制后的 EXE。
- 清理按解析后的工作目录边界和精确路径执行，不使用通配符。
