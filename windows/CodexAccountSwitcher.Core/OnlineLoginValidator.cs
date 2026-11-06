using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

public interface ILoginValidator
{
    Task ValidateAsync(LoginCredential credential, CancellationToken cancellationToken);
}

public sealed class OnlineLoginValidator : ILoginValidator, IDisposable
{
    private readonly HttpClient http;
    public OnlineLoginValidator(HttpMessageHandler? handler = null)
    {
        // Never forward the account bearer to a redirect destination.
        http = new HttpClient(handler ?? WindowsHttp.Handler()) { Timeout = TimeSpan.FromSeconds(25) };
    }
    public async Task ValidateAsync(LoginCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", credential.AccountID);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodexAccountSwitcher/0.1.27-guarded.2");
        try {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new IOException("账号的登录凭据已失效（401）。请重新授权，或导入有效的完整账号凭据。 / Login rejected; reauthorize this account.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new IOException("登录验证被拒绝（403）。请检查代理、网络或账号权限；此次不切换。 / Login validation forbidden.");
            if (!response.IsSuccessStatusCode)
                throw new IOException($"暂时无法验证登录（HTTP {(int)response.StatusCode}）。请检查网络后重试；此次不切换。 / Login validation unavailable.");
            // A gateway HTML page with HTTP 200 is not evidence of an authenticated account.
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new IOException("登录验证返回异常响应。 / Invalid validation response.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var body = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (body.RootElement.ValueKind != JsonValueKind.Object ||
                (!body.RootElement.TryGetProperty("rate_limit", out _) && !body.RootElement.TryGetProperty("plan_type", out _)))
                throw new IOException("服务返回了无法确认登录状态的响应；此次不切换。 / Unrecognized validation response.");
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            throw new IOException("登录验证超时；保留当前账号，请检查代理或网络后重试。 / Login validation timed out.");
        } catch (HttpRequestException) {
            throw new IOException("无法连接登录验证服务；保留当前账号，请检查代理或网络。 / Cannot reach login validation service.");
        } catch (JsonException) {
            throw new IOException("登录验证响应不是有效 JSON；请检查代理或网络。 / Invalid validation response.");
        }
    }
    public void Dispose() => http.Dispose();
}
