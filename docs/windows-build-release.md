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

当前版本为 `1.0.15`，后续从 `1.0.16` 递增。内部 `FileVersion` 使用四段形式（如 `1.0.15.0`），文件名和 Release tag 使用三段形式（如 `v1.0.15`）。可以通过 `-ReleaseTag` 显式指定；必须与实际 GitHub Release tag 完全一致。

1.0.15 的交付名称如下；仓库地址中的 `star-codex-switcher` 保持不变：

| 用途 | 名称 |
| --- | --- |
| 显示标题、快捷方式、安装显示名 | `Star Codex Switcher` |
| 安装包 | `Star-Codex-Switcher-1.0.15-Setup-x64.exe` |
| 固定最新版安装包 | `Star-Codex-Switcher-Setup-x64.exe` |
| 安装后的主程序 | `StarCodexSwitcher.exe` |
| 开发/打包中间应用 | `Star-Codex-Switcher-windows-x64.exe` |
| 源码下载 | GitHub 自动提供的 `Source code (zip)` / `Source code (tar.gz)` |
| 安装包校验文件 | `Star-Codex-Switcher-1.0.15-Setup-x64.exe.sha256` |
| 更新清单 | `update-installed.json` |

安装脚本生成版本安装包、校验文件与更新清单。1.0.4–1.0.6 发布这三项资产；从 1.0.7 起按固定最新版直链要求，发布准备阶段从已验证版本包生成同内容的 `Star-Codex-Switcher-Setup-x64.exe`，共四项 Release 资产。这项新增要求替代原三项规则，不改变产品编译、打包脚本或 CI。固定名副本必须与版本包大小和 SHA-256 一致；不另传源码包，源码使用 GitHub 根据 tag 自动提供的 Source code (zip/tar.gz)。新用户默认安装路径为 `%LOCALAPPDATA%\Programs\Star Codex Switcher`；旧用户升级沿用现有安装位置。更名时保留既有账号数据目录、登录记录和设置存储。

安装包清单含安装包哈希与内置应用哈希；重新构建安装包后必须重新生成清单，不得只更换 EXE。

1.0.7 发布前使用隔离的真实 Windows 目录联接验证：新用户空存储无错误、根及祖先目录链接可解析、导入后重启可读取原账号、Codex 登录根链接兼容，且账号库内部链接仍被拒绝。需检查链接变更后运行中客户端继续使用已固定目录、无效链接不会新建空账号库，以及目录别名的单实例与旧版安装退出兼容。中文界面和导入弹窗应显示中文存储错误并保留编号；测试只使用合成凭据。

确认重置按钮还需验证：点击后立即显示旋转加载和“重置中…”；请求及回读完成前保持忙碌，重复点击不产生第二次请求；成功完成或失败均停止动画，失败保留明确提示。此检查使用合成请求，不消耗真实重置卡。

1.0.6 的更新提示说明重置卡到期倒计时，以及自定义额度刷新间隔的保存反馈和下拉样式。账号列表与重置确认页在最早到期时间后显示括号剩余时间，并随时间更新；自定义间隔新增明确的“保存”按钮及未保存、成功、失败反馈，保留回车与失焦保存，并整合圆角输入框、分钟单位、下拉图标和深浅主题菜单。

发布前需验证两个到期入口的文本一致、倒计时持续更新、窄窗口可读，并回归原有三级颜色、未知时间及已到期提示。刷新设置需验证保存按钮、回车和失焦三种入口、失败后重试、非法值不保存、重启后读取已保存值，以及下拉菜单在深浅主题和窄窗口中的显示。以上均为待执行的发布检查，文档更新本身不表示验证通过。

继续回归 1.0.5 引入的额度自动刷新设置：默认 5 分钟，禁用、2、5、10、15 分钟及自定义；自定义整数范围 1–1440 分钟，回车或失焦保存，非法输入不保存。验证即时生效、重启保留、托盘驻留有效、禁用只停止定时刷新，以及首次加载、手动刷新和自动重置继续可用；还需检查修改间隔不产生重复计时器或重叠请求。

继续回归卡片和重置确认的边界：超过 3 天为绿、超过 24 小时且不超过 3 天为橙、24 小时内为红；时间和图标同步着色，3 天内加粗，未知为灰，已到期提示待刷新。手动全量刷新及打开窗口实际发起的全量刷新应使顶部和所有卡片图标旋转；单卡刷新仅影响对应图标。等待实际任务完成或失败后停止，期间阻止重复请求并保持滚动位置。普通后台读取不应被描述为每次都有旋转动效。

更新弹窗的“跳过版本”只跳过该版本，下一个新版仍提醒；“暂不更新”在本次运行中不重复提示；“更新并重启”保留账号。手动检查仍可查看并安装已跳过的版本，后台或忙碌时延后弹窗。发布前需回归这些行为及原有账号、设置保留，不能仅以文档已更新作为验证完成的依据。

`subscriptions` 使用合成登录信息与 HTTP 响应检查到期前续费查询、`accounts_check` → 必要时目标工作区 `subscriptions` 的只读查询流程，包含明确时区、30 分钟缓存、手动刷新、跨工作区响应隔离、网络失败或空响应保留上次有效日期。界面应如实区分登录信息来源、“核验中”“待核验”与在线核验日期；不能用固定加减时差或一个月修正账期。测试不会访问真实账号或改变登录。

应用、安装与卸载程序、托盘统一使用 S 形双箭头/星标资源。修改资源时按 [图标资源说明](branding.md) 重新生成 ICO，并运行界面检查，确认所有入口图标一致。

## 手动 Actions 构建

打开仓库的 **Actions → Windows installer → Run workflow**。它从源码构建 Host，运行 Windows 检查，生成安装包并上传构建产物。它不会自动发布 Release，也不使用 SSH 密码。

本工作流的远端执行结果以 Actions 实际记录为准。

## 发布步骤

1. 先在本地完成代码和文档修改，更新版本号、`CHANGELOG.md`、软件内新版本提示与 Release 说明。提交标题和正文统一使用中文，说明具体变化及验证结果。
2. 在本地编译、打包并运行相应检查；界面变化还需查看实际截图。验证失败先修复，不先提交一个尚未验证的发布版本。
3. 在隔离 Windows 用户或测试机验证首次安装、旧安装位置升级、账号与设置保留、更新完成后启动、卸载，以及无 SDK 环境运行。使用合成或测试账号，不在发布资产中放入个人账号。
4. 本地检查通过后正常提交源码并运行 CI，保留 1.0.0 及后续 Git 提交和 tag 历史，不重建分支历史。CI 通过后，创建与已验证构建一致的 tag（本次为 `v1.0.15`）；GitHub 会自动提供该 tag 的 Source code (zip/tar.gz)，无需另外制作源码包。
5. 在 [GitHub Releases](https://github.com/evenstar-2273/star-codex-switcher/releases) 创建该 tag 的草稿 Release，上传版本安装包、对应 SHA-256 文件、`update-installed.json` 和固定名 `Star-Codex-Switcher-Setup-x64.exe` 四项资产。固定名 EXE 从同一已验证版本包复制，必须确认两个 EXE 的大小及 SHA-256 相同；不要先更新清单或发布 Latest，再补尚不可下载的固定名包。
6. 四项草稿资产齐全、版本与摘要核对通过后，正式发布为 Latest，保留已有历史版本及其下载资产。
7. 从公网重新下载 GitHub 版本包、固定最新版直链、校验文件和清单，确认两种 EXE 地址返回相同内容；核对 `download_url`、版本、包哈希与内置应用哈希，确认软件能检测到新版、提示文案一致。不能用草稿上传成功代替公开下载核验。
8. GitHub 四项资产、固定最新版直链及自动更新入口均核验成功后，记录验证结果，再清理本地交付及测试打包目录中的旧版本安装包及对应 `.sha256`，只保留最新版安装包、校验文件、固定名副本及有效更新清单。后续发布不再上传、同步、验证或清理服务器镜像，服务器状态不作为本地清理前提。GitHub 历史 Release、下载资产和 tag 不删除；若发布或 GitHub 核验失败，不清理本地旧包，先修复或停止分发新版。

GitHub 保留历史安装包、Release、资产、提交和 tag，Latest 与软件更新清单指向最新验证通过的版本。本地交付及测试打包目录只保留最新版安装包、对应校验文件、固定名副本及有效更新清单。清理前列出明确的本地旧包路径，排除最新版文件和固定名入口；仅处理这些旧包及对应 `.sha256`，不得递归删除目录。源码、源码归档、`.git`、账号数据和其他文件保留，GitHub 自带的 Source code (zip/tar.gz) 入口也保留。清理与发布只有实际完成后才记录为已完成；本说明不表示旧文件已经清理。

客户端固定读取：

`https://github.com/evenstar-2273/star-codex-switcher/releases/latest/download/update-installed.json`

清单的安装包 URL 应固定到具体 tag，不要指向可变的 `latest` EXE。不要把低版本设置为 Latest 来代替回滚方案；遇到问题时先停止分发，修复后发布更高版本。

供用户保存和分享的固定最新版直链为 [GitHub 最新安装包](https://github.com/evenstar-2273/star-codex-switcher/releases/latest/download/Star-Codex-Switcher-Setup-x64.exe)。该地址用于手动下载最新版本；自动更新清单仍绑定具体 tag 的版本文件，以便版本、摘要和回滚记录保持对应。

## 历史服务器镜像

服务器已上传的 1.0.8 安装包可作为历史镜像保留。后续版本仅发布到 GitHub，不再上传、同步或验证服务器安装包，也不在后续发布流程中清理该镜像。原服务器固定入口不再维护，不能作为始终下载最新版的地址。

软件自动更新一直读取 GitHub 的 `update-installed.json`，停止维护服务器镜像无需改动程序更新源。后续以 GitHub 固定最新版直链和历史 Release 为下载入口。

## 公开仓库检查

只提交源码、文档、许可证及必要的合成测试资源。不提交：

- `auth.json`、真实账号导出、访问/刷新令牌、SSH 密码、GitHub token；
- 本地诊断日志、真实账号截图、运行时缓存；
- `bin/`、`obj/`、`windows/artifacts/` 内的构建产物。

检查待提交文件列表与 diff；不要仅依赖 `.gitignore`。已提交过的秘密需要从历史移除并轮换，删除工作区文件本身不够。发布资产使用 GitHub Releases，不把安装包放入 Git 源码历史。
