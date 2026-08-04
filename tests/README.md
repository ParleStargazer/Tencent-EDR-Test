# 测试目录

实现阶段按以下层次组织：

- `unit`：领域模型、断言、评分、脱敏；
- `contract`：Baseline Schema、Collector 和 Normalizer 契约；
- `integration`：Mock/File Collector 端到端；
- `e2e`：隔离 Windows VM 与腾讯 EDR 实测。
