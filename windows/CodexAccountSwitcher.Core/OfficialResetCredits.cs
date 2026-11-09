using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

public sealed record BankedCredit(string Id, string Status, string ResetType, DateTimeOffset? ExpiresAt);
public sealed record ResetCreditSnapshot(int? AvailableCount, DateTimeOffset? EarliestExpiry, BankedCredit[]? Credits = null);

public interface IResetCreditService
{
    Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct);
    Task ConsumeAsync(LoginCredential credential, CancellationToken ct);
}

public sealed class ResetCreditException(string message, bool outcomeUncertain = false, int? availableCount = null) : IOException(message)
{
    public bool OutcomeUncertain { get; } = outcomeUncertain;
    public int? AvailableCount { get; } = availableCount;
}

// Independently implemented Windows adapter for the authenticated account endpoint.
// A consume request is never retried, including after timeout or an ambiguous server error.
public sealed class OfficialResetCredits : IResetCreditService, IDisposable
{
    private const string Endpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> now;
    public OfficialResetCredits(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        http = new HttpClient(handler ?? WindowsHttp.Handler()) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, LoginCredential credential)
    {
        var request = new HttpRequestMessage(method, url);
        try {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Add("ChatGPT-Account-Id", credential.AccountID);
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        } catch (Exception ex) when (ex is FormatException or ArgumentException) {
            request.Dispose(); throw new ResetCreditException("登录凭据格式无效，请重新授权或导入有效凭据。");
        }
    }

    public async Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct)
    {
        try {
            using var request = Request(HttpMethod.Get, Endpoint, credential);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode, consuming: false);
            return Parse(await response.Content.ReadAsStringAsync(ct), now());
        } catch (ResetCreditException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or FormatException or IOException or InvalidOperationException) {
            throw new ResetCreditException("读取重置次数失败，请检查网络或重新授权后重试。");
        }
    }

    public async Task ConsumeAsync(LoginCredential credential, CancellationToken ct)
        => await ConsumeAsync(credential, null, Guid.NewGuid().ToString("D"), ct);

    internal async Task ConsumeAsync(LoginCredential credential, string? creditId, string requestId, CancellationToken ct)
    {
        var available = await ReadAsync(credential, ct);
        if (available.AvailableCount is not > 0)
            throw new ResetCreditException(available.AvailableCount == 0 ? "没有可用的重置次数。" : "无法确认可用重置次数，未执行重置。请先刷新。", availableCount: available.AvailableCount);
        if (creditId != null && available.Credits?.Any(c => c.Id == creditId && c.Status == "available" && c.ExpiresAt > now()) != true)
            throw new ResetCreditException("待重置的卡已不可用，未执行重置，请刷新后核对。");
        ct.ThrowIfCancellationRequested();
        using var request = Request(HttpMethod.Post, Endpoint + "/consume", credential);
        request.Content = new StringContent(JsonSerializer.Serialize(new { redeem_request_id = requestId }), Encoding.UTF8, "application/json");
        try {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode, consuming: true);
        } catch (ResetCreditException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) {
            throw new ResetCreditException("重置结果尚未确认，已停止重试。请先刷新次数和额度核对，勿重复重置。", outcomeUncertain: true);
        }
    }

    private static ResetCreditException HttpFailure(HttpStatusCode status, bool consuming)
    {
        var code = (int)status;
        if (status == HttpStatusCode.Unauthorized) return new("登录凭据失效（401），请重新授权或导入有效凭据。");
        if (consuming && (code >= 500 || status is HttpStatusCode.RequestTimeout))
            return new($"重置结果尚未确认（HTTP {code}），已停止重试。请先刷新次数和额度核对。", outcomeUncertain: true);
        return new($"{(consuming ? "重置请求" : "读取重置次数")}未完成（HTTP {code}），请检查账号状态或稍后重试。");
    }

    public static ResetCreditSnapshot Parse(string json, DateTimeOffset now)
    {
        try {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new FormatException();
            var data = Property(root, "data");
            var countValue = Property(root, "available_count", "availableCount") ?? (data is { } wrapped ? Property(wrapped, "available_count", "availableCount") : null);
            int? count = countValue is { } counter && TryNonnegativeCount(counter, out var parsed) ? parsed : null;
            JsonElement? records = root.ValueKind == JsonValueKind.Array ? root : Property(root, "credits");
            records ??= data is { ValueKind: JsonValueKind.Array } ? data : data is { } nested ? Property(nested, "credits") : null;
            DateTimeOffset? earliest = null;
            var banked = new List<BankedCredit>();
            if (records is { ValueKind: JsonValueKind.Array } entries) {
                var derived = 0; var complete = true;
                foreach (var entry in entries.EnumerateArray()) {
                    if (entry.ValueKind != JsonValueKind.Object) { complete = false; continue; }
                    var expiry = Timestamp(Property(entry, "expires_at", "expire_at", "expiresAt"));
                    var statusField = Property(entry, "status", "state");
                    var status = statusField is { ValueKind: JsonValueKind.String } statusText ? statusText.GetString()?.Trim().ToLowerInvariant() : null;
                    if (expiry <= now || Timestamp(Property(entry, "redeemed_at", "used_at", "consumed_at", "redeemedAt")) != null ||
                        status is "used" or "consumed" or "redeemed" or "expired" or "revoked") continue;
                    var recognizable = expiry != null || Property(entry, "id", "credit_id", "creditId") != null || status != null;
                    if (!recognizable || status is not (null or "available" or "active" or "unredeemed")) { complete = false; continue; }
                    derived++;
                    var id = Property(entry, "id", "credit_id", "creditId");
                    if (id is { ValueKind: JsonValueKind.String } identifier && !string.IsNullOrWhiteSpace(identifier.GetString()))
                        banked.Add(new(identifier.GetString()!, "available", "codexRateLimits", expiry));
                    if (expiry != null && (earliest == null || expiry < earliest)) earliest = expiry;
                }
                if (count == null && complete) count = derived;
            }
            if (count == 0) earliest = null;
            return new(count, earliest, records is { ValueKind: JsonValueKind.Array } ? banked.ToArray() : null);
        } catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentOutOfRangeException) {
            throw new ResetCreditException("重置次数响应格式无法识别，未将未知次数显示为 0。");
        }
    }

    private static JsonElement? Property(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names) if (value.TryGetProperty(name, out var property) && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return property;
        return null;
    }
    private static bool TryNonnegativeCount(JsonElement value, out int count)
    {
        count = -1;
        return (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out count) ||
            value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out count)) && count >= 0;
    }
    private static DateTimeOffset? Timestamp(JsonElement? value)
    {
        if (value is not { } timestamp) return null;
        long epoch;
        if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out epoch) ||
            timestamp.ValueKind == JsonValueKind.String && long.TryParse(timestamp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch)) {
            try { return epoch > 999999999999 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return timestamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
    public void Dispose() => http.Dispose();
}
