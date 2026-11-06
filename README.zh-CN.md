# Star Codex Switcher

本分支的中文说明统一维护在 [README.md](README.md)。

本项目提供 Windows 版 Codex 账号管理、切换和安装更新。

订阅有效期通过官方 `accounts_check` 与必要时的 `subscriptions` 查询核验；旧 JWT 无法确认当前到期日时显示“待核验”。结果缓存 30 分钟，手动刷新可立即重新核验。完整使用说明见 [订阅有效期](README.md#订阅有效期)，合成测试命令见 [构建与发布](docs/windows-build-release.md)。

Windows 应用、安装程序与托盘采用本分支选定的 S 形双箭头与星标图标，详见 [图标资源说明](docs/branding.md)。
