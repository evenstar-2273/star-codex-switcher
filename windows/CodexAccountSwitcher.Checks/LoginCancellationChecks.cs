using System.Diagnostics;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class LoginCancellationChecks
{
    private static int passed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    public static async Task<int> RunAsync(string? host)
    {
        try {
            await CheckManagedAsync();
            if (host != null) await CheckHostAsync(Path.GetFullPath(host));
            Console.WriteLine($"PASS: {passed} login cancellation assertions; priority cancellation, bounded recovery, late replies, isolated original credentials.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task CheckManagedAsync()
    {
        using (var fixture = new ManagedFixture("held-start")) {
            await using var client = fixture.Client(); await client.InitializeAsync();
            var add = client.CommandAsync("add");
            await Until(() => client.State.IsAddingAccount);
            await client.CommandAsync("cancelAdd").WaitAsync(TimeSpan.FromSeconds(2));
            await add.WaitAsync(TimeSpan.FromSeconds(2));
            Check(fixture.Runtimes[0].CancelCalls == 1, "Cancel bypasses an outstanding add command acknowledgement.");
            Check(!client.State.IsAddingAccount && !client.State.IsMutating && fixture.Runtimes.Count == 1, "Responsive cancellation unlocks controls without restarting the host.");
            fixture.CheckCredential();
        }
        foreach (var mode in new[] { "no-ack", "ack-but-pending" }) {
            using var fixture = new ManagedFixture(mode);
            await using var client = fixture.Client(); await client.InitializeAsync();
            await client.CommandAsync("add"); var abandoned = fixture.Runtimes[0];
            await client.CommandAsync("cancelAdd").WaitAsync(TimeSpan.FromSeconds(2));
            Check(fixture.Runtimes.Count == 2 && abandoned.Stopped && fixture.MaximumRunning == 1, "A stuck login is stopped before a replacement runtime starts.");
            Check(!client.State.IsAddingAccount && !client.State.IsMutating, "An acknowledgement without cleanup cannot leave controls disabled forever.");
            abandoned.PublishLate();
            Check(client.State.Accounts.Length == 1 && !client.State.IsAddingAccount, "Late snapshots from the disposed login cannot change the replacement runtime.");
            fixture.CheckCredential();
        }
        using (var fixture = new ManagedFixture("held-start-no-ack")) {
            await using var client = fixture.Client(); await client.InitializeAsync();
            var add = client.CommandAsync("add"); await Until(() => client.State.IsAddingAccount);
            await client.CommandAsync("cancelAdd").WaitAsync(TimeSpan.FromSeconds(2));
            await add.WaitAsync(TimeSpan.FromSeconds(2));
            Check(fixture.Runtimes.Count == 2 && client.State.Error == null, "Stopping a stuck add acknowledgement does not surface its expected transport shutdown as a new login error.");
            fixture.CheckCredential();
        }
        using (var fixture = new ManagedFixture("no-ack") { FailRestart = true }) {
            await using var client = fixture.Client(); await client.InitializeAsync(); await client.CommandAsync("add");
            await client.CommandAsync("cancelAdd");
            Check(!client.State.IsAddingAccount && !client.State.IsMutating && client.State.Error?.Contains("重新打开") == true,
                "A restart failure reports recovery while keeping the stopped login out of pending state.");
            fixture.CheckCredential();
        }
        using (var fixture = new ManagedFixture("no-ack", hasActiveCredential: false)) {
            await using var client = fixture.Client(); await client.InitializeAsync(); await client.CommandAsync("add");
            await client.CommandAsync("cancelAdd");
            fixture.CheckCredential();
            Check(client.State.ActiveAccountID == null, "Cancelling an initially signed-out session does not create an active login.");
        }
        foreach (var mode in new[] { "held-start-no-ack", "held-before-snapshot" }) {
            using var fixture = new ManagedFixture(mode);
            var client = fixture.Client(); await client.InitializeAsync();
            var add = client.CommandAsync("add");
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await add.WaitAsync(TimeSpan.FromSeconds(2));
            Check(fixture.Runtimes.Count == 1 && fixture.Runtimes[0].Stopped && fixture.Running == 0,
                "Quitting a hung add stops its runtime without waiting forever for the add command gate or restarting it.");
            fixture.CheckCredential();
        }
    }

    private static async Task CheckHostAsync(string host)
    {
        var root = Path.Combine(Path.GetTempPath(), "switcher-cancel-host-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var names = new[] { "CODEX_CLI_PATH", "SWITCHER_LOGIN_FIXTURE_ROOT" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var active = Path.Combine(root, "active"); Directory.CreateDirectory(active);
        var original = GuardedChecks.Credential("original-login").Bytes;
        File.WriteAllBytes(Path.Combine(active, "auth.json"), original);
        Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Environment.ProcessPath);
        Environment.SetEnvironmentVariable("SWITCHER_LOGIN_FIXTURE_ROOT", root);
        try {
            var browserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateBrowserReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var browserCalls = 0;
            await using (var client = new CoreClient(host, Path.Combine(root, "data"), active, openBrowser: async (url, ct) => {
                Check(url.Host == "example.test", "Integration never opens a real authentication URL.");
                if (Interlocked.Increment(ref browserCalls) == 1) {
                    browserStarted.TrySetResult(); await lateBrowserReply.Task.WaitAsync(ct);
                }
            })) {
                await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var originalCount = client.State.Accounts.Length;
                await client.CommandAsync("add").WaitAsync(TimeSpan.FromSeconds(3));
                await browserStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await client.CommandAsync("cancelAdd").WaitAsync(TimeSpan.FromSeconds(3));
                await Until(() => !client.State.IsAddingAccount);
                Check(!client.State.IsMutating, "Cancelling while the browser platform reply is pending completes cleanup.");
                var sessions = Directory.GetFiles(root, "login-session-*.json");
                Check(sessions.Length == 1, "Exactly one isolated login process was started.");
                foreach (var session in sessions) {
                    using var document = JsonDocument.Parse(File.ReadAllText(session));
                    var pid = document.RootElement.GetProperty("pid").GetInt32();
                    var home = document.RootElement.GetProperty("home").GetString()!;
                    await Until(() => !IsRunning(pid));
                    Check(!Directory.Exists(home), "Cancellation removes the unregistered temporary login profile.");
                    File.WriteAllText(Path.Combine(root, "complete-" + pid), "late browser authorization");
                }
                lateBrowserReply.TrySetResult();
                await client.CommandAsync("percentage", value: false).WaitAsync(TimeSpan.FromSeconds(3));
                await Task.Delay(100);
                Check(client.State.Accounts.Length == originalCount && !client.State.IsAddingAccount,
                    "A late browser platform reply/authorization cannot import the cancelled account.");
                await client.CommandAsync("add").WaitAsync(TimeSpan.FromSeconds(3));
                await Until(() => Volatile.Read(ref browserCalls) == 2);
                await client.CommandAsync("cancelAdd").WaitAsync(TimeSpan.FromSeconds(3));
                await Until(() => !client.State.IsAddingAccount);
                Check(client.State.Accounts.Length == originalCount, "A second login cancels independently after a late first reply.");
                Check(File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(original), "Every real-host cancellation preserves the original active credential bytes.");
            }
        } finally {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }

    private static bool IsRunning(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task Until(Func<bool> condition) {
        for (var i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(20); }
        throw new TimeoutException("The isolated cancellation did not complete.");
    }
    private sealed class ManagedFixture : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "switcher-cancel-managed-" + Guid.NewGuid());
        private readonly string mode;
        private readonly bool hasActiveCredential;
        private readonly byte[] original = GuardedChecks.Credential("original-login").Bytes;
        private readonly AccountSnapshot snapshot;
        public readonly List<Runtime> Runtimes = [];
        public bool FailRestart;
        public int Running, MaximumRunning;
        public ManagedFixture(string mode, bool hasActiveCredential = true) {
            this.mode = mode; this.hasActiveCredential = hasActiveCredential; var id = Guid.NewGuid();
            Directory.CreateDirectory(Path.Combine(root, "active"));
            if (hasActiveCredential) File.WriteAllBytes(Path.Combine(root, "active", "auth.json"), original);
            var saved = Path.Combine(root, "data", "accounts", id.ToString().ToUpperInvariant()); Directory.CreateDirectory(saved); File.WriteAllBytes(Path.Combine(saved, "auth.json"), original);
            snapshot = AccountSnapshot.Empty with { Accounts = [new(new(id, "Original fixture", "fixture@example.test"), "F", null, null)], ActiveAccountID = hasActiveCredential ? id : null };
        }
        public GuardedAccountClient Client() => new(Path.Combine(root, "data"), Path.Combine(root, "active"), () => {
            if (FailRestart && Runtimes.Count > 0) throw new IOException("fixture restart failed");
            var runtime = new Runtime(this, snapshot, mode); Runtimes.Add(runtime); return runtime;
        }, new Credits(), (_, _) => Task.FromResult(new Usage(50, DateTimeOffset.UtcNow.AddDays(1), null, null, 0)), new Validator(), loginCancellationTimeout: TimeSpan.FromMilliseconds(80));
        public void CheckCredential() => Check(hasActiveCredential ? File.ReadAllBytes(Path.Combine(root, "active", "auth.json")).SequenceEqual(original)
            : !File.Exists(Path.Combine(root, "active", "auth.json")), "Managed cancellation never overwrites or creates the active login.");
        public void Dispose() => SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
    }
    private sealed class Runtime(ManagedFixture fixture, AccountSnapshot snapshot, string mode) : IAccountRuntime {
        private readonly TaskCompletionSource addAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancelAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AccountSnapshot State { get; private set; } = snapshot;
        public event Action? Changed;
        public int CancelCalls;
        public bool Stopped;
        public Task InitializeAsync() { fixture.MaximumRunning = Math.Max(fixture.MaximumRunning, ++fixture.Running); return Task.CompletedTask; }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            if (command == "add") {
                if (mode != "held-before-snapshot") { State = State with { IsAddingAccount = true }; Changed?.Invoke(); }
                return mode.StartsWith("held-", StringComparison.Ordinal) ? addAck.Task : Task.CompletedTask;
            }
            if (command == "cancelAdd") {
                CancelCalls++;
                if (mode == "held-start") { State = State with { IsAddingAccount = false }; Changed?.Invoke(); addAck.TrySetResult(); }
                return mode is "no-ack" or "held-start-no-ack" ? cancelAck.Task : Task.CompletedTask;
            }
            return Task.CompletedTask;
        }
        public void PublishLate() { State = State with { IsAddingAccount = true, Accounts = [] }; Changed?.Invoke(); }
        public ValueTask DisposeAsync() {
            if (!Stopped) {
                Stopped = true; fixture.Running--;
                if (mode is "held-start-no-ack" or "held-before-snapshot") addAck.TrySetException(new IOException("Expected fixture transport shutdown.")); else addAck.TrySetResult();
                cancelAck.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Credits : IResetCreditService {
        public Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct) => Task.FromResult(new ResetCreditSnapshot(0, null));
        public Task ConsumeAsync(LoginCredential credential, CancellationToken ct) => throw new InvalidOperationException("Cancellation tests never reset credits.");
    }
    private sealed class Validator : ILoginValidator { public Task ValidateAsync(LoginCredential credential, CancellationToken ct) => Task.CompletedTask; }
}
