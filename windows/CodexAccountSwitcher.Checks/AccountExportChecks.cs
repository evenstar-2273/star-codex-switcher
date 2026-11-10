using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;

internal static class AccountExportChecks
{
    public static int Run()
    {
        var assertions = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); assertions++; }
        string Jwt(object data) => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(data)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
        var authClaims = new Dictionary<string, object> { ["chatgpt_account_id"] = "workspace-fixture", ["chatgpt_user_id"] = "user-fixture", ["organization_id"] = "org-fixture", ["chatgpt_plan_type"] = "plus" };
        var id = Jwt(new Dictionary<string, object> { ["email"] = "export@example.test", ["sub"] = "user-fixture", ["auth_provider"] = "password", ["https://api.openai.com/auth"] = authClaims });
        var access = Jwt(new Dictionary<string, object> { ["exp"] = 2000000000L, ["https://api.openai.com/auth"] = authClaims });
        var credential = LoginCredential.Parse(JsonSerializer.SerializeToUtf8Bytes(new { tokens = new { access_token = access, id_token = id, refresh_token = "fixture-refresh", account_id = "workspace-fixture" }, last_refresh = "2026-11-01T01:02:03Z" }));
        var row = new AccountRow(new(Guid.NewGuid(), "工作账号", credential.Email), "E", null, null,
            Subscription: new(DateTimeOffset.Parse("2026-12-01T00:00:00Z"), DateTimeOffset.UtcNow, "fixture", true), PlanType: "plus");
        foreach (var format in Enum.GetValues<AccountExportFormat>()) {
            var json = AccountExport.Build(credential, row, format);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var preview = AccountExport.Preview(json);
            Check(!preview.Contains(access) && !preview.Contains(id) && !preview.Contains("fixture-refresh"), "Preview must mask all token fields.");
            Check(json.Contains(access) && json.Contains(id) && json.Contains("fixture-refresh"), "Exports must retain complete credential values.");
            if (format != AccountExportFormat.Sub2api) {
                var imported = AccountImport.ParseText(json).Single();
                Check(imported.AccountID == credential.AccountID && imported.AccessToken == access, "Export/import must preserve workspace and token.");
                Check(JsonNode.Parse(imported.Bytes)!["last_refresh"]!.GetValue<string>().StartsWith("2026-11-01"), "Export must preserve real refresh time.");
            }
            switch (format) {
                case AccountExportFormat.Star:
                    Check(root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1 && root[0].GetProperty("account_name").GetString() == "工作账号", "Portable format is an array with display metadata."); break;
                case AccountExportFormat.AuthJson:
                    Check(root.GetProperty("tokens").GetProperty("account_id").GetString() == credential.AccountID && root.GetProperty("OPENAI_API_KEY").ValueKind == JsonValueKind.Null, "Official nested token format."); break;
                case AccountExportFormat.Cpa:
                    Check(root.ValueKind == JsonValueKind.Object && root.GetProperty("type").GetString() == "codex" && root.GetProperty("expired").GetString() == "2033-05-18T03:33:20.000Z", "CPA is a single portable object and uses access token expiry, not subscription expiry."); break;
                case AccountExportFormat.Sub2api:
                    var account = root.GetProperty("accounts")[0]; var tokens = account.GetProperty("credentials");
                    Check(root.GetProperty("type").GetString() == "sub2api-data" && root.GetProperty("version").GetInt32() == 1 && root.GetProperty("proxies").GetArrayLength() == 0, "Batch wrapper schema.");
                    Check(account.GetProperty("platform").GetString() == "openai" && account.GetProperty("type").GetString() == "oauth" && account.GetProperty("priority").GetInt32() == 50 && account.GetProperty("concurrency").GetInt32() == 3, "OAuth defaults.");
                    Check(tokens.GetProperty("chatgpt_account_id").GetString() == credential.AccountID && tokens.GetProperty("chatgpt_user_id").GetString() == "user-fixture" && tokens.GetProperty("organization_id").GetString() == "org-fixture", "Workspace, user and organization must remain distinct.");
                    Check(tokens.GetProperty("subscription_expires_at").GetString() == "2026-12-01T00:00:00.000Z" && tokens.GetProperty("expires_at").GetString() == "2033-05-18T03:33:20.000Z", "Subscription and token expiration must not be interchanged.");
                    Check(tokens.GetProperty("client_id").GetString() == "app_EMoamEEZ73f0CkXaXp7hrann" && account.GetProperty("extra").GetProperty("auth_provider").GetString() == "password", "Refresh client and provider metadata."); break;
            }
        }
        var noRefresh = JsonNode.Parse(credential.Bytes)!; noRefresh["tokens"]!["refresh_token"] = ""; noRefresh.AsObject().Remove("last_refresh");
        var partial = LoginCredential.Parse(Encoding.UTF8.GetBytes(noRefresh.ToJsonString()));
        using var limited = JsonDocument.Parse(AccountExport.Build(partial, row, AccountExportFormat.Sub2api));
        var limitedAccount = limited.RootElement.GetProperty("accounts")[0];
        Check(limitedAccount.GetProperty("expires_at").GetInt64() == 2000000000 && limitedAccount.GetProperty("auto_pause_on_expired").GetBoolean(), "Nonrefreshable token must auto-pause at actual token expiry.");
        Check(!limitedAccount.GetProperty("credentials").TryGetProperty("client_id", out _), "No refresh client without refresh token.");
        using var undated = JsonDocument.Parse(AccountExport.Build(partial, row, AccountExportFormat.Cpa));
        Check(undated.RootElement.GetProperty("last_refresh").GetString() == "", "Do not invent a fresh timestamp for an undated credential.");
        Console.WriteLine($"PASS: {assertions} export format, identity, expiry, preview and round-trip assertions (synthetic credentials only).");
        return 0;
    }
}
