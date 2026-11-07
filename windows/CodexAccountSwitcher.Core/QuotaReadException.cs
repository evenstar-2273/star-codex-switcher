using System.Net;
using System.Net.Sockets;

namespace CodexAccountSwitcher.Core;

// Only fixed, user-safe descriptions reach account snapshots. Never forward
// transport exception strings, response bodies, or proxy credentials to the UI.
public sealed class QuotaReadException(string message) : IOException(message)
{
    public const string Connection = "无法连接额度服务，请检查网络或 VPN 后重试。";
    public const string ProxyConnection = "通过代理连接额度服务失败，请检查代理或更换节点后重试。";
    public const string LocalProxyUnavailable = "无法连接本地代理，请确认代理或 VPN 正在运行。";
    public const string Timeout = "额度刷新超时，请检查网络或 VPN 后重试。";
    public const string ProxyTimeout = "通过代理连接额度服务超时，请检查代理或更换节点后重试。";
    public const string ProxyAuthentication = "代理认证失败（407），请检查代理用户名或密码。";
    public const string Unauthorized = "登录凭据已失效（401），请重新授权或导入有效凭据。";
    public const string AuthorizationRevoked = "Codex 登录授权已被服务端撤销，无法自动刷新。请重新登录 Codex 账号。";
    public static bool IsAuthenticationFailure(string? message) => message is Unauthorized or AuthorizationRevoked;
    public const string Forbidden = "额度请求被拒绝（403），请检查网络出口或账号权限。";
    public const string RateLimited = "请求过于频繁（429），请稍后刷新。";
    public const string ServiceUnavailable = "额度服务暂时不可用，请稍后重试。";
    public const string InvalidResponse = "额度服务返回异常响应，请检查网络或代理后重试。";
    public const string MissingWindow = "服务暂未返回可识别的额度窗口，请稍后刷新。";
    public const string RequestFailed = "额度请求失败，请稍后重试。";

    public static QuotaReadException FromStatus(HttpStatusCode status) => new(status switch {
        HttpStatusCode.Unauthorized => Unauthorized,
        HttpStatusCode.Forbidden => Forbidden,
        HttpStatusCode.ProxyAuthenticationRequired => ProxyAuthentication,
        HttpStatusCode.TooManyRequests => RateLimited,
        _ when (int)status >= 500 => ServiceUnavailable,
        _ => RequestFailed
    });

    public static QuotaReadException FromTransport(HttpRequestException error, Uri? proxy)
    {
        if (error.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) return new(ProxyAuthentication);
        // A local listener being refused is evidence of an unavailable local
        // proxy only when this request actually selected that proxy endpoint.
        for (Exception? cause = error; cause != null; cause = cause.InnerException) {
            if (cause is TimeoutException) return new(proxy != null ? ProxyTimeout : Timeout);
            if (proxy?.IsLoopback == true && cause is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return new(LocalProxyUnavailable);
        }
        return new(proxy != null ? ProxyConnection : Connection);
    }
}
