# Star Codex Switcher · Windows

本目录包含 Windows 10/11 x64 原生 WPF 应用、核心服务封装、安装程序和合成检查。

- [安装与使用](../README.md)
- [切换、导入、订阅与更新实现](../GUARDED-README.md)
- [构建、测试与发布安装包](../docs/windows-build-release.md)
- [Windows 结构](../docs/shared-core.md)
- [验证范围](../docs/windows-validation.md)
- [界面验证](../design-qa.md)
- [运行时许可](licenses/README.md)

当前安装包为 `Star-Codex-Switcher-1.0.7-Setup-x64.exe`，安装后的主程序为 `StarCodexSwitcher.exe`。源码使用对应 GitHub Release 自带的 **Source code (zip)** 或 **Source code (tar.gz)** 下载，不再单独上传源码包。窗口标题和快捷方式统一为 `Star Codex Switcher`。

新用户默认安装到 `%LOCALAPPDATA%\Programs\Star Codex Switcher`，旧用户升级沿用原安装位置，并保留账号数据和设置。运行时随包附带，用户不需要安装 .NET SDK 或 Swift 工具链。源码构建与发布步骤统一维护在构建文档中。
