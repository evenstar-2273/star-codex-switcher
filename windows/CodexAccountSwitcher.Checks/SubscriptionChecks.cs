using System.Net;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class SubscriptionChecks
{
    private static int passed;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00+08:00");
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    public static async Task<int> RunAsync()
    {
        try {
            ParseChecks(); PlanChecks(); await HttpChecks(); await LoginPeriodChecks(); await GuardedChecks(); await GuardedLoginPeriodChecks();
            Console.WriteLine($"PASS: {passed} subscription selection/date/HTTP/cache assertions (synthetic only, GET fixtures)."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static string Accounts(string expires = "2026-11-01T08:26:00Z", string plan = "plus") => JsonSerializer.Serialize(new {
        accounts = new[] { new { account = new { account_id = "workspace", plan_type = "free" }, entitlement = new { subscription_plan = plan, expires_at = expires } } }
    });
    private static void ParseChecks()
    {
        var map = """{"accounts":{"org-personal":{"account":{"account_id":"personal","plan_type":"free","is_default":true},"entitlement":{"subscription_plan":"free","expires_at":"2027-01-01T00:00:00Z"}},"org-team":{"account":{"account_id":"team","plan_type":"team"},"entitlement":{"subscription_plan":"plus","expires_at":"2026-12-01T08:26:00Z"}}}}""";
        var org = OfficialSubscriptionReader.ParseAccountCheck(map, "personal", "org-team");
        Require(org.AccountID == "team" && org.PlanType == "plus" && org.ActiveUntil == DateTimeOffset.Parse("2026-12-01T08:26:00Z"), "Organization key outranks account ID and entitlement outranks account-level fields.");
        Require(OfficialSubscriptionReader.ParseAccountCheck(map, "team").AccountID == "team", "Account ID matches before default.");
        Require(OfficialSubscriptionReader.ParseAccountCheck(map, "missing").AccountID == "personal", "Default precedes a paid-account fallback.");
        var array = """[{"account":{"id":"free","planType":"free","expires_at":"2099-01-01T00:00:00Z"}},{"account":{"workspace_id":"paid","planType":"prolite","expires_at":"2026-12-01T08:26:00Z"}}]""";
        Require(OfficialSubscriptionReader.ParseAccountCheck(array, "missing").AccountID == "paid", "Top-level arrays fall back to the paid account rather than the largest expiry.");
        Require(OfficialSubscriptionReader.ParseAccountCheck(array, "free").AccountID == "free", "A matched free workspace is not replaced by another paid workspace.");
        var result = OfficialSubscriptionReader.ParseSubscriptions("""{"active_until":"2026-12-01T16:26:00+08:00","expires_at":"2026-11-01T08:26:00Z","subscription_plan":"plus","plan_type":"free"}""", "workspace");
        Require(result.ActiveUntil == DateTimeOffset.Parse("2026-12-01T08:26:00Z") && result.PlanType == "plus", "Actual active_until and subscription_plan take priority; timezone preserves the precise instant.");
        Require(OfficialSubscriptionReader.ParseSubscriptions("{}", "workspace").ActiveUntil == null, "Empty subscription data never invents an expiry.");
        Require(OfficialSubscriptionReader.ParseSubscriptions("{\"active_until\":0}", "workspace").ActiveUntil == null, "Zero is not a meaningful billing expiry.");
        Require(OfficialSubscriptionReader.ParseSubscriptions("{\"active_until\":\"not-a-date\"}", "workspace").ActiveUntil == null, "Unknown dates are not shifted by a billing month.");
        foreach (var ambiguous in new[] { "2026-12-01", "2026-12-01T08:26:00", "2026-12-01 08:26:00" })
            Require(OfficialSubscriptionReader.ParseSubscriptions(JsonSerializer.Serialize(new { active_until = ambiguous }), "workspace").ActiveUntil == null,
                "A date with no explicit timezone cannot silently become a different UTC subscription instant.");
        var expected = DateTimeOffset.Parse("2026-12-01T08:26:00Z");
        foreach (var timestamp in new[] { expected.ToUnixTimeSeconds().ToString(), expected.ToUnixTimeMilliseconds().ToString(), "\"" + expected.ToUnixTimeSeconds() + "\"" })
            Require(OfficialSubscriptionReader.ParseSubscriptions("{\"active_until\":" + timestamp + "}", "workspace").ActiveUntil == expected, "Seconds, milliseconds and numeric strings normalize to the same instant.");
        try { OfficialSubscriptionReader.ParseAccountCheck("SECRET_BODY", "workspace"); throw new Exception("Malformed response accepted."); }
        catch (SubscriptionReadException ex) { Require(!ex.Message.Contains("SECRET_BODY"), "Parse errors never expose server body or tokens."); }
        var identity = Credential("org-from-id", "org-from-access");
        Require(identity.OrganizationID == "org-from-access", "Current access-token organization takes priority over the saved ID-token organization.");
        Require(Credential("org-from-id", null).OrganizationID == "org-from-id", "ID-token organization supplies the missing access-token field.");
    }
    private static void PlanChecks()
    {
        foreach (var generic in new[] { "pro", "chatgptpro", "pro-200" }) {
            Require(SubscriptionPlan.Resolve(generic, "prolite", "plus") == "prolite", "Generic subscription Pro does not hide the official Pro 100 quota tier.");
            Require(SubscriptionPlan.Resolve(generic, "promax", "plus") == "promax", "Generic subscription Pro retains the official Pro 500 quota tier.");
        }
        Require(SubscriptionPlan.Resolve("promax", "prolite", "plus") == "promax", "An explicit subscription tier takes priority over quota metadata.");
        Require(SubscriptionPlan.Resolve("free", "promax", "plus") == "free" && SubscriptionPlan.Resolve("plus", "prolite", "pro") == "plus", "A real subscription change to free/plus overrides old Pro metadata.");
        Require(SubscriptionPlan.Resolve(null, "future-plan", "plus") == "future-plan", "Unknown official plan strings remain unknown rather than reverting to JWT.");
        Require(SubscriptionPlan.Resolve("pro", "prolite", null, Now.AddSeconds(-1), Now) == "free", "Only a verified actual past expiry lowers the effective plan to free.");
        Require(SubscriptionPlan.Resolve("pro", "prolite", null, null, Now) == "prolite", "Unverified or missing expiry cannot lower a paid plan to free.");
    }
    private static async Task HttpChecks()
    {
        var credential = Credential(null, null); var original = credential.Bytes.ToArray(); var calls = new List<string>();
        using (var reader = new OfficialSubscriptionReader(new Handler(request => {
            calls.Add(request.RequestUri!.AbsolutePath);
            Require(request.Method == HttpMethod.Get && request.RequestUri.Host == "chatgpt.com" && request.RequestUri.Scheme == "https", "Subscription lookup is read-only against the official origin.");
            Require(request.Headers.Authorization?.Parameter == credential.AccessToken && request.Headers.Referrer?.AbsoluteUri == "https://chatgpt.com/", "Only the intended official GET carries bearer authentication and referer.");
            Require(request.Headers.GetValues("x-openai-target-path").Single() == request.RequestUri.AbsolutePath && request.Headers.GetValues("x-openai-target-route").Single() == request.RequestUri.AbsolutePath, "Routing headers match their fixed endpoint.");
            if (calls.Count == 1) {
                Require(request.RequestUri.Query.Contains("timezone_offset_min="), "Account check includes the current timezone offset.");
                return Response(200, Accounts());
            }
            Require(request.RequestUri.Query == "?account_id=workspace", "Subscription fallback uses the selected workspace ID.");
            return Response(200, "{\"active_until\":\"2026-12-01T08:26:00Z\",\"subscription_plan\":\"plus\"}");
        }), () => Now)) {
            var lookup = await reader.ReadAsync(credential, default);
            Require(lookup.ActiveUntil == DateTimeOffset.Parse("2026-12-01T08:26:00Z") && lookup.Source == "subscriptions", "Renewal replaces the old November 1 entitlement with the actual December 1 billing date.");
            Require(calls.SequenceEqual(new[] { "/backend-api/accounts/check/v4-2023-04-27", "/backend-api/subscriptions" }), "Expired account metadata causes exactly the expected fallback GET.");
            Require(credential.Bytes.SequenceEqual(original), "Subscription queries never alter credential bytes or login identity.");
        }
        var futureCalls = 0;
        using (var reader = new OfficialSubscriptionReader(new Handler(_ => { futureCalls++; return Response(200, Accounts("2026-12-01T08:26:00Z")); }), () => Now)) {
            var lookup = await reader.ReadAsync(credential, default);
            Require(futureCalls == 1 && lookup.Source == "accounts_check", "A valid future entitlement needs no second endpoint.");
        }
        foreach (var firstExpiry in new[] { "", "invalid-date", "2026-11-01T08:26:00Z" }) {
            var count = 0;
            using var reader = new OfficialSubscriptionReader(new Handler(_ => Response(200, ++count == 1 ? Accounts(firstExpiry) : "{}")), () => Now);
            var lookup = await reader.ReadAsync(credential, default);
            Require(count == 2 && lookup.ActiveUntil == null, "An empty fallback cannot certify a missing/invalid/expired older date.");
        }
        using (var reader = new OfficialSubscriptionReader(new Handler(request => Response(200, request.RequestUri!.AbsolutePath.Contains("accounts/check") ? Accounts() : "{\"active_until\":\"2026-11-02T08:26:00Z\"}")), () => Now)) {
            var lookup = await reader.ReadAsync(credential, default);
            Require(lookup.ActiveUntil == DateTimeOffset.Parse("2026-11-02T08:26:00Z"), "An explicit fresh subscription response may truthfully confirm an already-expired period.");
        }
        foreach (var status in new[] { 401, 403, 302, 500 }) {
            using var reader = new OfficialSubscriptionReader(new Handler(_ => Response(status, "SECRET_TOKEN")), () => Now);
            try { await reader.ReadAsync(credential, default); throw new Exception("HTTP failure accepted."); }
            catch (SubscriptionReadException ex) { Require(!ex.Message.Contains("SECRET_TOKEN") && ex.Message.Contains(status.ToString()), "HTTP failures remain unverified and never print response bodies."); }
        }
        using (var reader = new OfficialSubscriptionReader(new Handler(request => request.RequestUri!.AbsolutePath.Contains("accounts/check") ? Response(200, Accounts()) : Response(500, "SECRET_TOKEN")), () => Now)) {
            try { await reader.ReadAsync(credential, default); throw new Exception("Failed fallback accepted."); }
            catch (SubscriptionReadException) { Require(true, "A failed fallback cannot return the expired check date as a verified subscription."); }
        }
    }
    private static async Task GuardedChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "switcher-subscription-check-" + Guid.NewGuid());
        var id = Guid.NewGuid(); var credential = Credential(null, null);
        var profile = Path.Combine(root, "data", "accounts", id.ToString().ToUpperInvariant()); Directory.CreateDirectory(profile);
        File.WriteAllBytes(Path.Combine(profile, "auth.json"), credential.Bytes);
        var old = new SubscriptionSnapshot(DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(-20));
        var state = AccountSnapshot.Empty with { Accounts = [new(new(id, "Fixture", "fixture@example.test"), "F", null, null, Subscription: old)], ActiveAccountID = id };
        var runtime = new Runtime(state);
        var pending = new TaskCompletionSource<SubscriptionLookup>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = new Reader((_, ct) => pending.Task.WaitAsync(ct));
        var freshUntil = DateTimeOffset.UtcNow.AddDays(26);
        try {
            await using var client = new GuardedAccountClient(Path.Combine(root, "data"), Path.Combine(root, "active"), () => runtime,
                new Credits(), (_, _) => Task.FromResult(new Usage(81, Now.AddDays(4), null, null, 2, null, "prolite")), new Validator(), subscription);
            await client.InitializeAsync();
            Require(client.State.Accounts[0].Subscription == null && client.State.Accounts[0].SubscriptionStatus == "checking", "An expired JWT snapshot is hidden while the official lookup is pending.");
            pending.SetResult(new("workspace", "pro", freshUntil, "subscriptions"));
            await Until(() => client.State.Accounts[0].SubscriptionStatus == "verified");
            var current = client.State.Accounts[0]; var checkedAt = current.Subscription!.LastChecked;
            Require(current.Subscription.ActiveUntil == freshUntil && current.Subscription.IsVerified && current.Subscription.Source == "subscriptions" && current.PlanType == "prolite", "Live expiry is verified while generic Pro keeps the official Pro 100 usage tier.");
            runtime.Publish(state);
            Require(client.State.Accounts[0].Subscription?.ActiveUntil == freshUntil, "Old Swift JWT snapshots cannot replace the live subscription date.");
            subscription.Response = (_, _) => throw new IOException("SECRET_TOKEN failed");
            await client.CommandAsync("refresh", id);
            current = client.State.Accounts[0];
            Require(current.SubscriptionStatus == "stale" && current.Subscription?.ActiveUntil == freshUntil && current.Subscription.LastChecked == checkedAt,
                "A failed lookup may retain a still-valid prior official date without claiming a new verification.");
            Require(current.SubscriptionError?.Contains("SECRET_TOKEN") == false && current.PlanType == "prolite", "Lookup failures leak no secrets and do not infer free from stale metadata.");
            subscription.Response = (_, _) => Task.FromResult(new SubscriptionLookup("workspace", "pro", null, "subscriptions"));
            await client.CommandAsync("refresh", id);
            Require(client.State.Accounts[0].SubscriptionStatus == "stale" && client.State.Accounts[0].Subscription?.LastChecked == checkedAt, "An empty response does not refresh the last successful verification timestamp.");
            var actualPast = DateTimeOffset.UtcNow.AddDays(-1);
            subscription.Response = (_, _) => Task.FromResult(new SubscriptionLookup("workspace", "pro", actualPast, "subscriptions"));
            await client.CommandAsync("refresh", id);
            Require(client.State.Accounts[0].SubscriptionStatus == "verified" && client.State.Accounts[0].Subscription?.ActiveUntil == actualPast && client.State.Accounts[0].PlanType == "free", "A fresh real expired subscription can display expired and effective free.");
            subscription.Response = (_, _) => throw new IOException("offline");
            await client.CommandAsync("refresh", id);
            Require(client.State.Accounts[0].Subscription == null && client.State.Accounts[0].SubscriptionStatus == "unavailable" && client.State.Accounts[0].PlanType == "prolite", "A failed recheck cannot keep an old expired date as confirmed or downgrade from it.");
            Require(File.ReadAllBytes(Path.Combine(profile, "auth.json")).SequenceEqual(credential.Bytes), "Subscription reconciliation never rewrites account authentication.");
            var lateId = Guid.NewGuid();
            var lateProfile = Path.Combine(root, "data", "accounts", lateId.ToString().ToUpperInvariant()); Directory.CreateDirectory(lateProfile);
            File.WriteAllBytes(Path.Combine(lateProfile, "auth.json"), credential.Bytes);
            var lateState = state with { Accounts = [state.Accounts[0] with { Profile = state.Accounts[0].Profile with { Id = lateId } }], ActiveAccountID = lateId };
            var oldLookup = new TaskCompletionSource<SubscriptionLookup>(TaskCreationOptions.RunContinuationsAsynchronously); var lookupCalls = 0;
            var lateReader = new Reader((_, ct) => ++lookupCalls == 1 ? oldLookup.Task.WaitAsync(ct) : Task.FromResult(new SubscriptionLookup("workspace", "pro", freshUntil, "subscriptions")));
            await using var lateClient = new GuardedAccountClient(Path.Combine(root, "data"), Path.Combine(root, "active"), () => new Runtime(lateState),
                new Credits(), (_, _) => Task.FromResult(new Usage(81, Now.AddDays(4), null, null, 2, null, "prolite")), new Validator(), lateReader);
            var sawOldExpired = false;
            lateClient.Changed += () => { if (lateClient.State.Accounts.FirstOrDefault()?.Subscription?.ActiveUntil < DateTimeOffset.UtcNow) sawOldExpired = true; };
            await lateClient.InitializeAsync(); await Until(() => lookupCalls == 1);
            await lateClient.CommandAsync("refresh", lateId);
            Require(lateClient.State.Accounts[0].Subscription?.ActiveUntil == freshUntil, "Explicit refresh supersedes an older pending billing lookup.");
            oldLookup.SetResult(new("workspace", "pro", actualPast, "subscriptions")); await Task.Delay(30);
            Require(!sawOldExpired && lateClient.State.Accounts[0].Subscription?.ActiveUntil == freshUntil && lateClient.State.Accounts[0].PlanType == "prolite",
                "An old in-flight billing result cannot overwrite renewed expiry or incorrectly turn the badge free.");
        } finally { SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root); }
    }
    private static async Task LoginPeriodChecks()
    {
        foreach (var (raw, local) in new[] {
            ("2026-12-06T07:55:21+00:00", "2026-12-06T15:55:21+08:00"),
            ("2026-11-22T10:11:13Z", "2026-11-22T18:11:13+08:00"),
            ("2026-12-05T04:02:14Z", "2026-12-05T12:02:14+08:00") }) {
            var credential = Credential(null, null, raw);
            using var reader = new OfficialSubscriptionReader(new Handler(_ => throw new Exception("A future login period must not be replaced by a different entitlement date.")), () => Now);
            var result = await reader.ReadAsync(credential, default);
            Require(result.ActiveUntil == DateTimeOffset.Parse(local) && result.ActiveUntil?.ToOffset(TimeSpan.FromHours(8)).Hour == DateTimeOffset.Parse(local).Hour,
                "Explicit UTC login periods retain their exact local hour without a fixed timezone correction.");
            Require(result.Source == "login_snapshot" && !result.IsVerified && result.PlanType == "prolite", "A future login period records its actual source and is not labelled an online verification.");
        }
        var boundary = DateTimeOffset.Parse("2026-12-06T07:55:21Z");
        var clock = boundary.AddSeconds(-1); var calls = 0;
        using (var reader = new OfficialSubscriptionReader(new Handler(_ => { calls++; return Response(200, Accounts("2027-01-06T07:55:21Z")); }), () => clock)) {
            var credential = Credential(null, null, boundary.ToString("O"));
            Require((await reader.ReadAsync(credential, default)).Source == "login_snapshot" && calls == 0, "The current login period is usable until its precise end, regardless of token issue date.");
            clock = boundary;
            var renewed = await reader.ReadAsync(credential, default);
            Require(calls == 1 && renewed.IsVerified && renewed.Source == "accounts_check" && renewed.ActiveUntil == DateTimeOffset.Parse("2027-01-06T07:55:21Z"),
                "At the exact end of the login period, a new online query can discover a renewed subscription.");
        }
        foreach (var until in new string?[] { null, "", "invalid", "2026-12-06T07:55:21", "2026-11-01T07:55:21Z" }) {
            var count = 0;
            using var reader = new OfficialSubscriptionReader(new Handler(_ => Response(200, ++count == 1 ? Accounts() : "{\"active_until\":\"2026-12-06T07:55:21Z\"}")), () => Now);
            var result = await reader.ReadAsync(Credential(null, null, until), default);
            Require(count == 2 && result.Source == "subscriptions" && result.IsVerified && result.ActiveUntil == boundary,
                "Missing, invalid, ambiguous or expired login periods use account check and subscription fallback, never an invented expiry.");
        }
        var login = new SubscriptionSnapshot(boundary, boundary.AddMinutes(-1), "login_snapshot", false);
        Require(!GuardedAccountClient.ShouldRefreshSubscription("login_snapshot", login, boundary.AddMinutes(-1), boundary.AddSeconds(-1), false), "An unexpired cached login period avoids redundant background work.");
        Require(GuardedAccountClient.ShouldRefreshSubscription("login_snapshot", login, boundary.AddMinutes(-1), boundary, false), "Expiry invalidates the login-period cache even before its 30-minute lifetime ends.");
        Require(GuardedAccountClient.ShouldRefreshSubscription("login_snapshot", login, boundary.AddMinutes(-1), boundary.AddSeconds(-1), true), "Manual refresh bypasses the cache while keeping the same source selection rule.");
        Require(GuardedAccountClient.ShouldRefreshSubscription("unavailable", null, Now.AddMinutes(-30), Now, false), "A missing-period result remains retryable after the cache lifetime.");
    }
    private static async Task GuardedLoginPeriodChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "switcher-login-period-check-" + Guid.NewGuid());
        var id = Guid.NewGuid(); var until = DateTimeOffset.UtcNow.AddDays(10); var credential = Credential(null, null, until.ToString("O"));
        var profile = Path.Combine(root, "data", "accounts", id.ToString().ToUpperInvariant()); Directory.CreateDirectory(profile);
        File.WriteAllBytes(Path.Combine(profile, "auth.json"), credential.Bytes);
        var state = AccountSnapshot.Empty with { Accounts = [new(new(id, "Fixture", "fixture@example.test"), "F", null, null)], ActiveAccountID = id };
        var reads = 0; var httpCalls = 0;
        using var official = new OfficialSubscriptionReader(new Handler(_ => { httpCalls++; return Response(200, Accounts("2099-01-01T00:00:00Z")); }));
        var reader = new Reader((login, ct) => { reads++; return official.ReadAsync(login, ct); });
        try {
            await using var client = new GuardedAccountClient(Path.Combine(root, "data"), Path.Combine(root, "active"), () => new Runtime(state),
                new Credits(), (_, _) => Task.FromResult(new Usage(81, Now.AddDays(4), null, null, 2, null, "prolite")), new Validator(), reader);
            await client.InitializeAsync(); await Until(() => client.State.Accounts[0].SubscriptionStatus == "login_snapshot");
            var first = client.State.Accounts[0];
            Require(first.Subscription is { Source: "login_snapshot", IsVerified: false } && first.Subscription.ActiveUntil == until && first.PlanType == "prolite",
                "The account state preserves the exact login period and its non-online source without changing the plan.");
            await client.CommandAsync("refresh", id);
            Require(reads == 2 && httpCalls == 0 && client.State.Accounts[0].Subscription?.ActiveUntil == until, "Manual refresh re-reads current credentials without replacing their future period with a different entitlement expiry.");
            var expired = Credential(null, null, DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
            File.WriteAllBytes(Path.Combine(profile, "auth.json"), expired.Bytes);
            await client.CommandAsync("refresh", id);
            Require(httpCalls == 1 && client.State.Accounts[0].Subscription is { IsVerified: true, Source: "accounts_check" }, "Credential changes discard the cached login period and an expired replacement performs online reconciliation.");
            Require(File.ReadAllBytes(Path.Combine(profile, "auth.json")).SequenceEqual(expired.Bytes), "Login-period selection never rewrites authentication data.");
        } finally { SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root); }
    }
    private static LoginCredential Credential(string? idOrg, string? accessOrg, string? subscriptionUntil = null)
    {
        string Token(string? org) {
            var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["email"] = "fixture@example.test", ["https://api.openai.com/auth"] = new { chatgpt_account_id = "workspace", organization_id = org, chatgpt_plan_type = "prolite", chatgpt_subscription_active_until = subscriptionUntil } });
            return "e30." + Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
        }
        return LoginCredential.Parse(JsonSerializer.SerializeToUtf8Bytes(new { tokens = new { account_id = "workspace", id_token = Token(idOrg), access_token = Token(accessOrg) } }));
    }
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
    private sealed class Reader(Func<LoginCredential, CancellationToken, Task<SubscriptionLookup>> response) : ISubscriptionReader {
        public Func<LoginCredential, CancellationToken, Task<SubscriptionLookup>> Response = response;
        public Task<SubscriptionLookup> ReadAsync(LoginCredential credential, CancellationToken ct) => Response(credential, ct);
    }
    private sealed class Runtime(AccountSnapshot state) : IAccountRuntime {
        public AccountSnapshot State { get; private set; } = state;
        public event Action? Changed;
        public void Publish(AccountSnapshot next) { State = next; Changed?.Invoke(); }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Credits : IResetCreditService {
        public Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct) => Task.FromResult(new ResetCreditSnapshot(2, Now.AddDays(3)));
        public Task ConsumeAsync(LoginCredential credential, CancellationToken ct) => throw new Exception("Subscription tests must not consume credits.");
    }
    private sealed class Validator : ILoginValidator { public Task ValidateAsync(LoginCredential credential, CancellationToken ct) => Task.CompletedTask; }
    private static async Task Until(Func<bool> condition) {
        for (var i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Expected subscription state was not reached.");
    }
}
