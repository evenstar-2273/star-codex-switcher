using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountSwitcher.Core;

// Values stay in memory/local private files; never include them in errors or UI snapshots.
public sealed record LoginCredential(string AccountID, string Email, string AccessToken, byte[] Bytes, string? PlanType = null, string? OrganizationID = null)
{
    public static LoginCredential Read(string path)
    {
        SecureFiles.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            throw new IOException("登录文件缺失或过大。请重新添加账号。 / Invalid login file.");
        return Parse(File.ReadAllBytes(path));
    }

    public static LoginCredential Parse(byte[] bytes)
    {
        try {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            var token = root.GetProperty("tokens");
            var access = token.GetProperty("access_token").GetString();
            var idToken = token.GetProperty("id_token").GetString();
            if (string.IsNullOrWhiteSpace(access) || access.Contains('*') || string.IsNullOrWhiteSpace(idToken))
                throw new FormatException();
            var claims = Claims(idToken);
            var claimID = ClaimAccount(claims);
            var account = token.TryGetProperty("account_id", out var field) ? field.GetString() : claimID;
            account = string.IsNullOrWhiteSpace(account) ? claimID : account;
            if (string.IsNullOrWhiteSpace(account) || account.Contains('*') || (claimID != null && claimID != account))
                throw new FormatException();
            var plan = ClaimPlan(claims);
            var organization = ClaimOrganization(claims);
            if (access.Split('.').Length == 3) {
                var accessClaims = Claims(access);
                var accessID = ClaimAccount(accessClaims);
                if (accessID != null && accessID != account) throw new FormatException();
                plan ??= ClaimPlan(accessClaims);
                organization = ClaimOrganization(accessClaims) ?? organization;
            }
            var email = claims.TryGetProperty("email", out var e) ? e.GetString() : null;
            return new(account, email ?? account, access, bytes, plan, organization);
        } catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException) {
            throw new IOException("账号凭据格式不完整、被打码或身份不一致。请导出完整 JSON，或重新登录。 / Invalid credential metadata.");
        }
    }

    private static JsonElement Claims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new FormatException();
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.Clone();
    }

    private static string? ClaimAccount(JsonElement claims) =>
        claims.TryGetProperty("https://api.openai.com/auth", out var auth) &&
        auth.TryGetProperty("chatgpt_account_id", out var id) ? id.GetString() : null;

    private static string? ClaimPlan(JsonElement claims)
    {
        if (claims.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object &&
            auth.TryGetProperty("chatgpt_plan_type", out var plan) && plan.ValueKind == JsonValueKind.String) {
            var value = plan.GetString()?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        return null;
    }

    private static string? ClaimOrganization(JsonElement claims)
    {
        if (!claims.TryGetProperty("https://api.openai.com/auth", out var auth) || auth.ValueKind != JsonValueKind.Object) return null;
        string? String(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
            && field.GetString()?.Trim() is { Length: > 0 } text ? text : null;
        foreach (var key in new[] { "organization_id", "chatgpt_organization_id", "chatgpt_org_id", "org_id", "poid", "POID" })
            if (String(auth, key) is { } organization) return organization;
        if (auth.TryGetProperty("organizations", out var groups) && groups.ValueKind == JsonValueKind.Array) {
            var records = groups.EnumerateArray().ToArray();
            var primary = records.FirstOrDefault(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("is_default", out var flag) && flag.ValueKind == JsonValueKind.True);
            return String(primary, "id") ?? (records.Length > 0 ? String(records[0], "id") : null);
        }
        return null;
    }

    public static LoginCredential FromExport(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new IOException("JSON 中的账号必须是对象。 / Expected an account object.");
        var source = row.TryGetProperty("tokens", out var tokens) ? tokens : row;
        string? Get(string key) => source.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        var account = Get("account_id");
        if (account == null && row.TryGetProperty("account_id", out var aid) && aid.ValueKind == JsonValueKind.String) account = aid.GetString();
        var auth = new JsonObject {
            ["auth_mode"] = "chatgpt", ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject { ["id_token"] = Get("id_token"), ["access_token"] = Get("access_token"),
                ["refresh_token"] = Get("refresh_token") ?? "", ["account_id"] = account }
        };
        // Preserve a real refresh timestamp; never invent a fresh one for an old token.
        if (row.TryGetProperty("last_refresh", out var last) && last.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(last.GetString(), out var timestamp)) auth["last_refresh"] = timestamp.ToString("O");
        var parsed = Parse(Encoding.UTF8.GetBytes(auth.ToJsonString()));
        auth["tokens"]!["account_id"] = parsed.AccountID;
        return Parse(Encoding.UTF8.GetBytes(auth.ToJsonString()));
    }
}
