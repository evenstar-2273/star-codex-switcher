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
        try {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw QuotaReadException.FromStatus(response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(ct);
            try { return Parse(body, DateTimeOffset.UtcNow); }
            catch (JsonException) { throw new QuotaReadException(QuotaReadException.InvalidResponse); }
            catch (IOException) { throw new QuotaReadException(QuotaReadException.MissingWindow); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or FormatException or OverflowException) {
                throw new QuotaReadException(QuotaReadException.InvalidResponse);
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new QuotaReadException(proxyEndpoint != null ? QuotaReadException.ProxyTimeout : QuotaReadException.Timeout);
        } catch (HttpRequestException error) {
            throw QuotaReadException.FromTransport(error, proxyEndpoint);
        }
    }
    public static Usage Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("rate_limit", out var limits) || limits.ValueKind != JsonValueKind.Object) throw new IOException("服务尚未返回可识别的额度窗口。");
        int? five = null, weekly = null; DateTimeOffset? fiveReset = null, weekReset = null;
        foreach (var name in new[] { "primary_window", "secondary_window" }) {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (!window.TryGetProperty("limit_window_seconds", out var duration) || !duration.TryGetInt64(out var seconds) ||
                !window.TryGetProperty("used_percent", out var usedElement) || !usedElement.TryGetDouble(out var used) || !double.IsFinite(used)) continue;
            DateTimeOffset? reset = null;
            if (window.TryGetProperty("reset_at", out var absolute) && absolute.TryGetInt64(out var epoch)) reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
            else if (window.TryGetProperty("reset_after_seconds", out var relative) && relative.TryGetInt64(out var after)) reset = now.AddSeconds(after);
            if (!reset.HasValue) continue;
            var remaining = (int)Math.Clamp(Math.Round(100 - used, MidpointRounding.AwayFromZero), 0, 100);
            if (seconds == 18000) { five = remaining; fiveReset = reset; }
            else if (seconds >= 6 * 86400 && seconds <= 8 * 86400) { weekly = remaining; weekReset = reset; }
        }
        if (weekly == null || weekReset == null) throw new IOException("服务未返回周额度；不会将未知额度显示为 0。");
        int? credits = null;
        if (root.TryGetProperty("rate_limit_reset_credits", out var credit) && credit.ValueKind == JsonValueKind.Object &&
            credit.TryGetProperty("available_count", out var count) && count.TryGetInt32(out var available) && available >= 0) credits = available;
        var plan = root.TryGetProperty("plan_type", out var planField) && planField.ValueKind == JsonValueKind.String
            ? planField.GetString()?.Trim() : null;
        return new Usage(weekly.Value, weekReset.Value, five, fiveReset, credits,
            PlanType: string.IsNullOrEmpty(plan) ? null : plan);
    }
    public void Dispose() => http.Dispose();
}
