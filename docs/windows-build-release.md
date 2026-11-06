# Windows 构建与 GitHub 发布

仓库：<https://github.com/evenstar-2273/star-codex-switcher>

本项目发布 Windows x64 安装包。运行用户不需要 .NET/Swift 开发环境；下列依赖用于构建。

## 构建依赖

- Windows x64 与 PowerShell。
- .NET 10 SDK。
- Swift 6.3.3 Windows 工具链（从源码构建 Host 时需要）。
- NSIS 3.11。
- 能访问 NuGet、Swift 依赖及 GitHub 的网络。

先确认 `dotnet --version`、`swift --version` 和 `makensis /VERSION`。本仓库手动 Actions 工作流可作为环境配置参考；第一次远端运行前，不应称其已经验证成功。

## 从源码生成运行时

在仓库根目录执行。下列路径都是仓库内示例路径，不依赖开发者用户名：

```powershell
./scripts/package-windows.ps1 -OutputDirectory ./windows/artifacts/source-build
Expand-Archive -LiteralPath ./windows/artifacts/source-build/SwiftRuntime.zip -DestinationPath ./windows/artifacts/installer-runtime -Force
```

打包脚本会编译 `SwitcherHost.exe` 并收集 Swift 运行时与许可证。这里生成的便携 EXE 是构建中间产物，正式发布安装版使用下面的命令。

`SwiftRuntimeDirectory` 使用上述脚本生成的运行时目录，不得使用账号数据目录。运行时目录至少需要 `SwitcherHost.exe`、`swiftCore.dll`、`Foundation.dll`、`vcruntime140.dll` 及其依赖和许可证。构建记录应包含工具链版本及产物 SHA-256。

## 测试与生成安装包

```powershell
dotnet build windows/CodexAccountSwitcher -c Release --nologo
dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- guarded ./windows/artifacts/installer-runtime/SwitcherHost.exe
dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- plans
dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- reset-credits
dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- subscriptions
dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release

$dotnetPath = (Get-Command dotnet).Source
$nsisPath = (Get-Command makensis).Source
./scripts/package-installed.ps1 -SwiftRuntimeDirectory ./windows/artifacts/installer-runtime -Dotnet $dotnetPath -Makensis $nsisPath -OutputDirectory ./windows/artifacts/release
```

面向用户的版本从 `1.0.0` 开始，后续按 `1.0.1`、`1.0.2` 递增。内部 `FileVersion` 使用四段形式（如 `1.0.0.0`），文件名和 Release tag 使用三段形式（如 `v1.0.0`）。可以通过 `-ReleaseTag` 显式指定；必须与实际 GitHub Release tag 完全一致。构建脚本同时生成：

- `Codex-Account-Switcher-<版本>-Setup-x64.exe`
- 对应 `.exe.sha256`
- `update-installed.json`

安装包清单含安装包哈希与内置应用哈希；重新构建安装包后必须重新生成清单，不得只更换 EXE。

`subscriptions` 使用合成 HTTP 响应检查官方 `accounts_check` → 必要时 `subscriptions` 的只读核验流程，包含旧 JWT 日期不能确认为当前过期日、续费后的实际日期、30 分钟缓存和手动刷新跳过缓存。界面应区分“核验中”“待核验”与已核验日期；不能用固定加一个月的方式修正账期。测试不会访问真实账号或改变登录。

应用、安装与卸载程序、托盘统一使用 S 形双箭头/星标资源。修改资源时按 [图标资源说明](branding.md) 重新生成 ICO，并运行界面检查，确认所有入口图标一致。

## 手动 Actions 构建

打开仓库的 **Actions → Windows installer → Run workflow**。它从源码构建 Host，运行 Windows 检查，生成安装包并上传构建产物。它不会自动发布 Release，也不使用 SSH 密码。

本工作流的远端执行结果以 Actions 实际记录为准。

## 发布步骤

1. 更新版本号、`CHANGELOG.md` 与本次提交信息。确认源码检查、界面截图与安装包检查通过。
2. 在隔离 Windows 用户或测试机验证首次安装、保留账号数据的升级、更新完成后启动、卸载，以及无 SDK 环境运行。使用合成或测试账号，不在发布资产中放入个人账号。
3. 将通过检查的源码提交并创建与构建一致的 tag。
4. 在 [GitHub Releases](https://github.com/evenstar-2273/star-codex-switcher/releases) 创建该 tag 的 Release，上传安装包、SHA-256 文件和 `update-installed.json`。
5. 检查清单中的 `download_url` 能下载到同一 Release 的安装包，下载后重新校验 SHA-256。
6. 完成检查后将稳定版发布为 Latest。只有正式发布且可公开下载的 Release 才能供现有客户端检查到。

客户端固定读取：

`https://github.com/evenstar-2273/star-codex-switcher/releases/latest/download/update-installed.json`

清单的安装包 URL 应固定到具体 tag，不要指向可变的 `latest` EXE。不要把低版本设置为 Latest 来代替回滚方案；遇到问题时先停止分发，修复后发布更高版本。

## 公开仓库检查

只提交源码、文档、许可证及必要的合成测试资源。不提交：

- `auth.json`、真实账号导出、访问/刷新令牌、SSH 密码、GitHub token；
- 本地诊断日志、真实账号截图、运行时缓存；
- `bin/`、`obj/`、`windows/artifacts/` 内的构建产物。

检查待提交文件列表与 diff；不要仅依赖 `.gitignore`。已提交过的秘密需要从历史移除并轮换，删除工作区文件本身不够。发布资产使用 GitHub Releases，不把安装包放入 Git 源码历史。
