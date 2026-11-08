using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexAccountSwitcher.Core;

public static class WindowsHttp
{
    public static HttpClientHandler Handler()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        // Desktop apps follow the user's Windows proxy, rather than inherited shell proxy variables.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key?.GetValue("ProxyEnable") is int enabled && enabled != 0 && key.GetValue("ProxyServer") is string server) {
            var address = server.Split(';').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("https=", StringComparison.OrdinalIgnoreCase))?[6..]
                ?? server.Split(';').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("http=", StringComparison.OrdinalIgnoreCase))?[5..]
                ?? (server.Contains('=') ? null : server);
            if (address != null && Uri.TryCreate(address.Contains("://") ? address : "http://" + address, UriKind.Absolute, out var proxy))
                handler.Proxy = new WebProxy(proxy);
        }
        return handler;
    }
}

public sealed class OfficialUsageReader : IDisposable
{
    private const int MaximumErrorBytes = 16 * 1024;
    private readonly HttpClient http;
    private readonly Uri? proxyEndpoint;
    public OfficialUsageReader(HttpMessageHandler? handler = null, Uri? proxyEndpoint = null)
    {
        var transport = handler ?? WindowsHttp.Handler();
        this.proxyEndpoint = proxyEndpoint ?? (transport is HttpClientHandler { UseProxy: true, Proxy: WebProxy proxy } ? proxy.Address : null);
        http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
    }
    public async Task<Usage> ReadAsync(LoginCredential credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", credential.AccountID);
        request.Headers.Accept.ParseAdd("application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) {
                if (response.StatusCode == HttpStatusCode.Unauthorized &&
                    await HasRevokedAuthorizationAsync(response.Content, deadline.Token, ct))
                    throw new QuotaReadException(QuotaReadException.AuthorizationRevoked);
                throw QuotaReadException.FromStatus(response.StatusCode);
            }
            await response.Content.LoadIntoBufferAsync(1024 * 1024, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            try { return Parse(body, DateTimeOffset.UtcNow); }
            catch (JsonException) { throw new QuotaReadException(QuotaReadException.InvalidResponse); }
            catch (QuotaReadException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or FormatException or OverflowException) {
                throw new QuotaReadException(QuotaReadException.InvalidResponse);
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new QuotaReadException(proxyEndpoint != null ? QuotaReadException.ProxyTimeout : QuotaReadException.Timeout);
        } catch (HttpRequestException error) {
            throw QuotaReadException.FromTransport(error, proxyEndpoint);
        }
    }

    private static async Task<bool> HasRevokedAuthorizationAsync(HttpContent content, CancellationToken deadline, CancellationToken caller)
    {
        // Never expose server messages or buffer an unbounded error response. A
        // broken error body does not change the already confirmed HTTP 401.
        try {
            caller.ThrowIfCancellationRequested();
            if (content.Headers.ContentLength > MaximumErrorBytes) return false;
            using var stream = await content.ReadAsStreamAsync(deadline);
            var bytes = new byte[MaximumErrorBytes + 1];
            var count = 0;
            while (count < bytes.Length) {
                var read = await stream.ReadAsync(bytes.AsMemory(count), deadline);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumErrorBytes) return false;
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            root.TryGetProperty("error", out var error);
            if (HasErrorCode(root, IsRevokedCode) || HasErrorCode(error, IsRevokedCode)) return true;
            // Structured expiry/reuse codes take priority over incidental text
            // in either envelope, so a generic 401 is not labeled revoked.
            if (HasErrorCode(root, IsOtherAuthenticationCode) || HasErrorCode(error, IsOtherAuthenticationCode)) return false;
            return HasRevocationMessage(root) || HasRevocationMessage(error);
        } catch (OperationCanceledException) when (!caller.IsCancellationRequested) {
            return false;
        } catch (Exception error) when (error is JsonException or IOException or HttpRequestException) {
            caller.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static bool HasErrorCode(JsonElement error, Func<string?, bool> matches)
    {
        if (error.ValueKind == JsonValueKind.String) return matches(error.GetString());
        if (error.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "code", "type", "error_code" })
            if (error.TryGetProperty(name, out var code) && code.ValueKind == JsonValueKind.String && matches(code.GetString()))
                return true;
        return false;
    }

    private static bool HasRevocationMessage(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return false;
        if (!error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return false;
        var text = message.GetString()!;
        return text.Contains("authentication token has been invalidated", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication token has been revoked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("refresh token has been invalidated", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("refresh token has been revoked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("access token has been invalidated", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("access token has been revoked", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRevokedCode(string? code) => code?.Trim().ToLowerInvariant() is
        "refresh_token_invalidated" or "token_invalidated" or "token_revoked";

    private static bool IsOtherAuthenticationCode(string? code) => code?.Trim().ToLowerInvariant() is
        "refresh_token_reused" or "token_reused" or "refresh_token_expired" or "token_expired" or
        "expired_token" or "invalid_grant" or "invalid_refresh_token";

    public static Usage Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var plan = root.TryGetProperty("plan_type", out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(plan)) plan = null;
        if (!root.TryGetProperty("rate_limit", out var limits) || limits.ValueKind != JsonValueKind.Object)
            throw new QuotaReadException(QuotaReadException.MissingWindow, plan);

        var windows = new List<QuotaWindow>(2);
        foreach (var name in new[] { "primary_window", "secondary_window" }) {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (!window.TryGetProperty("limit_window_seconds", out var duration) || duration.ValueKind != JsonValueKind.Number ||
                !duration.TryGetInt64(out var seconds) || seconds <= 0 ||
                !window.TryGetProperty("used_percent", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var used) || !double.IsFinite(used)) continue;
            DateTimeOffset? reset = null;
            if (window.TryGetProperty("reset_at", out var absolute) && absolute.ValueKind == JsonValueKind.Number && absolute.TryGetInt64(out var epoch))
                reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
            else if (window.TryGetProperty("reset_after_seconds", out var relative) && relative.ValueKind == JsonValueKind.Number && relative.TryGetInt64(out var after))
                reset = now.AddSeconds(after);
            var remaining = (int)Math.Clamp(Math.Round(100 - used, MidpointRounding.AwayFromZero), 0, 100);
            windows.Add(new(seconds, remaining, reset));
        }
        if (windows.Count == 0) throw new QuotaReadException(QuotaReadException.MissingWindow, plan);
        int? credits = null;
        if (root.TryGetProperty("rate_limit_reset_credits", out var credit) && credit.ValueKind == JsonValueKind.Object &&
            credit.TryGetProperty("available_count", out var count) && count.TryGetInt32(out var available) && available >= 0) credits = available;
        var weekly = windows.Find(window => window.Seconds == 604800);
        var five = windows.Find(window => window.Seconds == 18000);
        return new Usage(weekly?.RemainingPercent, weekly?.ResetsAt, five?.RemainingPercent, five?.ResetsAt,
            credits, PlanType: plan, Windows: windows.ToArray());
    }
    public void Dispose() => http.Dispose();
}
