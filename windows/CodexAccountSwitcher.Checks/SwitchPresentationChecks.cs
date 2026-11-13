using CodexAccountSwitcher.Core;

internal static class SwitchPresentationChecks
{
    public static async Task RunAsync()
    {
        foreach (var fail in new[] { false, true }) await VerifyAsync(fail);
        await RestartAsync();
        Console.WriteLine("PASS: switch presentation retains plan, verified expiry, quota and reset expiry during success/failure and delayed rereads; changed credentials invalidate only their own account.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > timeout) throw new TimeoutException("Switch fixture timed out."); await Task.Delay(10); }
    }
    private static async Task VerifyAsync(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "switcher-presentation-" + Guid.NewGuid());
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        string Saved(Guid id) => Path.Combine(root, "data", "accounts", id.ToString().ToUpperInvariant(), "auth.json");
        foreach (var id in ids) { Directory.CreateDirectory(Path.GetDirectoryName(Saved(id))!); File.WriteAllBytes(Saved(id), GuardedChecks.Credential(id.ToString()).Bytes); }
        var now = DateTimeOffset.UtcNow;
        var runtime = new Runtime(AccountSnapshot.Empty with { ActiveAccountID = ids[0], Settings = new(QuotaRefreshIntervalMinutes: 0),
            Accounts = ids.Select(id => new AccountRow(new(id, "Fixture", "fixture@example.test"), "F", null, null)).ToArray() }, fail);
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdReads = false; var reads = 0;
        var credits = new Credits(now.AddDays(2));
        var subscription = new Subscription(now.AddDays(20));
        var client = new GuardedAccountClient(Path.Combine(root, "data"), Path.Combine(root, "active"), () => runtime,
            credits, async (_, ct) => {
                Interlocked.Increment(ref reads);
                if (holdReads) await readGate.Task.WaitAsync(ct);
                return new Usage(62, now.AddDays(4), 83, now.AddHours(2), PlanType: "prolite");
            }, new Validator(), subscription);
        try {
            await client.InitializeAsync();
            await Until(() => reads == 2 && client.State.Accounts.All(row => row.SubscriptionStatus == "verified" && row.Usage?.ResetCreditsExpireAt != null));
            // A foreground command also waits for the initial background batch to fully release its gate.
            await client.CommandAsync("dismissError");
            var before = client.State.Accounts;
            holdReads = true;
            var switching = client.CommandAsync("switch", ids[1]);
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(client.State.IsMutating, "Switch must expose busy state.");
            Check(client.State.Accounts.SequenceEqual(before), "Pending switch must retain all displayed account fields.");
            runtime.Done.SetResult();
            try { await switching; Check(!fail, "Failure fixture unexpectedly succeeded."); }
            catch (IOException) when (fail) { }
            await Until(() => reads >= 4);
            Check(client.State.Accounts.SequenceEqual(before), "Delayed post-switch reads must retain plan, verified date and reset expiry for every account.");
            Check(client.State.ActiveAccountID == ids[fail ? 0 : 1], "Only successful switches change the current marker.");
            readGate.SetResult();
            await client.CommandAsync("dismissError");
            Check(client.State.Accounts.All(row => row.PlanType == "prolite" && row.SubscriptionStatus == "verified"), "Fresh results must retain populated metadata.");
            // Replaced credentials must still reject old data instead of making the new display cache authoritative.
            var other = client.State.Accounts[1];
            File.WriteAllBytes(Saved(ids[0]), GuardedChecks.Credential("replacement-identity").Bytes);
            holdReads = true;
            readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            subscription.Gate = readGate.Task;
            runtime.Publish();
            Check(client.State.Accounts[0].PlanType == null, "A replaced credential identity invalidates its cached plan.");
            Check(client.State.Accounts[1] == other, "A changed credential must not invalidate the unrelated account.");
            readGate.SetResult();
            await client.CommandAsync("dismissError");
        } finally {
            runtime.Done.TrySetResult(); readGate.TrySetResult();
            await client.DisposeAsync();
            SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }
    private sealed class Runtime(AccountSnapshot state, bool fail) : IAccountRuntime
    {
        public AccountSnapshot State { get; private set; } = state;
        public event Action? Changed;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Publish() => Changed?.Invoke();
        public Task InitializeAsync() => Task.CompletedTask;
        public async Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null)
        {
            if (command != "switch") return;
            Started.SetResult(); await Done.Task;
            if (fail) throw new IOException("Synthetic preflight failure.");
            State = State with { ActiveAccountID = accountID }; Publish();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Credits(DateTimeOffset until) : IResetCreditService
    {
        public Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct) => Task.FromResult(new ResetCreditSnapshot(2, until));
        public Task ConsumeAsync(LoginCredential credential, CancellationToken ct) => throw new Exception("No real resets in fixture.");
    }
    private sealed class Subscription(DateTimeOffset until) : ISubscriptionReader
    {
        public Task? Gate;
        public async Task<SubscriptionLookup> ReadAsync(LoginCredential credential, CancellationToken ct)
        {
            if (Gate != null) await Gate.WaitAsync(ct);
            return new SubscriptionLookup(credential.AccountID, "pro", until, "subscriptions");
        }
    }
    private static async Task RestartAsync() {
        var root = Path.Combine(Path.GetTempPath(), "switcher-restart-" + Guid.NewGuid());
        var id = Guid.NewGuid(); var data = Path.Combine(root, "data"); var active = Path.Combine(root, "active");
        var saved = Path.Combine(data, "accounts", id.ToString().ToUpperInvariant(), "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(saved)!); Directory.CreateDirectory(active);
        var bytes = GuardedChecks.Credential(id.ToString()).Bytes;
        File.WriteAllBytes(saved, bytes); File.WriteAllBytes(Path.Combine(active, "auth.json"), bytes);
        var runtime = new Runtime(AccountSnapshot.Empty with { ActiveAccountID = id, Accounts = [new(new(id, "Synthetic", "restart@example.test"), "R", null, null)] }, false);
        var desktop = new RestartDesktop();
        try {
            await using var client = new GuardedAccountClient(data, active, () => runtime, new Credits(DateTimeOffset.UtcNow.AddDays(1)),
                (_, _) => Task.FromResult(new Usage(70, DateTimeOffset.UtcNow.AddDays(2), null, null)), new Validator(), desktop: desktop);
            await client.InitializeAsync(); await Until(() => client.State.Accounts[0].Usage != null);
            var restart = client.CommandAsync("restart", id);
            await desktop.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(client.State.IsMutating && desktop.Events.SequenceEqual(new[] { "close" }), "Restart must close before opening and expose mutation state.");
            desktop.Continue.SetResult(); await restart;
            Check(desktop.Events.SequenceEqual(new[] { "close", "open" }) && !client.State.IsMutating, "Restart opens the desktop exactly once after close.");
            Check(client.State.ActiveAccountID == id && File.ReadAllBytes(saved).SequenceEqual(bytes) && File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(bytes), "Restart preserves both saved/live credentials and current account.");
            await client.CommandAsync("restart", Guid.NewGuid());
            Check(desktop.Events.Count == 2, "A stale/non-current restart target cannot restart another account.");
        } finally { SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root); }
        Console.WriteLine("PASS: current-account restart confirmation backend, close/open ordering, stale target rejection and unchanged credentials.");
    }
    private sealed class RestartDesktop : IDesktopOperations {
        public readonly List<string> Events = [];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task CloseAsync(CancellationToken ct) { Events.Add("close"); Started.SetResult(); await Continue.Task.WaitAsync(ct); }
        public Task OpenAsync(CancellationToken ct) { Events.Add("open"); return Task.CompletedTask; }
    }
    private sealed class Validator : ILoginValidator
    {
        public Task ValidateAsync(LoginCredential credential, CancellationToken ct) => Task.CompletedTask;
    }
}
