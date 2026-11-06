# Windows 图标资源

本项目使用用户在最后一组候选中选定的第 3 款图标：深蓝圆角方块、白色双向 S 形箭头、中央蓝色星标。该资产由图像生成工具创建。

## 原始资源

- 仓库文件：[assets/star-codex-switcher-logo.png](../assets/star-codex-switcher-logo.png)。
- 会话生成资源组：`01a1a0bc-e0b8-72f3-8b87-c82442e13dbf`。
- 选定资源：`exec-e03b3919-f5d7-447c-b42e-b06880c6de3c.png`。
- 图像：1254 × 1254，RGBA，外部背景和四角具有真实透明通道。
- SHA-256：`D27D1CF2CEA1BD51E5C891C7D18532DE3BE68EA3C5A6C5E697D8875D8C171230`。

仓库 PNG 是选定输出的原样副本。转换脚本只做缩放和 ICO 编码，不重新绘制图形、不通过阈值删除背景、不替换原始透明通道。后续更换图标时，应保留新的来源记录并更新校验测试。

## Windows 打包

安装 Python 与 Pillow 后，在仓库根目录运行：

```powershell
python scripts/generate-windows-icons.py
```

脚本生成 `windows/CodexAccountSwitcher/app.ico`、`tray-light.ico` 和 `tray-dark.ico`，每个文件包含 16、20、24、32、40、48、64、128、256 像素九个尺寸。小尺寸采用 Windows BMP 图标帧，256 像素采用 PNG 图标帧。

浅色和深色任务栏都使用同款全彩图标，保留深蓝底和白色标记以帮助辨认。两份托盘资源同源是有意设计，不表示缺少深色适配。启用“在托盘显示百分比”时，仍显示现有的动态额度数字。

WPF 应用的可执行文件与窗口使用 `app.ico`，嵌入的 `logo.png` 使用原始 PNG。NSIS 的 `MUI_ICON`、`MUI_UNICON`、`Icon` 和 `UninstallIcon` 指向同一 `app.ico`，安装及卸载入口保持一致。

## 验证

`windows/CodexAccountSwitcher.UiChecks/BrandingChecks.cs` 验证嵌入 PNG 的 SHA-256、九种尺寸、WPF 解码（包括 256 像素）、Windows 小图标选择、透明四角与缩小后的可见度；不要求彩色图标满足旧单色资源的二值像素规则。

已查看实际 16、20、24、32、48、64 像素在浅色和深色背景上的效果。检查用对照图位于忽略的构建目录 `windows/artifacts/branding-qa/selected-icon-sizes.png`。图形缩小后仍可辨认，四角无不透明外底；放大的像素预览仅用于检查，不作为发布资源。
