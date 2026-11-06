using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class QuotaRefreshChecks
{
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    public static async Task<int> RunAsync(string? host)
    {
        try {
            await ManagedAsync();
            await InitialResetCycleAsync();
            if (host != null) await HostAsync(Path.GetFullPath(host));
            Console.WriteLine($"PASS: {passed} quota refresh assertions; single revision-driven direct batch, configured periodic clock, disabled/manual behavior, persistence and account-operation coordination.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task InitialResetCycleAsync()
    {
        foreach (var enabledAtStart in new[] { true, false }) {
            using var fixture = new Fixture(enabledAtStart);
            await using var client = fixture.Client; await client.InitializeAsync(); await Idle(client);
            Check(fixture.TotalReads == 2 && client.State.Accounts.All(row => row.Usage?.ResetCreditsRemaining == 2), "Startup reads the initial credit balance even with ordinary polling disabled.");
            if (!enabledAtStart) {
                fixture.Runtime.Publish(fixture.Runtime.State with { Settings = fixture.Runtime.State.Settings with { AutoResetEnabled = true } });
                fixture.Runtime.Publish(fixture.Runtime.State with { Settings = fixture.Runtime.State.Settings with { AutoResetEnabled = false } });
            }
            Check(fixture.TotalReads == 2, "Observing an initial-cycle auto-reset setting does not itself duplicate the pending fast read.");
            fixture.ReadRemaining = 100; fixture.Credits.Available = 0;
            fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = 1 }); await Idle(client);
            Check(fixture.TotalReads == 4 && client.State.Accounts.All(row => row.Usage?.ResetCreditsRemaining == 0 && row.Usage.RemainingPercent == 100),
                "A possibly spending first host cycle forces one direct readback even when periodic polling is disabled or auto reset was toggled off meanwhile.");
            fixture.Runtime.Publish(fixture.Runtime.State); await Task.Delay(35);
            Check(fixture.TotalReads == 4, "The verified first-cycle readback is consumed once, without a completion snapshot retry loop.");
        }
        using (var fixture = new Fixture()) {
            fixture.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var client = fixture.Client; await client.InitializeAsync();
            await Until(() => fixture.ActiveReads == 2);
            fixture.Runtime.Publish(fixture.Runtime.State with { Settings = fixture.Runtime.State.Settings with { AutoResetEnabled = true } });
            fixture.ReadRemaining = 100; fixture.Credits.Available = 0;
            fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = 1 });
            Check(fixture.TotalReads == 2 && client.State.IsMutating, "A completed first host reset waits for the pending fast read without starting an overlapping batch.");
            fixture.ReadGate.SetResult(); await Idle(client); fixture.ReadGate = null;
            Check(fixture.TotalReads == 4 && client.State.Accounts.All(row => row.Usage?.RemainingPercent == 100 && row.Usage.ResetCreditsRemaining == 0),
                "A fast read started before auto reset was enabled cannot falsely consume the completed first-cycle revision when it returns late.");
            fixture.Runtime.Publish(fixture.Runtime.State); await Task.Delay(35);
            Check(fixture.TotalReads == 4, "The actual post-cycle revision-one readback is retained and does not loop back to revision zero.");
        }
    }
    private static async Task ManagedAsync()
    {
        using var fixture = new Fixture();
        await using var client = fixture.Client;
        await client.InitializeAsync(); await Idle(client);
        Check(fixture.TotalReads == 2, "Each account receives one initial direct read even when periodic refresh is disabled.");
        fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = 1 }); await Idle(client);
        Check(fixture.TotalReads == 2, "The first, possibly delayed host cycle is already covered by immediate startup reads.");
        AgeDirectCache(client);
        for (var i = 0; i < 8; i++) fixture.Runtime.Publish(fixture.Runtime.State);
        await Task.Delay(60);
        Check(fixture.TotalReads == 2, "An old five-minute cache timestamp cannot trigger hidden reads while periodic refresh is disabled.");
        fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = 2 }); await Idle(client);
        Check(fixture.TotalReads == 4, "One newly completed host cycle triggers exactly one direct read per account without waiting for the old cache age.");
        fixture.Runtime.Publish(fixture.Runtime.State); await Task.Delay(30);
        Check(fixture.TotalReads == 4, "Repeated snapshots of the same completed cycle do not duplicate requests.");

        fixture.Runtime.Publish(fixture.Runtime.State with { IsAddingAccount = true, UsageRefreshRevision = 3 }); await Task.Delay(30);
        Check(fixture.TotalReads == 4, "A newly due cycle is deferred during browser authorization.");
        fixture.Runtime.Publish(fixture.Runtime.State with { IsAddingAccount = false }); await Idle(client);
        Check(fixture.TotalReads == 6, "Leaving authorization services the deferred cycle once.");
        fixture.Runtime.Publish(fixture.Runtime.State with { IsMutating = true, UsageRefreshRevision = 4 }); await Task.Delay(30);
        Check(fixture.TotalReads == 6, "A host account mutation prevents background direct requests.");
        fixture.Runtime.Publish(fixture.Runtime.State with { IsMutating = false }); await Idle(client);
        Check(fixture.TotalReads == 8, "The pending revision is retained until account mutation ends.");

        fixture.Runtime.CommandGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = client.CommandAsync("language", language: "english");
        fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = 5 }); await Task.Delay(30);
        Check(fixture.TotalReads == 8, "An ordinary in-flight command owns the same gate as periodic direct batches.");
        fixture.Runtime.CommandGate.SetResult(); await held; fixture.Runtime.CommandGate = null; await Idle(client);
        Check(fixture.TotalReads == 10, "Releasing an ordinary command services the pending batch once.");
        await client.CommandAsync("refresh"); await Idle(client);
        Check(fixture.TotalReads == 12, "Manual refresh consumes its resulting host revision and is not repeated by the completion snapshot.");
        var before = fixture.Reads.ToDictionary(item => item.Key, item => item.Value);
        await client.CommandAsync("refresh", fixture.IDs[0]); await Idle(client);
        Check(fixture.IDs.All(id => fixture.Reads[id] == before[id] + 1), "Targeted manual refresh consumes only its account; another account's pending host revision is still serviced once.");

        fixture.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = fixture.Runtime.State.UsageRefreshRevision + 1 });
        await Until(() => fixture.ActiveReads == 2);
        Check(client.State.IsMutating, "A scheduled direct batch publishes its busy state before account operations can start.");
        var hostCommands = fixture.Runtime.Calls;
        await client.CommandAsync("refresh"); await client.CommandAsync("remove", fixture.IDs[0]);
        Check(fixture.Runtime.Calls == hostCommands, "Manual refresh and account mutations cannot overlap a pending scheduled batch.");
        await Reject(() => client.CommandAsync("quotaRefreshInterval", language: "10"));
        var pendingReads = fixture.TotalReads;
        fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = fixture.Runtime.State.UsageRefreshRevision + 1 });
        fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = fixture.Runtime.State.UsageRefreshRevision + 1 });
        Check(fixture.TotalReads == pendingReads, "Newer host cycles cannot overlap the running direct batch.");
        fixture.ReadGate.SetResult(); await Idle(client); fixture.ReadGate = null;
        Check(fixture.TotalReads == pendingReads + 2, "Multiple host cycles arriving during a slow read coalesce into one catch-up batch for the newest revision.");
        Check(fixture.MaximumReads <= 2 && fixture.ActiveReads == 0, "Concurrent reads remain bounded to one read per fixture account.");

        var retained = client.State.Accounts.Select(row => row.Usage).ToArray();
        fixture.FailReads = true;
        fixture.Runtime.Publish(fixture.Runtime.State with { UsageRefreshRevision = fixture.Runtime.State.UsageRefreshRevision + 1 }); await Idle(client);
        var failedCount = fixture.TotalReads;
        for (var i = 0; i < 8; i++) fixture.Runtime.Publish(fixture.Runtime.State);
        await Task.Delay(60);
        Check(fixture.TotalReads == failedCount, "Failed quota reads consume the cycle instead of creating an automatic retry loop.");
        Check(client.State.Accounts.Select((row, index) => row.Usage?.RemainingPercent == retained[index]?.RemainingPercent && row.UsageError != null).All(value => value),
            "A failed refresh preserves each account's previous direct quota and attaches an error.");
        fixture.FailReads = false;
        await client.CommandAsync("refresh"); await Idle(client);
        Check(client.State.Accounts.All(row => row.UsageError == null), "An explicit retry clears failed-read state after succeeding.");

        foreach (var invalid in new[] { "", "-1", "1441", "1.5", "abc", " 2", "+2" }) await Reject(() => client.CommandAsync("quotaRefreshInterval", language: invalid));
        var beforeSettings = fixture.TotalReads;
        foreach (var valid in new[] { 0, 1, 2, 5, 10, 15, 37, 1440, 0 }) {
            await client.CommandAsync("quotaRefreshInterval", language: valid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == valid, "The settings command exposes its saved interval before acknowledgement.");
        }
        Check(fixture.TotalReads == beforeSettings, "Changing interval settings does not itself issue a quota refresh.");
        var readsBeforeCredentialChange = fixture.TotalReads;
        fixture.ReplaceCredential(0); fixture.Runtime.Publish(fixture.Runtime.State); await Idle(client);
        Check(fixture.TotalReads == readsBeforeCredentialChange + 1, "Credential replacement still forces an initial read independently of the periodic setting.");
        Check(File.ReadAllBytes(fixture.ActiveCredential).SequenceEqual(fixture.OriginalBytes), "All scheduling tests preserve the active authentication bytes.");
    }
    private static async Task HostAsync(string host)
    {
        var root = Path.Combine(Path.GetTempPath(), "switcher-quota-host-" + Guid.NewGuid());
        var previousCli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        var data = Path.Combine(root, "data"); var active = Path.Combine(root, "active");
        Directory.CreateDirectory(data); Directory.CreateDirectory(active);
        var credential = GuardedChecks.Credential("quota-refresh-host").Bytes;
        File.WriteAllBytes(Path.Combine(active, "auth.json"), credential);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"language\":\"simplifiedChinese\"}");
        try {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Environment.ProcessPath);
            await using (var core = new CoreClient(host, data, active)) {
                await core.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await Until(() => core.State.UsageRefreshRevision > 0, 15000);
                Check(core.State.Settings.QuotaRefreshIntervalMinutes == 5, "A real host loads an old settings file with the compatible five-minute default.");
                await core.CommandAsync("quotaRefreshInterval", language: "0");
                Check(core.State.Settings.QuotaRefreshIntervalMinutes == 0, "A real host acknowledges disabled periodic refresh only after publishing the saved value.");
                using (var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "settings.json"))))
                    Check(saved.RootElement.GetProperty("quotaRefreshIntervalMinutes").GetInt32() == 0, "The disabled setting is persisted by Swift, not a C# mirror.");
                var revision = core.State.UsageRefreshRevision;
                await core.CommandAsync("refresh");
                Check(core.State.UsageRefreshRevision == revision + 1 && core.State.Accounts[0].Usage != null, "Manual refresh while disabled waits for the real host cycle and publishes its revision before acknowledging.");
                foreach (var invalid in new[] { "-1", "1441", "1.5", "invalid" }) await Reject(() => core.CommandAsync("quotaRefreshInterval", language: invalid));
                Check(core.State.Settings.QuotaRefreshIntervalMinutes == 0, "Rejected real-host settings leave the effective value unchanged.");
                await core.CommandAsync("quotaRefreshInterval", language: "1");
                revision = core.State.UsageRefreshRevision;
                Console.WriteLine("Quota host check: waiting for one real 1-minute periodic cycle (synthetic CLI only).");
                await Until(() => core.State.UsageRefreshRevision > revision, 75000);
                Check(core.State.UsageRefreshRevision == revision + 1, "The real configured one-minute host timer produces exactly one completed cycle.");
                await core.CommandAsync("quotaRefreshInterval", language: "0");
                revision = core.State.UsageRefreshRevision;
                Console.WriteLine("Quota host check: verifying disabled periodic refresh stays quiet beyond its former 1-minute deadline.");
                await Task.Delay(TimeSpan.FromSeconds(65));
                Check(core.State.UsageRefreshRevision == revision, "Disabling the real timer cancels its next deadline rather than merely changing displayed settings.");
            }
            await using (var restored = new CoreClient(host, data, active)) {
                await restored.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await Until(() => restored.State.UsageRefreshRevision > 0, 15000);
                Check(restored.State.Settings.QuotaRefreshIntervalMinutes == 0 && restored.State.Accounts[0].Usage != null,
                    "Disabled survives a real host restart while the one-time initial load remains enabled.");
                await restored.CommandAsync("quotaRefreshInterval", language: "37");
            }
            await using (var restored = new CoreClient(host, data, active)) {
                await restored.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(restored.State.Settings.QuotaRefreshIntervalMinutes == 37, "A non-preset custom interval persists through a real host restart.");
            }
            Check(File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(credential), "Real-host scheduling and settings never change the active account credential.");
        } finally {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", previousCli); SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }
    private static void AgeDirectCache(GuardedAccountClient client)
    {
        var cache = (ConcurrentDictionary<Guid, (Usage? Usage, string? Error, DateTimeOffset At, bool UnknownCredits)>)
            typeof(GuardedAccountClient).GetField("directUsage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        foreach (var item in cache.ToArray()) cache[item.Key] = item.Value with { At = DateTimeOffset.UtcNow.AddDays(-1) };
    }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (IOException) { passed++; return; } throw new Exception("Invalid or unavailable settings operation was not rejected."); }
    private static async Task Idle(GuardedAccountClient client) { await Until(() => !client.State.IsMutating); await Task.Delay(35); await Until(() => !client.State.IsMutating); }
    private static async Task Until(Func<bool> condition, int timeout = 5000) {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (started.ElapsedMilliseconds > timeout) throw new TimeoutException("Quota fixture did not reach expected state."); await Task.Delay(15); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "switcher-quota-managed-" + Guid.NewGuid());
        public readonly Guid[] IDs = [Guid.NewGuid(), Guid.NewGuid()];
        public readonly ConcurrentDictionary<Guid, int> Reads = new();
        public int ActiveReads, MaximumReads;
        public int TotalReads => Reads.Values.Sum();
        public TaskCompletionSource? ReadGate;
        public bool FailReads;
        public int ReadRemaining = 80;
        public readonly Credits Credits = new();
        public readonly Runtime Runtime;
        public readonly GuardedAccountClient Client;
        public readonly byte[] OriginalBytes;
        public string ActiveCredential => Path.Combine(root, "active", "auth.json");
        private string Saved(int index) => Path.Combine(root, "data", "accounts", IDs[index].ToString().ToUpperInvariant(), "auth.json");
        public Fixture(bool autoResetEnabled = false) {
            for (var i = 0; i < IDs.Length; i++) { Directory.CreateDirectory(Path.GetDirectoryName(Saved(i))!); File.WriteAllBytes(Saved(i), GuardedChecks.Credential(IDs[i].ToString()).Bytes); }
            OriginalBytes = File.ReadAllBytes(Saved(0)); Directory.CreateDirectory(Path.GetDirectoryName(ActiveCredential)!); File.WriteAllBytes(ActiveCredential, OriginalBytes);
            Runtime = new(AccountSnapshot.Empty with { Settings = new(AutoResetEnabled: autoResetEnabled, QuotaRefreshIntervalMinutes: 0),
                Accounts = IDs.Select(id => new AccountRow(new(id, "Fixture", "quota@example.test"), "Q", null, null)).ToArray(), ActiveAccountID = IDs[0] });
            Client = new(Path.Combine(root, "data"), Path.Combine(root, "active"), () => Runtime, Credits, ReadAsync, new Validator());
        }
        public void ReplaceCredential(int index) => File.AppendAllText(Saved(index), "\n");
        private async Task<Usage> ReadAsync(LoginCredential credential, CancellationToken ct) {
            var id = Guid.Parse(credential.AccountID); Reads.AddOrUpdate(id, 1, (_, value) => value + 1);
            var active = Interlocked.Increment(ref ActiveReads); MaximumReads = Math.Max(MaximumReads, active);
            var remaining = ReadRemaining;
            try {
                if (ReadGate != null) await ReadGate.Task.WaitAsync(ct);
                if (FailReads) throw new IOException("synthetic network failure");
                return new(remaining, DateTimeOffset.UtcNow.AddDays(1), 90, DateTimeOffset.UtcNow.AddHours(2), 2);
            } finally { Interlocked.Decrement(ref ActiveReads); }
        }
        public void Dispose() => SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
    }
    private sealed class Runtime(AccountSnapshot state) : IAccountRuntime
    {
        public AccountSnapshot State { get; private set; } = state;
        public event Action? Changed;
        public TaskCompletionSource? CommandGate;
        public int Calls;
        public Task InitializeAsync() => Task.CompletedTask;
        public void Publish(AccountSnapshot snapshot) { State = snapshot; Changed?.Invoke(); }
        public async Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            Calls++;
            if (CommandGate != null) await CommandGate.Task;
            if (command == "refresh") Publish(State with { UsageRefreshRevision = State.UsageRefreshRevision + 1 });
            if (command == "quotaRefreshInterval") Publish(State with { Settings = State.Settings with { QuotaRefreshIntervalMinutes = int.Parse(language!, System.Globalization.CultureInfo.InvariantCulture) } });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Credits : IResetCreditService {
        public int Available = 2;
        public Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct) => Task.FromResult(new ResetCreditSnapshot(Available, null));
        public Task ConsumeAsync(LoginCredential credential, CancellationToken ct) => throw new Exception("Scheduling may not spend a reset credit.");
    }
    private sealed class Validator : ILoginValidator { public Task ValidateAsync(LoginCredential credential, CancellationToken ct) => Task.CompletedTask; }
}
