using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CodexAccountSwitcher.Core;

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
        if (message == null) return message;
        if (!IsChinese(language)) return message switch {
            QuotaReadException.Connection => "Cannot connect to the quota service. Check your network or VPN and retry.",
            QuotaReadException.ProxyConnection => "Cannot reach the quota service through the proxy. Check the proxy or try another node.",
            QuotaReadException.LocalProxyUnavailable => "Cannot connect to the local proxy. Make sure the proxy or VPN is running.",
            QuotaReadException.Timeout => "Quota refresh timed out. Check your network or VPN and retry.",
            QuotaReadException.ProxyTimeout => "The quota request through the proxy timed out. Check the proxy or try another node.",
            QuotaReadException.ProxyAuthentication => "Proxy authentication failed (407). Check the proxy username and password.",
            QuotaReadException.Unauthorized => "Sign-in credentials have expired (401). Sign in again or import valid credentials.",
            QuotaReadException.Forbidden => "The quota request was denied (403). Check your network route or account permissions.",
            QuotaReadException.RateLimited => "Too many requests (429). Wait before refreshing again.",
            QuotaReadException.ServiceUnavailable => "The quota service is temporarily unavailable. Try again later.",
            QuotaReadException.InvalidResponse => "The quota service returned an invalid response. Check your network or proxy and retry.",
            QuotaReadException.MissingWindow => "The service has not returned a recognized quota window. Refresh again later.",
            QuotaReadException.RequestFailed => "The quota request failed. Try again later.",
            "读取额度失败，请检查网络后重试。" => "Quota refresh failed. Check your network and retry.",
            _ => message
        };
        var storageError = PrivateStorageError(message);
        if (storageError != null) return storageError;
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
            "Windows user identity is unavailable." => "无法确认当前 Windows 用户身份，暂时无法访问账号存储。",
            "Account storage cannot use symbolic links or junctions." => "账号存储路径包含目录链接或联接，暂时无法访问。请检查账号数据目录的链接或重定向设置。",
            "Cleanup path is outside account storage." => "清理目标超出账号数据目录，已停止清理。",
            "The account storage directory is empty." => "账号数据目录不能为空。",
            "The account storage root must be a directory." => "账号数据存储位置必须是文件夹，请检查数据目录路径。",
            "Choose a dedicated account storage folder, not a drive or Windows user folder." => "请使用独立的账号数据文件夹，不能直接使用系统、用户或磁盘根目录。",
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

    private static string? PrivateStorageError(string message)
    {
        const string accessPrefix = "Windows could not access private account storage (error ";
        const string resolvePrefix = "The account storage directory could not be resolved (Windows error ";
        const string suffix = ").";
        var resolving = message.StartsWith(resolvePrefix, StringComparison.Ordinal);
        var prefix = resolving ? resolvePrefix : accessPrefix;
        if (!message.StartsWith(prefix, StringComparison.Ordinal) || !message.EndsWith(suffix, StringComparison.Ordinal)
            || message.Length <= prefix.Length + suffix.Length
            || !uint.TryParse(message.AsSpan(prefix.Length, message.Length - prefix.Length - suffix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out var code)) return null;
        var reason = code switch {
            4393 => "请检查账号数据目录的链接或重定向设置。",
            2 or 3 => "找不到账号数据目录或其链接目标，请检查目录位置。",
            5 => "请检查当前 Windows 用户对账号数据目录的访问权限。",
            32 or 33 => "账号数据文件正被其他程序占用，请关闭占用程序后重试。",
            1921 => "系统无法访问账号数据目录的链接目标，请检查是否存在无效或循环链接。",
            _ => "请检查账号数据目录是否可访问后重试。"
        };
        return (resolving ? "Windows 无法确定账号存储目录的实际位置（错误 " : "Windows 无法访问账号存储（错误 ")
            + code.ToString(CultureInfo.InvariantCulture) + "）。" + reason;
    }
}
