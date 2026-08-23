# 项目代理执行约束

- Windows 环境执行命令前，显式通过 pwsh 使用 PowerShell 7。
- Python 由 Conda 管理；项目需要 Python 时创建独立环境，并优先使用 python -m pip install，不使用 conda install。
- 禁止使用 Base64、EncodedCommand 或其他编码载荷写入项目文件。文件修改必须保持命令和内容可直接阅读；优先使用补丁工具，补丁工具不可用时使用可读的 PowerShell/.NET 直接文件编辑。
- 直接在当前分支提交，不为常规实现创建额外分支。
- 驱动开发工具链默认位于 F:\EWDK；相关脚本必须支持参数覆盖并在使用前验证路径和工具版本。
