using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CodexAccountSwitcher;

public static class WindowsLocalization
{
    public static bool IsChinese(string? language) => language switch {
        "english" => false,
        "simplifiedChinese" => true,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
    };

    private static readonly (string English, string Chinese)[] ErrorPrefixes = [
        ("Codex app-server failed to start: ", "Codex 服务启动失败："),
        ("Codex app-server closed the connection: ", "Codex 服务已断开连接："),
        ("Codex app-server error: ", "Codex 服务错误："),
        ("Codex app-server error ", "Codex 服务错误 "),
        ("Codex login failed: ", "Codex 登录失败：")
    ];

    [return: NotNullIfNotNull(nameof(message))]
    public static string? Error(string? message, string? language)
    {
        if (message == null || !IsChinese(language)) return message;
        var translated = message switch {
            "Codex did not return an account identity." => "无法读取 Codex 账号身份，请确认已完成登录，或重新导入有效的账号凭据。",
            "The account profile could not be found." => "找不到该账号的本地记录。",
            "No active account profile is configured." => "尚未设置当前账号。",
            "The active Codex auth.json file is missing." => "当前 Codex 登录凭据 auth.json 不存在，请先完成登录。",
            "Codex is signed in to a different account than Switcher expects. No saved credential was overwritten. Use Register Current Account to associate the current login before switching." => "Codex 当前登录与工具记录的账号不一致，已保存的凭据未被覆盖。请先点击“保存当前登录”，再切换账号。",
            "The selected account has no saved auth.json file." => "所选账号没有保存的 auth.json 凭据，请重新导入或登录。",
            "This account is already saved. Select the existing account instead." => "该账号已保存，请选择已有账号。",
            "The active account cannot be removed." => "不能删除当前正在使用的账号。",
            "The Codex executable could not be found." => "找不到 Codex 命令行程序，请确认已安装 Codex。",
            "Codex app-server returned malformed JSON." => "Codex 服务返回的数据格式无效，请重试。",
            "Codex app-server closed the connection." => "Codex 服务已断开连接，请重试。",
            "Codex app-server did not respond before the timeout." => "等待 Codex 服务响应超时，请检查服务状态后重试。",
            "No weekly Codex Usage window is available." => "暂时无法获取 Codex 的 7 天额度。",
            "The shared account core could not start." => "账号服务无法启动，请重新打开应用。",
            "The shared account core stopped." => "账号服务已停止，请重新打开应用。",
            "The shared core protocol failed." => "账号服务通信失败，请重新打开应用。",
            "Invalid account state." => "账号服务返回的状态无效，请重新打开应用。",
            "Invalid sign-in address." => "登录地址无效，请重新发起登录。",
            "Unknown platform operation." => "收到无法识别的系统操作。",
            "The configured Codex Desktop executable was not found." => "找不到配置的 Codex 桌面程序，请检查安装路径。",
            "Could not inspect the Codex Desktop installation." => "无法读取 Codex 桌面版的安装信息。",
            "Codex Desktop was not found. Install Codex Desktop before switching." => "找不到 Codex 桌面版，请先安装后再切换账号。",
            "Could not inspect a matching Desktop process. Close Codex Desktop manually before switching." => "无法检查 Codex 桌面进程，请手动退出 Codex 后再切换。",
            "Could not terminate Codex Desktop and its child processes. Close them manually before switching; the account was not changed." => "无法结束 Codex 桌面版及其子进程，账号尚未切换。请手动退出后重试。",
            "Codex Desktop could not be opened; the selected account remains active." => "无法打开 Codex 桌面版；所选账号已生效，请手动打开 Codex。",
            "Codex Desktop did not start within 15 seconds. Open it manually; the selected account remains active." => "Codex 桌面版未能在 15 秒内启动；所选账号已生效，请手动打开 Codex。",
            "Codex Desktop is running without a closable window. Quit it manually before switching." => "Codex 桌面版仍在后台运行，请手动退出后再切换账号。",
            "Switch was not validated." => "账号切换尚未通过验证。",
            _ => null
        };
        if (translated != null) return translated;
        // Translate only known wrappers; retain service details and unknown errors verbatim.
        foreach (var prefix in ErrorPrefixes) {
            if (message.StartsWith(prefix.English, StringComparison.Ordinal))
                return prefix.Chinese + message[prefix.English.Length..];
        }
        return message;
    }
}
