using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    private const string CancelledCleanup = "Sign-in was cancelled, but the incomplete account profile could not be removed: ";
    private const string SavedCleanup = "The account was saved, but the incomplete account profile could not be removed: ";
    private const string LegacyCleanup = " Removing the incomplete profile also failed: ";
    private const string SwiftCancellation = "The operation could not be completed. (Swift.CancellationError error 1.)";
    private const string UnknownError = "操作未完成，请重试。可查看技术详情以进一步排查。";

    // Only these complete internal bilingual messages may discard their other-language half.
    private static readonly string[] BilingualErrors = [
        "验证期间账号凭据发生变化，请重试。 / Credentials changed during validation.",
        "写入后账号身份不一致；取消启动。 / Account changed during handoff.",
        "检测到其他程序已切换账号，已停止自动恢复以保护当前登录。 / Another app changed the active account.",
        "此修复版支持 auth.json 文件登录。检测到其他凭据存储方式，已保留当前登录；请使用兼容该存储方式的切换器。 / Unsupported credential store.",
        "Codex 配置强制 API 登录，与 ChatGPT 账号不兼容；此次不切换。 / Incompatible login policy.",
        "Codex 配置限制了工作区；请先确认目标账号符合该配置。 / Workspace-restricted configuration.",
        "切换未完成。 / Switch failed.",
        "已恢复原登录并重新打开 Codex。 / Previous login restored.",
        "自动恢复未完成。请手动打开 Codex 并核对当前账号。 / Recovery needs manual attention.",
        "请等待当前操作完成。 / Another operation is running.",
        "请先完成或取消登录操作。 / Finish the current login first.",
        "请选择 1–100 个 JSON 文件。 / Select 1–100 JSON files.",
        "单个导入文件不能超过 16 MB。 / Import file too large.",
        "一次最多导入 500 个账号。 / At most 500 accounts per import.",
        "请先粘贴完整 JSON。 / Paste account JSON first.",
        "JSON 不能超过 16 MB。 / JSON too large.",
        "内容不是有效 JSON。请粘贴完整导出内容，不能使用打码预览。 / Invalid JSON export.",
        "JSON 中没有账号。 / No accounts found.",
        "账号的登录凭据已失效（401）。请重新授权，或导入有效的完整账号凭据。 / Login rejected; reauthorize this account.",
        "登录验证被拒绝（403）。请检查代理、网络或账号权限；此次不切换。 / Login validation forbidden.",
        "登录验证返回异常响应。 / Invalid validation response.",
        "服务返回了无法确认登录状态的响应；此次不切换。 / Unrecognized validation response.",
        "登录验证超时；保留当前账号，请检查代理或网络后重试。 / Login validation timed out.",
        "无法连接登录验证服务；保留当前账号，请检查代理或网络。 / Cannot reach login validation service.",
        "登录验证响应不是有效 JSON；请检查代理或网络。 / Invalid validation response.",
        "登录文件缺失或过大。请重新添加账号。 / Invalid login file.",
        "账号凭据格式不完整、被打码或身份不一致。请导出完整 JSON，或重新登录。 / Invalid credential metadata.",
        "JSON 中的账号必须是对象。 / Expected an account object."
    ];

    [return: NotNullIfNotNull(nameof(message))]
    public static string? Error(string? message, string? language)
    {
        if (message == null) return message;
        var chinese = IsChinese(language);
        if (IncompleteProfileError(message, chinese) is { } cleanup) return cleanup;
        if (message == SwiftCancellation) return chinese ? "已取消添加账号。" : "Account addition cancelled.";
        if (message.Contains('\n')) return string.Join("\n", message.Split('\n').Select(line => Error(line.TrimEnd('\r'), language)));
        if (BilingualErrors.Contains(message, StringComparer.Ordinal)) {
            var separator = message.IndexOf(" / ", StringComparison.Ordinal);
            return chinese ? message[..separator] : message[(separator + 3)..];
        }
        var validation = Regex.Match(message, @"^暂时无法验证登录（HTTP ([0-9]{3})）。请检查网络后重试；此次不切换。 / Login validation unavailable\.$", RegexOptions.CultureInvariant);
        if (validation.Success) return chinese ? $"暂时无法验证登录（HTTP {validation.Groups[1].Value}）。请检查网络后重试；此次不切换。" : $"Login validation is unavailable (HTTP {validation.Groups[1].Value}). Check your network and retry; the account was not switched.";
        if (!chinese) return message switch {
            QuotaReadException.Connection => "Cannot connect to the quota service. Check your network or VPN and retry.",
            QuotaReadException.ProxyConnection => "Cannot reach the quota service through the proxy. Check the proxy or try another node.",
            QuotaReadException.LocalProxyUnavailable => "Cannot connect to the local proxy. Make sure the proxy or VPN is running.",
            QuotaReadException.Timeout => "Quota refresh timed out. Check your network or VPN and retry.",
            QuotaReadException.ProxyTimeout => "The quota request through the proxy timed out. Check the proxy or try another node.",
            QuotaReadException.ProxyAuthentication => "Proxy authentication failed (407). Check the proxy username and password.",
            QuotaReadException.Unauthorized => "Sign-in credentials have expired (401). Sign in again or import valid credentials.",
            QuotaReadException.AuthorizationRevoked => "Codex sign-in authorization was revoked by the server and cannot be refreshed automatically. Sign in to the Codex account again.",
            QuotaReadException.Forbidden => "The quota request was denied (403). Check your network route or account permissions.",
            QuotaReadException.RateLimited => "Too many requests (429). Wait before refreshing again.",
            QuotaReadException.ServiceUnavailable => "The quota service is temporarily unavailable. Try again later.",
            QuotaReadException.InvalidResponse => "The quota service returned an invalid response. Check your network or proxy and retry.",
            QuotaReadException.MissingWindow => "The service has not returned a recognized quota window. Refresh again later.",
            QuotaReadException.RequestFailed => "The quota request failed. Try again later.",
            "读取额度失败，请检查网络后重试。" => "Quota refresh failed. Check your network and retry.",
            "该账号登录已失效，请先恢复登录后再切换。" or "该账号登录已失效，请先重新授权后再切换。" => "This account's sign-in has expired. Reauthorize the account before switching.",
            _ => Redact(message)
        };
        var storageError = PrivateStorageError(message);
        if (storageError != null) return storageError;
        var translated = message switch {
            "该账号登录已失效，请先恢复登录后再切换。" => "该账号登录已失效，请先重新授权后再切换。",
            "Codex did not return an account identity." => "无法读取 Codex 账号身份，请确认已完成登录，或重新导入有效的账号凭据。",
            "The account profile could not be found." => "找不到该账号的本地记录。",
            "No active account profile is configured." => "尚未设置当前账号。",
            "The active Codex auth.json file is missing." => "当前 Codex 登录凭据 auth.json 不存在，请先完成登录。",
            "Codex is signed in to a different account than Switcher expects. No saved credential was overwritten. Use Register Current Account to associate the current login before switching." => "Codex 当前登录与工具记录的账号不一致，已保存的凭据未被覆盖。请先在“添加账号”中选择“获取本地账号”，再切换账号。",
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
            "The Codex account process did not stop in time. Its files were left unchanged." => "Codex 账号进程未能及时退出，已保留其文件。请关闭相关进程后重试。",
            "The authorized account does not match the account being restored. Sign in with the selected account." => "授权账号与正在恢复的账号不一致，请使用所选账号重新登录。",
            "Account core is unavailable. Restart the app." => "账号服务暂不可用，请重新打开工具。",
            "The shared Swift runtime is missing. Build with scripts/package-windows.ps1." => "程序运行组件缺失，请重新安装完整安装包。",
            "Invalid runtime archive entry." => "程序运行组件包格式无效，请重新下载安装包。",
            "The runtime archive has no Swift host." => "程序运行组件不完整，请重新下载安装包。",
            "The refresh interval was not confirmed by the account service." => "账号服务尚未确认刷新间隔，设置未保存，请重试。",
            "Codex Desktop contains the switcher process. Close Desktop manually before switching; the account was not changed." => "本工具由 Codex 桌面进程启动，无法自动关闭该进程。账号未切换，请手动关闭 Codex 后重试。",
            "The sign-in page could not be opened." => "无法打开登录网页，请检查默认浏览器后重试。",
            "The operation could not be completed. You don’t have permission." or "The operation could not be completed. You don't have permission." => "无法访问或删除相关文件，请检查文件是否被占用及目录权限。",
            "Access is denied." => "权限不足，操作未完成。请检查当前用户对相关目录的访问和删除权限。",
            "The process cannot access the file because it is being used by another process." => "文件正被其他程序占用，请关闭占用程序后重试。",
            "The system cannot find the file specified." => "找不到所需文件，请检查文件位置或重新安装。",
            "The system cannot find the path specified." => "找不到所需目录，请检查目录位置。",
            "The operation has timed out." => "操作超时，请稍后重试。",
            _ => null
        };
        if (translated != null) return translated;
        var exitTimeout = Regex.Match(message, @"^Codex Desktop or its child processes did not exit within ([0-9]+(?:\.[0-9]+)?) seconds\. Close them manually before switching; the account was not changed\.$", RegexOptions.CultureInvariant);
        if (exitTimeout.Success) return $"Codex 桌面版或子进程未能在 {exitTimeout.Groups[1].Value} 秒内退出，账号未切换。请手动关闭后重试。";
        var serviceCode = Regex.Match(message, @"^Codex app-server error (-?[0-9]+): (.*)$", RegexOptions.CultureInvariant);
        if (serviceCode.Success) return $"Codex 服务错误（{serviceCode.Groups[1].Value}）：" + Error(serviceCode.Groups[2].Value, language);
        // The primary message stays localized; raw diagnostics are available separately.
        foreach (var prefix in ErrorPrefixes) {
            if (message.StartsWith(prefix.English, StringComparison.Ordinal))
                return prefix.Chinese + Error(message[prefix.English.Length..], language);
        }
        if (message.Length == 0) return message;
        var prose = message.Replace("Codex Desktop", "Codex", StringComparison.Ordinal).Replace("Star Codex Switcher", "Switcher", StringComparison.Ordinal);
        return message.Any(character => character is >= '\u3400' and <= '\u9fff') && !Regex.IsMatch(prose, @"[A-Za-z]{3,}\s+[A-Za-z]{3,}") ? Redact(message) : UnknownError;
    }

    private static string? IncompleteProfileError(string message, bool chinese)
    {
        string? summary = null;
        string? detail = null;
        if (message.StartsWith(CancelledCleanup, StringComparison.Ordinal)) {
            summary = chinese ? "已取消添加账号，但未能清理临时账号目录。" : "Account addition was cancelled, but the temporary account folder could not be removed.";
            detail = message[CancelledCleanup.Length..];
        } else if (message.StartsWith(SavedCleanup, StringComparison.Ordinal)) {
            summary = chinese ? "账号已保存，但未能清理临时账号目录。" : "The account was saved, but the temporary account folder could not be removed.";
            detail = message[SavedCleanup.Length..];
        } else {
            var marker = message.IndexOf(LegacyCleanup, StringComparison.Ordinal);
            if (marker > 0) {
                var original = message[..marker]; detail = message[(marker + LegacyCleanup.Length)..];
                summary = original == SwiftCancellation
                    ? (chinese ? "已取消添加账号，但未能清理临时账号目录。" : "Account addition was cancelled, but the temporary account folder could not be removed.")
                    : Error(original, chinese ? "simplifiedChinese" : "english") + (chinese ? "\n未能清理未完成授权的临时账号目录。" : "\nThe incomplete account folder could not be removed.");
            }
        }
        if (summary == null || string.IsNullOrWhiteSpace(detail)) return null;
        return summary + "\n" + Error(detail, chinese ? "simplifiedChinese" : "english");
    }

    public static string? TechnicalDetails(string? message, string? language) => string.IsNullOrEmpty(message) ? null
        : (IsChinese(language) ? "技术详情（敏感字段已隐藏）：\n" : "Technical details (sensitive fields hidden):\n") + Redact(message);

    public static string Redact(string message)
    {
        var result = message.Length > 65536 ? message[..65536] : message;
        result = Regex.Replace(result, @"(?i)\b(Bearer|Basic)\s+[^\s,;]+", "$1 [redacted]");
        result = Regex.Replace(result, "(?i)([\"']?(?:access_token|refresh_token|id_token|api[_-]?key|authorization|password|client_secret|session_token|code|state)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;&}\\]]+)", "$1[redacted]");
        result = Regex.Replace(result, @"(?i)(https?://)[^/\s@]+@", "$1[redacted]@");
        result = Regex.Replace(result, @"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", "[redacted]");
        result = Regex.Replace(result, @"\b(?:sk-|rt-)[A-Za-z0-9._-]+|[A-Za-z0-9_-]{40,}", "[redacted]");
        return result.Length <= 4096 ? result : result[..4096] + "\n[truncated]";
    }

    public static string? SaveDiagnostic(string message)
    {
        try {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Star Codex Switcher", "Diagnostics");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "last-error.txt");
            File.WriteAllText(path, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + "\n" + Redact(message));
            return path;
        } catch { return null; }
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
