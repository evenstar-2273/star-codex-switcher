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

面向用户的版本从 `1.0.0` 开始；本次更名版本为 `1.0.1`，后续按 `1.0.2`、`1.0.3` 递增。内部 `FileVersion` 使用四段形式（如 `1.0.1.0`），文件名和 Release tag 使用三段形式（如 `v1.0.1`）。可以通过 `-ReleaseTag` 显式指定；必须与实际 GitHub Release tag 完全一致。

1.0.1 的交付名称如下；仓库地址中的 `star-codex-switcher` 保持不变：

| 用途 | 名称 |
| --- | --- |
| 显示标题、快捷方式、安装显示名 | `Star Codex Switcher` |
| 安装包 | `Star-Codex-Switcher-1.0.1-Setup-x64.exe` |
| 安装后的主程序 | `StarCodexSwitcher.exe` |
| 开发/打包中间应用 | `Star-Codex-Switcher-windows-x64.exe` |
| 源码包 | `Star-Codex-Switcher-1.0.1-source.zip` |
| 安装包校验文件 | `Star-Codex-Switcher-1.0.1-Setup-x64.exe.sha256` |
| 更新清单 | `update-installed.json` |

安装脚本生成安装包、校验文件与更新清单。源码包从本次验证通过并提交的同一源码生成，不混入构建目录或账号数据。新用户默认安装路径为 `%LOCALAPPDATA%\Programs\Star Codex Switcher`；旧用户升级沿用现有安装位置。更名时保留既有账号数据目录、登录记录和设置存储。

安装包清单含安装包哈希与内置应用哈希；重新构建安装包后必须重新生成清单，不得只更换 EXE。

`subscriptions` 使用合成 HTTP 响应检查官方 `accounts_check` → 必要时 `subscriptions` 的只读核验流程，包含旧 JWT 日期不能确认为当前过期日、续费后的实际日期、30 分钟缓存和手动刷新跳过缓存。界面应区分“核验中”“待核验”与已核验日期；不能用固定加一个月的方式修正账期。测试不会访问真实账号或改变登录。

应用、安装与卸载程序、托盘统一使用 S 形双箭头/星标资源。修改资源时按 [图标资源说明](branding.md) 重新生成 ICO，并运行界面检查，确认所有入口图标一致。

## 手动 Actions 构建

打开仓库的 **Actions → Windows installer → Run workflow**。它从源码构建 Host，运行 Windows 检查，生成安装包并上传构建产物。它不会自动发布 Release，也不使用 SSH 密码。

本工作流的远端执行结果以 Actions 实际记录为准。

## 发布步骤

1. 先在本地完成代码和文档修改，更新版本号、`CHANGELOG.md`、软件内新版本提示与 Release 说明。提交信息说明具体变化及验证结果。
2. 在本地编译、打包并运行相应检查；界面变化还需查看实际截图。验证失败先修复，不先提交一个尚未验证的发布版本。
3. 在隔离 Windows 用户或测试机验证首次安装、旧安装位置升级、账号与设置保留、更新完成后启动、卸载，以及无 SDK 环境运行。使用合成或测试账号，不在发布资产中放入个人账号。
4. 本地检查通过后正常提交源码并运行 CI，保留 1.0.0 及后续 Git 提交和 tag 历史，不重建分支历史。CI 通过后，创建与已验证构建一致的 tag（本次为 `v1.0.1`），从该提交生成 `Star-Codex-Switcher-1.0.1-source.zip`。
5. 在 [GitHub Releases](https://github.com/evenstar-2273/star-codex-switcher/releases) 创建该 tag 的草稿 Release，上传同一版本的安装包、源码包、SHA-256 文件和 `update-installed.json`。确认资产齐全、版本和哈希匹配，再准备公开；不要先更新清单，再补尚不可下载的安装包。
6. 将资产齐全的草稿正式发布为 Latest，保留旧版下载直至下一步核验通过。
7. 从公网重新下载新版安装包和清单，核对 `download_url`、版本、包哈希与内置应用哈希；确认软件能检测到新版，提示文案与本次改动一致。该步骤验证公开可下载性，不能用草稿上传成功代替。
8. 新版公开下载及更新入口验证可用后，再移除旧 Release 的下载入口和资产；保留对应 Git 提交、tag 与更新记录。若新版核验失败，先修复或恢复可用入口，不先删除线上可用版本。

本地打包交付目录与公开下载最终只保留最后一次验证通过的一组文件（安装包、源码包、校验文件和更新清单）。清理与发布属于实际操作，只有完成后才记录为已完成；本说明不表示旧文件已被清理。

客户端固定读取：

`https://github.com/evenstar-2273/star-codex-switcher/releases/latest/download/update-installed.json`

清单的安装包 URL 应固定到具体 tag，不要指向可变的 `latest` EXE。不要把低版本设置为 Latest 来代替回滚方案；遇到问题时先停止分发，修复后发布更高版本。

## 公开仓库检查

只提交源码、文档、许可证及必要的合成测试资源。不提交：

- `auth.json`、真实账号导出、访问/刷新令牌、SSH 密码、GitHub token；
- 本地诊断日志、真实账号截图、运行时缓存；
- `bin/`、`obj/`、`windows/artifacts/` 内的构建产物。

检查待提交文件列表与 diff；不要仅依赖 `.gitignore`。已提交过的秘密需要从历史移除并轮换，删除工作区文件本身不够。发布资产使用 GitHub Releases，不把安装包放入 Git 源码历史。
