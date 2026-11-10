using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;

internal static class PlanChecks
{
    public static async Task<int> RunAsync()
    {
        try {
            var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
            EffectiveQuotaChecks(now);
            foreach (var plan in new[] { "free", "plus", "prolite", "pro", "promax" }) {
                var credential = Credential(plan);
                Require(credential.PlanType == plan, "The ID token's plan must survive credential parsing.");
                var usage = OfficialUsageReader.Parse(Response(plan), now);
                Require(usage.PlanType == plan && usage.FiveHourRemainingPercent == null, "Official plan and weekly-only quota are independent metadata.");
            }
            var freeWindow = OfficialUsageReader.Parse("""
                {"plan_type":"free","rate_limit":{"primary_window":{"limit_window_seconds":2592000,"used_percent":0,"reset_after_seconds":2591940}}}
                """, now);
            var monthly = freeWindow.DisplayWindows().Single();
            Require(freeWindow.PlanType == "free" && monthly.Seconds == 2592000 && monthly.RemainingPercent == 100 &&
                monthly.ResetsAt == now.AddSeconds(2591940) && freeWindow.RemainingPercent == null, "A free monthly window is not a weekly allowance.");
            using (var missing = new OfficialUsageReader(new Handler("""{"plan_type":"free","rate_limit":{}}"""))) {
                try { await missing.ReadAsync(Credential(null), default); throw new Exception("Expected missing quota."); }
                catch (QuotaReadException error) { Require(error.PlanType == "free" && error.Message == QuotaReadException.MissingWindow,
                    "Reading the plan does not require a valid quota window."); }
            }
            Require(Credential(null, "pro500@example.test", "pro-200-file-name").PlanType == null,
                "An email or account identifier must not be used to guess a plan.");
            Require(Credential(100).PlanType == null, "Malformed optional plan metadata must remain unknown.");
            Require(Credential("  plus  ").PlanType == "plus", "Whitespace in a claim is normalized.");
            Require(Credential(null, accessPlan: "prolite").PlanType == "prolite", "An access-token claim can supply absent ID-token plan metadata.");
            var exported = JsonNode.Parse(Credential("plus").Bytes)!;
            exported["plan_type"] = "promax";
            using (var document = JsonDocument.Parse(exported.ToJsonString()))
                Require(LoginCredential.FromExport(document.RootElement).PlanType == "plus", "An unverified export hint cannot override the actual token claim.");
            Require(OfficialUsageReader.Parse(Response(null), now).PlanType == null, "Absent official plan is unknown rather than free.");
            Require(OfficialUsageReader.Parse(Response(500), now).PlanType == null, "Non-string official plan is unknown.");
            Require(OfficialUsageReader.Parse(Response("future-plan"), now).PlanType == "future-plan", "Unrecognized official metadata must not be relabeled as an old JWT tier.");
            using (var reader = new OfficialUsageReader(new Handler(Response("promax")))) {
                var usage = await reader.ReadAsync(Credential("plus"), CancellationToken.None);
                Require(usage.PlanType == "promax", "The authenticated usage response takes precedence over a stale token claim.");
            }
            var legacy = JsonSerializer.Deserialize<AccountRow>("{\"profile\":{\"id\":\"11111111-1111-1111-1111-111111111111\",\"displayName\":\"example\",\"email\":null},\"initials\":\"E\",\"usage\":null,\"usageError\":null}", AccountSnapshot.JsonOptions)!;
            Require(legacy.PlanType == null, "Older Host snapshots stay compatible and do not imply FREE.");
            Console.WriteLine("PASS: plan metadata, authoritative response, missing/invalid claim and legacy snapshot checks (synthetic only).");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void EffectiveQuotaChecks(DateTimeOffset now)
    {
        var raw = OfficialUsageReader.Parse("""
            {"rate_limit":{"primary_window":{"limit_window_seconds":18000,"used_percent":0,"reset_after_seconds":18000},
            "secondary_window":{"limit_window_seconds":604800,"used_percent":100,"reset_after_seconds":172800}}}
            """, now);
        var display = raw.DisplayWindows().ToArray();
        Require(display.Length == 2 && display.All(w => w.RemainingPercent == 0), "Weekly exhaustion blocks displayed five-hour availability.");
        Require(raw.FiveHourRemainingPercent == 100 && raw.Windows![0].RemainingPercent == 100, "Display policy never mutates raw quota or cached values.");
        Require(display[0].ResetsAt == now.AddHours(5) && display[1].ResetsAt == now.AddDays(2), "Each window retains its original reset time.");
        var legacy = new Usage(0, now.AddDays(2), 100, now.AddHours(5));
        Require(legacy.DisplayWindows().First().RemainingPercent == 0, "Old cached snapshots use the same display policy.");
        Require((legacy with { RemainingPercent = 1 }).DisplayWindows().First().RemainingPercent == 100, "Renewed weekly availability restores the raw short-window percentage.");
        Require(new Usage(42, now, 0, now).DisplayWindows().Last().RemainingPercent == 42, "Short-window exhaustion does not zero the weekly window.");
        Require(new Usage(null, null, 100, now).DisplayWindows().Single().RemainingPercent == 100, "A missing weekly window is not treated as exhausted.");
        Require(new Usage(0, now, null, null).DisplayWindows().Single().Seconds == 604800, "Weekly-only accounts do not gain an invented short window.");
        Require((legacy with { Windows = [new(2592000, 100, now)] }).DisplayWindows().Single().RemainingPercent == 100,
            "Explicit monthly windows ignore stale legacy weekly fields.");
        Require((legacy with { Windows = [] }).DisplayWindows().Count() == 0, "An explicit empty window list stays empty.");
    }

    private static LoginCredential Credential(object? plan, string email = "plan@example.test", string id = "synthetic-plan-account", string? accessPlan = null)
    {
        string Token(object? value) {
            var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> {
                ["email"] = email,
                ["https://api.openai.com/auth"] = new { chatgpt_account_id = id, chatgpt_plan_type = value }
            });
            return "e30." + Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
        }
        return LoginCredential.Parse(JsonSerializer.SerializeToUtf8Bytes(new {
            auth_mode = "chatgpt", tokens = new {
                account_id = id, id_token = Token(plan), access_token = accessPlan == null ? "synthetic-access" : Token(accessPlan)
            }
        }));
    }

    private static string Response(object? plan) => JsonSerializer.Serialize(new {
        plan_type = plan,
        rate_limit = new { primary_window = new { used_percent = 15, limit_window_seconds = 604800, reset_after_seconds = 3600 } }
    });
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }
}
