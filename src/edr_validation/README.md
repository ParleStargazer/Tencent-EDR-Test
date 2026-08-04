# 框架源码占位

实现阶段将在此目录按领域边界建设：

- `cli`：命令入口；
- `domain`：Run、CaseRun、Baseline、状态机；
- `orchestration`：计划、调度、恢复；
- `executors`：Windows 执行协议；
- `collectors`：Mock、文件和腾讯 EDR 适配器；
- `normalization`：规范化事件映射；
- `matching`：候选召回与断言引擎；
- `persistence`：运行存储；
- `reporting`：JSON、HTML、JUnit 与能力矩阵。

本文件只用于保留设计阶段的仓库骨架，不代表框架已经实现。
