using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountSwitcher.Core;

public enum AccountExportFormat { Star, AuthJson, Sub2api, Cpa }
public interface IAccountExporter
{
    string ExportAccount(Guid id, AccountExportFormat format);
}

public static class AccountExport
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Iso(DateTimeOffset date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static JsonObject Claims(string? token)
    {
        try {
            var part = token!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            return JsonNode.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '='))) as JsonObject ?? new();
        } catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or NullReferenceException or JsonException) { return new(); }
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    public static string Build(LoginCredential credential, AccountRow row, AccountExportFormat format)
    {
        var auth = JsonNode.Parse(credential.Bytes)!.AsObject();
        var tokens = auth["tokens"]!.AsObject();
        var access = credential.AccessToken;
        var id = Text(tokens["id_token"]) ?? "";
        var refresh = Text(tokens["refresh_token"]) ?? "";
        var lastRefresh = Text(auth["last_refresh"]);
        if (DateTimeOffset.TryParse(lastRefresh, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)) lastRefresh = Iso(last);
        var accessClaims = Claims(access);
        var idClaims = Claims(id);
        DateTimeOffset? expires = null;
        if (accessClaims["exp"] is JsonValue exp && exp.TryGetValue<long>(out var seconds)) {
            try { expires = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        }
        JsonObject result;
        switch (format) {
            case AccountExportFormat.AuthJson:
                result = new() {
                    ["OPENAI_API_KEY"] = null,
                    ["tokens"] = new JsonObject { ["id_token"] = id, ["access_token"] = access, ["refresh_token"] = refresh, ["account_id"] = credential.AccountID },
                    ["last_refresh"] = lastRefresh, ["type"] = "codex"
                };
                break;
            case AccountExportFormat.Star:
            case AccountExportFormat.Cpa:
                result = new() { ["id_token"] = id, ["access_token"] = access, ["refresh_token"] = refresh,
                    ["account_id"] = credential.AccountID, ["last_refresh"] = lastRefresh ?? "", ["email"] = credential.Email,
                    ["type"] = "codex", ["expired"] = expires is { } until ? Iso(until) : "" };
                break;
            case AccountExportFormat.Sub2api:
                var credentials = new JsonObject { ["access_token"] = access, ["id_token"] = id,
                    ["email"] = credential.Email, ["chatgpt_account_id"] = credential.AccountID };
                if (expires is { } tokenUntil) credentials["expires_at"] = Iso(tokenUntil);
                if (refresh.Length > 0) { credentials["refresh_token"] = refresh; credentials["client_id"] = "app_EMoamEEZ73f0CkXaXp7hrann"; }
                var idAuth = idClaims["https://api.openai.com/auth"] as JsonObject;
                var accessAuth = accessClaims["https://api.openai.com/auth"] as JsonObject;
                var userId = Text(idAuth?["chatgpt_user_id"]) ?? Text(accessAuth?["chatgpt_user_id"]) ?? Text(idAuth?["user_id"]) ?? Text(accessAuth?["user_id"]) ?? Text(idClaims["sub"]);
                if (userId != null) credentials["chatgpt_user_id"] = userId;
                if (credential.OrganizationID != null) credentials["organization_id"] = credential.OrganizationID;
                if ((row.PlanType ?? credential.PlanType) is { } plan) credentials["plan_type"] = plan;
                if ((row.Subscription?.ActiveUntil ?? credential.SubscriptionActiveUntil) is { } subscriptionUntil) credentials["subscription_expires_at"] = Iso(subscriptionUntil);
                var item = new JsonObject { ["name"] = credential.Email, ["platform"] = "openai", ["type"] = "oauth",
                    ["concurrency"] = 3, ["priority"] = 50, ["credentials"] = credentials };
                if (Text(idClaims["auth_provider"]) is { } provider) item["extra"] = new JsonObject { ["auth_provider"] = provider };
                if (refresh.Length == 0) {
                    if (expires == null) throw new IOException("此账号没有刷新凭据，且无法确定访问凭据到期时间，暂不能导出 sub2api 格式。");
                    item["expires_at"] = expires.Value.ToUnixTimeSeconds(); item["auto_pause_on_expired"] = true;
                }
                result = new() { ["type"] = "sub2api-data", ["version"] = 1,
                    ["exported_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    ["proxies"] = new JsonArray(), ["accounts"] = new JsonArray(item) };
                break;
            default: throw new IOException("不支持此导出格式。");
        }
        if (format == AccountExportFormat.Star) {
            if (!string.IsNullOrWhiteSpace(row.Profile.DisplayName) && row.Profile.DisplayName != credential.Email)
                result["account_name"] = row.Profile.DisplayName;
            return new JsonArray(result).ToJsonString(Options);
        }
        return result.ToJsonString(Options);
    }
    public static string Preview(string json)
    {
        var node = JsonNode.Parse(json)!;
        void Mask(JsonNode? current) {
            if (current is JsonObject obj) {
                foreach (var key in obj.Select(pair => pair.Key).ToArray()) {
                    if (key is "access_token" or "refresh_token" or "id_token") obj[key] = "••••••（已隐藏）";
                    else Mask(obj[key]);
                }
            } else if (current is JsonArray array) foreach (var child in array) Mask(child);
        }
        Mask(node); return node.ToJsonString(Options);
    }
}
