using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

public sealed record SubscriptionLookup(string? AccountID, string? PlanType, DateTimeOffset? ActiveUntil, string Source, bool IsVerified = true) {
    public bool IsConfirmedFree(DateTimeOffset now) => IsVerified && Source == "accounts_check" &&
        SubscriptionPlan.NormalizeBillingPlan(PlanType) == "free" && (ActiveUntil == null || ActiveUntil <= now);
}

public interface ISubscriptionReader
{
    Task<SubscriptionLookup> ReadAsync(LoginCredential credential, CancellationToken ct);
}

// Query current billing even before the login snapshot expires, so renewals become visible.
// It never refreshes tokens, changes the selected workspace, or infers a billing period.
public sealed class OfficialSubscriptionReader : ISubscriptionReader, IDisposable
{
    private const string CheckPath = "/backend-api/accounts/check/v4-2023-04-27";
    private const string SubscriptionPath = "/backend-api/subscriptions";
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> now;
    public OfficialSubscriptionReader(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        http = new HttpClient(handler ?? WindowsHttp.Handler()) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SubscriptionLookup> ReadAsync(LoginCredential credential, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var offset = -(int)TimeZoneInfo.Local.GetUtcOffset(now()).TotalMinutes;
        var accounts = await GetAsync(CheckPath, "timezone_offset_min=" + offset.ToString(CultureInfo.InvariantCulture), credential, ct);
        var selected = ParseAccountCheck(accounts, credential.AccountID, credential.OrganizationID, now());
        if (selected.IsConfirmedFree(now())) return selected with { ActiveUntil = null };
        if (selected.ActiveUntil > now()) return selected;
        var accountId = selected.AccountID ?? credential.AccountID;
        var subscriptions = await GetAsync(SubscriptionPath, "account_id=" + Uri.EscapeDataString(accountId), credential, ct);
        var current = ParseSubscriptions(subscriptions, accountId);
        // An empty fallback does not certify the older expired entitlement as the current expiry.
        return current with { PlanType = current.PlanType ?? selected.PlanType };
    }

    private async Task<string> GetAsync(string path, string query, LoginCredential credential, CancellationToken ct)
    {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com" + path + "?" + query);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Referrer = new Uri("https://chatgpt.com/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36");
            request.Headers.Add("x-openai-target-path", path);
            request.Headers.Add("x-openai-target-route", path);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SubscriptionReadException("订阅有效期尚未核验：登录凭据失效（401），请重新授权。");
            if (!response.IsSuccessStatusCode) throw new SubscriptionReadException($"订阅有效期尚未核验（HTTP {(int)response.StatusCode}），请稍后刷新。");
            return await response.Content.ReadAsStringAsync(ct);
        } catch (SubscriptionReadException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or FormatException or ArgumentException or InvalidOperationException) {
            throw new SubscriptionReadException("订阅有效期尚未核验，请检查网络或重新授权后刷新。");
        }
    }

    private sealed record AccountCandidate(string? Key, string? AccountID, string? Plan, DateTimeOffset? Expiry, bool IsDefault);
    public static SubscriptionLookup ParseAccountCheck(string json, string? accountId, string? organizationId = null, DateTimeOffset? now = null)
    {
        try {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var records = new List<AccountCandidate>();
            var accounts = Field(root, "accounts");
            if (accounts is { ValueKind: JsonValueKind.Object } map) {
                foreach (var pair in map.EnumerateObject()) Add(pair.Value, pair.Name);
            } else if (accounts is { ValueKind: JsonValueKind.Array } array) {
                foreach (var row in array.EnumerateArray()) Add(row, null);
            } else if (root.ValueKind == JsonValueKind.Array) {
                foreach (var row in root.EnumerateArray()) Add(row, null);
            }
            void Add(JsonElement row, string? key) {
                if (row.ValueKind != JsonValueKind.Object) return;
                var account = Field(row, "account") is { ValueKind: JsonValueKind.Object } inner ? inner : row;
                var entitlement = Field(row, "entitlement");
                var plan = entitlement is { } ent ? Text(ent, "subscription_plan") : null;
                var expiry = entitlement is { } grant ? SubscriptionDate.Parse(Field(grant, "expires_at")) : null;
                expiry ??= SubscriptionDate.Parse(Field(account, "expires_at"));
                var accountPlan = Text(account, "plan_type", "planType");
                if (SubscriptionPlan.NormalizeBillingPlan(accountPlan) == "free" && expiry <= (now ?? DateTimeOffset.UtcNow)) plan = accountPlan;
                records.Add(new(key, Text(account, "account_id", "id", "chatgpt_account_id", "workspace_id"),
                    plan ?? Text(account, "plan_type", "planType"), expiry ?? SubscriptionDate.Parse(Field(account, "expires_at")),
                    Field(account, "is_default") is { ValueKind: JsonValueKind.True }));
            }
            var selected = records.FirstOrDefault(row => organizationId != null && row.Key == organizationId)
                ?? records.FirstOrDefault(row => accountId != null && row.AccountID == accountId)
                ?? records.FirstOrDefault(row => row.IsDefault)
                ?? records.FirstOrDefault(row => row.Plan != null && !row.Plan.Equals("free", StringComparison.OrdinalIgnoreCase))
                ?? records.FirstOrDefault()
                ?? throw new SubscriptionReadException("订阅接口未返回可识别的账号，有效期仍待核验。");
            if (accountId != null && selected.AccountID != accountId &&
                !(organizationId != null && selected.Key == organizationId))
                return new(accountId, null, null, "accounts_check");
            return new(selected.AccountID, selected.Plan, selected.Expiry, "accounts_check");
        } catch (SubscriptionReadException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) {
            throw new SubscriptionReadException("订阅账号响应格式无法识别，有效期仍待核验。");
        }
    }

    public static SubscriptionLookup ParseSubscriptions(string json, string accountId)
    {
        try {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException();
            return new(accountId, Text(root, "subscription_plan", "plan_type"),
                SubscriptionDate.Parse(Field(root, "active_until")) ?? SubscriptionDate.Parse(Field(root, "expires_at")), "subscriptions");
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) {
            throw new SubscriptionReadException("订阅响应格式无法识别，有效期仍待核验。");
        }
    }
    private static JsonElement? Field(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names) if (value.TryGetProperty(name, out var field) && field.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return field;
        return null;
    }
    private static string? Text(JsonElement value, params string[] names)
    {
        foreach (var name in names) {
            if (Field(value, name) is { ValueKind: JsonValueKind.String } item && item.GetString()?.Trim() is { Length: > 0 } text) return text;
        }
        return null;
    }
    public void Dispose() => http.Dispose();
}

internal static class SubscriptionDate
{
    internal static DateTimeOffset? Parse(JsonElement? value)
    {
        if (value is not { } item) return null;
        long number;
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out number) ||
            item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) {
            if (number <= 0) return null;
            try { return number > 999999999999 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (item.ValueKind != JsonValueKind.String || item.GetString()?.Trim() is not { Length: > 0 and <= 128 } text) return null;
        // A date without an offset is ambiguous; do not silently reinterpret it as UTC.
        if (!Regex.IsMatch(text, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt ][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})$")) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
    }
}

public sealed class SubscriptionReadException(string message) : IOException(message);

public static class SubscriptionPlan
{
    public static string? Resolve(string? subscriptionPlan, string? usagePlan, string? tokenPlan,
        DateTimeOffset? verifiedActiveUntil = null, DateTimeOffset? now = null)
    {
        if (verifiedActiveUntil <= (now ?? DateTimeOffset.UtcNow)) return "free";
        // Billing entitlements describe the subscription period; the quota response
        // describes the current effective plan and can already be FREE during that period.
        if (!string.IsNullOrWhiteSpace(usagePlan)) return usagePlan.Trim();
        return NormalizeBillingPlan(subscriptionPlan) ?? NormalizeBillingPlan(tokenPlan);
    }
    internal static string? NormalizeBillingPlan(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch {
            "chatgptfreeplan" => "free", "chatgptplusplan" => "plus",
            "chatgptproplan" => "pro", "chatgptteamplan" => "team",
            "chatgptbusinessplan" => "business", "chatgptenterpriseplan" => "enterprise",
            _ => value.Trim()
        };
    }
}
