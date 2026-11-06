using System.Net;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class ResetCreditsChecks
{
    private static int passed;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); passed++; }

    public static async Task<int> RunAsync()
    {
        try {
            ParseChecks(); await HttpChecks(); await ClientChecks(); await RefreshChecks();
            Console.WriteLine($"PASS: {passed} reset-credit HTTP/parser/transaction assertions (synthetic credentials, no network or real consume)."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RefreshChecks()
    {
        using var slowNetwork = new ClientFixture("prolite");
        var pendingUsage = new TaskCompletionSource<Usage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var client = slowNetwork.Client((_, ct) => pendingUsage.Task.WaitAsync(ct))) {
            var localPlanPublished = false;
            client.Changed += () => { if (client.State.Accounts.FirstOrDefault()?.PlanType == "prolite") localPlanPublished = true; };
            await client.InitializeAsync(); await Until(() => localPlanPublished);
            Require(client.State.Accounts[0].PlanType == "prolite" && slowNetwork.Credits.Reads == 0 && !pendingUsage.Task.IsCompleted,
                "JWT plan is published while the first HTTP request is still pending, ahead of stale cached host plan.");
            pendingUsage.SetResult(slowNetwork.Usage(72) with { PlanType = "future-official-plan" });
            await Until(() => client.State.Accounts[0].PlanType == "future-official-plan");
            Require(client.State.Accounts[0].PlanType == "future-official-plan", "New official metadata supersedes the immediate JWT hint.");
        }

        using var sync = new ClientFixture();
        var newExpiry = Now.AddDays(80);
        sync.RefreshAction = runtime => runtime.Publish(runtime.State with {
            ActiveAccountID = null, ActiveIdentityConfirmed = false,
            Accounts = runtime.State.Accounts.Select(row => row with { Subscription = new(newExpiry, Now) }).ToArray()
        });
        await using (var client = sync.Client()) {
            await client.InitializeAsync(); await client.CommandAsync("refresh", sync.Id);
            Require(sync.Refreshes == 1 && client.State.ActiveAccountID == null && !client.State.ActiveIdentityConfirmed,
                "Manual refresh still invokes shared-core identity synchronization.");
            Require(sync.LastRuntime!.State.Accounts[0].Subscription?.ActiveUntil == newExpiry && client.State.Accounts[0].Usage?.RemainingPercent == 42,
                "Shared-core subscription refresh is preserved alongside direct quota reads.");
            Require(client.State.Accounts[0].SubscriptionStatus == "unavailable" && client.State.Accounts[0].Subscription == null,
                "An empty official subscription result does not promote the core's JWT expiry to verified billing data.");
        }

        using var changed = new ClientFixture("plus");
        var oldRequest = new TaskCompletionSource<Usage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredOldRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayOldCredential = false;
        await using (var client = changed.Client((credential, ct) => {
            if (delayOldCredential && credential.AccessToken == "synthetic-reset-transaction") { enteredOldRequest.TrySetResult(); return oldRequest.Task.WaitAsync(ct); }
            return Task.FromResult(changed.Usage(credential.AccessToken == "replacement-token" ? 89 : 42) with { PlanType = credential.AccessToken == "replacement-token" ? "new-official-plan" : "plus" });
        })) {
            await client.InitializeAsync(); await Until(() => client.State.Accounts[0].Usage?.RemainingPercent == 42);
            var sawObsoleteResult = false;
            client.Changed += () => { if (client.State.Accounts[0].Usage?.RemainingPercent == 4) sawObsoleteResult = true; };
            delayOldCredential = true;
            var refresh = client.CommandAsync("refresh", changed.Id); await enteredOldRequest.Task;
            changed.ReplaceSavedCredential("replacement-token", "promax");
            changed.LastRuntime!.Publish(changed.LastRuntime.State with { Accounts = changed.LastRuntime.State.Accounts.Select(row => row with { Subscription = new(newExpiry, Now) }).ToArray() });
            oldRequest.SetResult(changed.Usage(4) with { PlanType = "obsolete-plan" }); await refresh;
            await Until(() => client.State.Accounts[0].Usage?.RemainingPercent == 89);
            Require(!sawObsoleteResult && client.State.Accounts[0].PlanType == "new-official-plan",
                "A credential change reported during core refresh invalidates the pending old-token result and rereads current credentials.");
            Require(changed.LastRuntime!.State.Accounts[0].Subscription?.ActiveUntil == newExpiry, "Credential reconciliation does not discard shared-core subscription metadata.");
        }
    }

    private static void ParseChecks()
    {
        var payload = """{"data":{"available_count":2,"credits":[{"id":"later","status":"available","expires_at":"2026-12-01T00:00:00Z"},{"id":"early","state":"available","expires_at":"2026-11-07T00:00:00Z"},{"id":"old","expires_at":"2020-01-01T00:00:00Z"},{"id":"used","status":"redeemed","expires_at":"2026-11-06T13:00:00Z"}]}}""";
        var parsed = OfficialResetCredits.Parse(payload, Now);
        Require(parsed.AvailableCount == 2 && parsed.EarliestExpiry == DateTimeOffset.Parse("2026-11-07T00:00:00Z"), "Nested count and earliest available expiry.");
        Require(OfficialResetCredits.Parse("{}", Now).AvailableCount == null, "Missing detail is unknown, never zero.");
        Require(OfficialResetCredits.Parse("{\"credits\":[]}", Now).AvailableCount == 0, "An explicitly empty list is known zero.");
        Require(OfficialResetCredits.Parse("{\"credits\":[{},null]}", Now).AvailableCount == null, "Unrecognizable records do not create zero or available credits.");
        Require(OfficialResetCredits.Parse("{\"credits\":[{\"status\":\"future-status\"}]}", Now).AvailableCount == null, "Unknown statuses cannot authorize consumption.");
        Require(OfficialResetCredits.Parse("{\"available_count\":-1}", Now).AvailableCount == null, "Negative credit counts remain unknown.");
        Require(OfficialResetCredits.Parse("{\"availableCount\":\"3\"}", Now).AvailableCount == 3, "Numeric string alias is supported.");
        var derived = OfficialResetCredits.Parse("""{"data":[{"id":"future","expires_at":4070908800000},{"id":"used","redeemed_at":"2026-01-01T00:00:00Z"},{"id":"old","expires_at":1000}]}""", Now);
        Require(derived.AvailableCount == 1 && derived.EarliestExpiry?.Year == 2099, "Derived counts exclude redeemed/expired records and support milliseconds.");
        var zero = OfficialResetCredits.Parse("""{"available_count":0,"credits":[{"id":"stale","expires_at":"2099-01-01T00:00:00Z"}]}""", Now);
        Require(zero.AvailableCount == 0 && zero.EarliestExpiry == null, "Known zero has no available expiry despite stale records.");
        try { OfficialResetCredits.Parse("SECRET_TOKEN invalid", Now); throw new Exception("Invalid payload accepted."); }
        catch (ResetCreditException ex) { Require(!ex.Message.Contains("SECRET_TOKEN"), "Parser errors do not expose response content."); }
    }

    private static async Task HttpChecks()
    {
        var credential = GuardedChecks.Credential("reset-fixture");
        var requests = new List<string>(); var redeemIds = new List<Guid>();
        using (var service = new OfficialResetCredits(new Handler(async (request, _) => {
            Require(request.RequestUri?.Scheme == "https" && request.RequestUri.Host == "chatgpt.com", "Only the fixed official HTTPS origin receives credentials.");
            Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == credential.AccessToken, "Bearer credential stays in request header.");
            Require(request.Headers.GetValues("ChatGPT-Account-Id").Single() == credential.AccountID, "Workspace header is preserved.");
            requests.Add(request.Method.Method);
            if (request.Method == HttpMethod.Get) return Response(200, "{\"available_count\":2}");
            Require(request.RequestUri!.AbsolutePath == "/backend-api/wham/rate-limit-reset-credits/consume", "Consume uses the dedicated fixed path.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Require(body.RootElement.EnumerateObject().Count() == 1, "Consume has only its idempotency key.");
            redeemIds.Add(Guid.Parse(body.RootElement.GetProperty("redeem_request_id").GetString()!));
            return Response(200, "{}");
        }), () => Now)) {
            await service.ConsumeAsync(credential, default); await service.ConsumeAsync(credential, default);
            Require(requests.SequenceEqual(new[] { "GET", "POST", "GET", "POST" }) && redeemIds.Distinct().Count() == 2, "Each explicit reset checks the balance then posts exactly once with a unique UUID.");
        }
        foreach (var json in new[] { "{}", "{\"available_count\":0}", "{\"available_count\":-3}" }) {
            var posts = 0;
            using var service = new OfficialResetCredits(new Handler((r, _) => { if (r.Method == HttpMethod.Post) posts++; return Task.FromResult(Response(200, json)); }));
            var failure = await Failure(() => service.ConsumeAsync(credential, default));
            Require(posts == 0 && !failure.OutcomeUncertain, "Unknown/zero/invalid counts cannot cause a POST.");
        }
        foreach (var status in new[] { 401, 403, 302, 429, 500 }) {
            var calls = 0;
            using var service = new OfficialResetCredits(new Handler((_, _) => { calls++; return Task.FromResult(Response(status, "SECRET_TOKEN")); }));
            var failure = await Failure(() => service.ConsumeAsync(credential, default));
            Require(calls == 1 && !failure.Message.Contains("SECRET_TOKEN"), "Preflight failures never consume or leak server content.");
        }
        foreach (var status in new[] { 401, 403, 302, 409, 429, 500, 504 }) {
            var posts = 0;
            using var service = new OfficialResetCredits(new Handler((r, _) => {
                if (r.Method == HttpMethod.Get) return Task.FromResult(Response(200, "{\"available_count\":1}"));
                posts++; return Task.FromResult(Response(status, "SECRET_TOKEN"));
            }));
            var failure = await Failure(() => service.ConsumeAsync(credential, default));
            Require(posts == 1 && !failure.Message.Contains("SECRET_TOKEN") && failure.OutcomeUncertain == (status >= 500), "Failed POST is never retried and ambiguous 5xx is marked uncertain.");
        }
        foreach (var timeout in new[] { true, false }) {
            var posts = 0;
            using var service = new OfficialResetCredits(new Handler((r, _) => {
                if (r.Method == HttpMethod.Get) return Task.FromResult(Response(200, "{\"available_count\":1}"));
                posts++; throw timeout ? new TaskCanceledException("SECRET_TOKEN") : new HttpRequestException("SECRET_TOKEN");
            }));
            var failure = await Failure(() => service.ConsumeAsync(credential, default));
            Require(posts == 1 && failure.OutcomeUncertain && !failure.Message.Contains("SECRET_TOKEN"), "Timeout/connection loss cannot automatically repeat a POST.");
        }
    }

    private static async Task ClientChecks()
    {
        using var fixture = new ClientFixture();
        var credits = fixture.Credits;
        await using (var client = fixture.Client()) {
            await client.InitializeAsync();
            await Until(() => client.State.Accounts[0].Usage?.ResetCreditsRemaining == 3);
            Require(client.State.Accounts[0].Usage?.RemainingPercent == 42 && client.State.Accounts[0].PlanType == "future-plan", "Fresh official quota/plan override stale Swift snapshots.");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); credits.Gate = gate;
            var reset = client.CommandAsync("resetQuota", fixture.Id);
            await Until(() => credits.Consumes == 1);
            Require(client.State.IsMutating && client.State.Accounts.Length == 1 && fixture.Running == 0, "Reset pauses the host, preserves visible accounts and holds busy state.");
            await client.CommandAsync("resetQuota", fixture.Id);
            Require(credits.Consumes == 1, "Overlapping confirmation cannot issue a second reset.");
            gate.SetResult(); await reset;
            Require(fixture.Starts == 2 && fixture.Stops == 1 && fixture.Running == 1, "The host restarts after reset.");
            Require(client.State.Accounts[0].Usage?.ResetCreditsRemaining == 2 && client.State.Accounts[0].Usage?.RemainingPercent == 100, "Success refreshes both balance and quota.");
            fixture.LastRuntime!.Publish(fixture.Snapshot);
            Require(client.State.Accounts[0].Usage?.ResetCreditsRemaining == 2, "An older host snapshot cannot replace the fresh balance.");
            Require(File.ReadAllBytes(fixture.ActiveCredential).SequenceEqual(fixture.OriginalBytes), "Reset never changes the active login file.");

            credits.Gate = null; credits.Uncertain = true;
            await client.CommandAsync("resetQuota", fixture.Id);
            Require(client.State.Accounts[0].Usage?.ResetCreditsRemaining == null && client.State.Error?.Contains("未确认") == true, "An uncertain POST hides the old spendable balance.");
            var previous = credits.Consumes;
            await client.CommandAsync("resetQuota", fixture.Id);
            Require(credits.Consumes == previous, "An uncertain reset requires explicit refresh before another consume.");
            credits.Uncertain = false;
            await client.CommandAsync("refresh", fixture.Id);
            Require(client.State.Accounts[0].Usage?.ResetCreditsRemaining == 2, "Explicit fresh balance resolves the uncertain UI state.");

            credits.FailReadsAfterConsume = true;
            await client.CommandAsync("resetQuota", fixture.Id);
            Require(client.State.Error?.Contains("已成功") == true && client.State.Accounts[0].Usage?.ResetCreditsRemaining == null, "Successful POST plus failed detail refresh is reported as success with stale credits disabled.");
            previous = credits.Consumes; await client.CommandAsync("resetQuota", fixture.Id);
            Require(credits.Consumes == previous, "Refresh failure cannot leave an old usable count for another consume.");
            credits.FailReadsAfterConsume = false;
            await client.CommandAsync("refresh", fixture.Id);
            Require(client.State.Accounts[0].Usage?.ResetCreditsRemaining == 1, "A later explicit read restores the actual remaining count.");
        }

        using var stale = new ClientFixture();
        var delayed = new TaskCompletionSource<Usage>(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        await using (var client = stale.Client((_, _) => ++reads == 1 ? delayed.Task : Task.FromResult(stale.Usage(100)))) {
            await client.InitializeAsync(); await Until(() => reads == 1);
            await client.CommandAsync("resetQuota", stale.Id);
            delayed.SetResult(stale.Usage(4)); await Until(() => stale.Credits.Reads >= 2);
            await Task.Delay(30);
            Require(client.State.Accounts[0].Usage?.RemainingPercent == 100 && client.State.Accounts[0].Usage?.ResetCreditsRemaining == 2, "An older in-flight direct request cannot overwrite reset results.");
        }
        using var failedRestart = new ClientFixture { FailSecondStart = true };
        await using (var client = failedRestart.Client()) {
            await client.InitializeAsync(); await client.CommandAsync("resetQuota", failedRestart.Id);
            Require(!client.State.IsMutating && client.State.Error?.Contains("已成功") == true && !client.State.Error.Contains("SECRET_TOKEN"), "Restart failure preserves the confirmed outcome without leaking implementation errors.");
        }
        using var failedStop = new ClientFixture { FailStopOnce = true };
        await using (var client = failedStop.Client()) {
            await client.InitializeAsync(); await client.CommandAsync("resetQuota", failedStop.Id);
            Require(failedStop.Credits.Consumes == 0 && failedStop.Starts == 1 && client.State.Error?.Contains("未执行重置") == true,
                "A host that cannot be stopped prevents consume and cannot be duplicated by a new host.");
            Require(!client.State.Error!.Contains("SECRET_TOKEN"), "Host-stop exceptions cannot leak raw details.");
        }
    }

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
    private static async Task<ResetCreditException> Failure(Func<Task> action) {
        try { await action(); } catch (ResetCreditException ex) { return ex; }
        throw new Exception("Expected a safe reset failure.");
    }
    private static async Task Until(Func<bool> condition) {
        for (var attempt = 0; attempt < 100; attempt++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Fixture state was not reached.");
    }
    private sealed class ClientFixture : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "switcher-reset-check-" + Guid.NewGuid());
        public Guid Id { get; } = Guid.NewGuid();
        public int Running, Starts, Stops, Refreshes;
        public bool FailSecondStart, FailStopOnce;
        public Action<FakeRuntime>? RefreshAction;
        public FakeRuntime? LastRuntime;
        public FakeCredits Credits { get; }
        public byte[] OriginalBytes { get; }
        public string ActiveCredential => Path.Combine(root, "active", "auth.json");
        private string SavedCredential => Path.Combine(root, "data", "accounts", Id.ToString().ToUpperInvariant(), "auth.json");
        public AccountSnapshot Snapshot { get; }
        public ClientFixture(string? plan = null) {
            var credential = Credential("synthetic-reset-transaction", plan); OriginalBytes = credential.Bytes;
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveCredential)!); File.WriteAllBytes(ActiveCredential, OriginalBytes);
            var saved = Path.Combine(root, "data", "accounts", Id.ToString().ToUpperInvariant()); Directory.CreateDirectory(saved); File.WriteAllBytes(Path.Combine(saved, "auth.json"), OriginalBytes);
            Snapshot = AccountSnapshot.Empty with { Accounts = [new(new(Id, "Fixture", "fixture@example.test"), "F", new(1, Now.AddDays(3), 2, Now.AddHours(2), 9, null, "plus"), null)], ActiveAccountID = Id };
            Credits = new FakeCredits(this);
        }
        private static LoginCredential Credential(string token, string? plan) {
            var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> {
                ["email"] = "fixture@example.test", ["https://api.openai.com/auth"] = new { chatgpt_account_id = "reset-transaction", chatgpt_plan_type = plan }
            });
            var payload = Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return LoginCredential.Parse(JsonSerializer.SerializeToUtf8Bytes(new { auth_mode = "chatgpt", tokens = new {
                account_id = "reset-transaction", id_token = "e30." + payload + ".fixture", access_token = token, refresh_token = "synthetic-refresh"
            }}));
        }
        public void ReplaceSavedCredential(string token, string? plan) => File.WriteAllBytes(SavedCredential, Credential(token, plan).Bytes);
        public Usage Usage(int remaining) => new(remaining, Now.AddDays(4), null, null, 9, null, "future-plan");
        public GuardedAccountClient Client(Func<LoginCredential, CancellationToken, Task<Usage>>? reader = null) => new(Path.Combine(root, "data"), Path.Combine(root, "active"), () => {
            Starts++; if (FailSecondStart && Starts > 1) throw new IOException("SECRET_TOKEN restart");
            return LastRuntime = new FakeRuntime(this, Snapshot);
        }, Credits, reader ?? ((_, _) => Task.FromResult(Usage(Credits.Consumes > 0 ? 100 : 42))), new Validator());
        public void Dispose() => SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
    }
    private sealed class FakeCredits(ClientFixture fixture) : IResetCreditService {
        public int Reads, Consumes, Available = 3;
        public bool Uncertain, FailReadsAfterConsume;
        public TaskCompletionSource? Gate;
        public Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct) {
            Reads++;
            if (FailReadsAfterConsume && Consumes > 0) throw new IOException("SECRET_TOKEN details");
            return Task.FromResult(new ResetCreditSnapshot(Available, Now.AddDays(2)));
        }
        public async Task ConsumeAsync(LoginCredential credential, CancellationToken ct) {
            Require(fixture.Running == 0, "No Swift auto-reset process runs during a consume.");
            Consumes++;
            if (Gate != null) await Gate.Task.WaitAsync(ct);
            if (Uncertain) throw new ResetCreditException("重置结果尚未确认，请刷新核对。", true);
            Available--;
        }
    }
    private sealed class FakeRuntime(ClientFixture fixture, AccountSnapshot state) : IAccountRuntime {
        private bool running;
        public AccountSnapshot State { get; private set; } = state;
        public event Action? Changed;
        public Task InitializeAsync() { running = true; fixture.Running++; return Task.CompletedTask; }
        public void Publish(AccountSnapshot snapshot) { State = snapshot; Changed?.Invoke(); }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            if (command == "refresh") { fixture.Refreshes++; fixture.RefreshAction?.Invoke(this); }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() {
            if (fixture.FailStopOnce) { fixture.FailStopOnce = false; throw new IOException("SECRET_TOKEN stop"); }
            if (running) { running = false; fixture.Running--; fixture.Stops++; } return ValueTask.CompletedTask;
        }
    }
    private sealed class Validator : ILoginValidator { public Task ValidateAsync(LoginCredential credential, CancellationToken ct) => Task.CompletedTask; }
}
