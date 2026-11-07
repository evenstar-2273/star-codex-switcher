using System.Collections.Concurrent;
using System.Reflection;
using CodexAccountSwitcher.Core;

internal static class CommandCoordinationChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }

    public static async Task<int> RunAsync() {
        try { await VerifyAsync(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    public static async Task VerifyAsync() {
        passed = 0;
        Console.WriteLine("Command coordination: initialization ownership and queued cancellation.");
        foreach (var command in new[] { "add", "refresh", "remove" }) await DuringAcquisitionAsync(command);
        Console.WriteLine("Command coordination: background completion boundary.");
        foreach (var command in new[] { "add", "refresh", "remove" })
            for (var iteration = 0; iteration < 32; iteration++) await DuringCompletionAsync(command);
        Console.WriteLine("Command coordination: shutdown with a foreground waiter.");
        await ShutdownAsync();
        Console.WriteLine($"PASS: {passed} command coordination assertions; atomic ownership, one foreground reservation, cancellable queued login and safe shutdown (synthetic only).");
    }

    private static async Task DuringAcquisitionAsync(string command) {
        using var fixture = new Fixture();
        await using var client = fixture.Client;
        fixture.Runtime.HoldAdd = command == "add";
        fixture.Runtime.PauseNextOwnedStateRead();
        var initialize = Task.Run(client.InitializeAsync);
        Task? pending = null;
        try {
            await fixture.Runtime.StateReadPaused.Task.WaitAsync(Deadline);
            pending = client.CommandAsync(command, fixture.Id);
            Check(!pending.IsCompleted && client.State.IsMutating && fixture.Runtime.Count(command) == 0,
                $"{command}: a click during initialization's acquired background gate is queued, never silently discarded.");
            await client.CommandAsync(command, fixture.Id).WaitAsync(Deadline);
            Check(fixture.Runtime.Count(command) == 0, $"{command}: a repeated click cannot create a second foreground reservation.");
        } finally { fixture.Runtime.ResumeStateRead.Set(); }
        await initialize.WaitAsync(Deadline);
        if (command == "add") {
            await Until(() => fixture.Runtime.Count("add") == 1 && client.State.IsAddingAccount);
            Check(!client.State.IsMutating && !pending!.IsCompleted,
                "A queued login becomes cancellable after it acquires the gate, even while its acknowledgement is pending.");
            await client.CommandAsync("cancelAdd").WaitAsync(Deadline);
            Check(fixture.Runtime.Count("cancelAdd") == 1 && !client.State.IsAddingAccount,
                "Priority cancellation reaches a queued login without waiting for the add acknowledgement.");
        }
        await pending!.WaitAsync(Deadline);
        Check(fixture.Runtime.Count(command) == 1, $"{command}: the reserved operation is delivered exactly once after initialization proceeds.");
        fixture.CheckCredential();
    }

    private static async Task DuringCompletionAsync(string command) {
        using var fixture = new Fixture { ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var client = fixture.Client;
        await client.InitializeAsync(); await fixture.ReadStarted.Task.WaitAsync(Deadline);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = Task.Run(async () => { await start.Task; fixture.ReadGate.TrySetResult(); });
        var foreground = Task.Run(async () => { await start.Task; await client.CommandAsync(command, fixture.Id); });
        try {
            start.SetResult();
            await Task.WhenAll(complete, foreground).WaitAsync(Deadline);
            Check(fixture.Runtime.Count(command) == 1, $"{command}: a click concurrent with background completion is delivered exactly once.");
            fixture.CheckCredential();
        } finally { start.TrySetResult(); fixture.ReadGate.TrySetResult(); }
    }

    private static async Task ShutdownAsync() {
        using var fixture = new Fixture { ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var client = fixture.Client;
        Task? queued = null;
        try {
            await client.InitializeAsync(); await fixture.ReadStarted.Task.WaitAsync(Deadline);
            queued = client.CommandAsync("add");
            Check(!queued.IsCompleted && client.State.IsMutating, "Shutdown fixture has a real foreground waiter behind a pending read.");
        } finally {
            await client.DisposeAsync().AsTask().WaitAsync(Deadline);
            fixture.ReadGate.TrySetResult();
        }
        await queued!.WaitAsync(Deadline);
        Check(fixture.Runtime.Count("add") == 0 && fixture.Runtime.Disposed,
            "Shutdown cancels the pending read and reservation without starting a new login or deadlocking disposal.");
        fixture.CheckCredential();
    }

    private static async Task Until(Func<bool> condition) {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) {
            if (started.Elapsed >= Deadline) throw new TimeoutException("Command fixture did not reach the expected state.");
            await Task.Delay(5);
        }
    }

    private sealed class Fixture : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "switcher-command-coordination-" + Guid.NewGuid());
        private readonly byte[] original = GuardedChecks.Credential("command-coordination").Bytes;
        public Guid Id { get; } = Guid.NewGuid();
        public readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ReadGate;
        public readonly Runtime Runtime;
        public readonly GuardedAccountClient Client;
        private string ActiveCredential => Path.Combine(root, "active", "auth.json");
        public Fixture() {
            var saved = Path.Combine(root, "data", "accounts", Id.ToString().ToUpperInvariant());
            Directory.CreateDirectory(saved); File.WriteAllBytes(Path.Combine(saved, "auth.json"), original);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveCredential)!); File.WriteAllBytes(ActiveCredential, original);
            Runtime = new(AccountSnapshot.Empty with {
                Settings = new(QuotaRefreshIntervalMinutes: 0), ActiveAccountID = Id,
                Accounts = [new(new(Id, "Fixture", "fixture@example.test"), "F", null, null)]
            });
            Client = new(Path.Combine(root, "data"), Path.Combine(root, "active"), () => Runtime,
                new Credits(), ReadAsync, new Validator());
            // Observe ownership only; no private state is changed. The normal runtime
            // snapshot getter provides a controllable pause after Notify takes its gate.
            var gate = (SemaphoreSlim)typeof(GuardedAccountClient).GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!;
            Runtime.GateIsHeld = () => gate.CurrentCount == 0;
        }
        private async Task<Usage> ReadAsync(LoginCredential credential, CancellationToken ct) {
            ReadStarted.TrySetResult();
            if (ReadGate != null) await ReadGate.Task.WaitAsync(ct);
            return new(80, DateTimeOffset.UtcNow.AddDays(1), null, null, 0);
        }
        public void CheckCredential() => Check(File.ReadAllBytes(ActiveCredential).SequenceEqual(original), "Command coordination never modifies the active credential bytes.");
        public void Dispose() {
            Runtime.ResumeStateRead.Set(); ReadGate?.TrySetResult();
            SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }

    private sealed class Runtime(AccountSnapshot initial) : IAccountRuntime {
        private AccountSnapshot state = initial;
        private int pauseNextOwnedRead;
        private readonly ConcurrentDictionary<string, int> calls = new();
        private readonly TaskCompletionSource addAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource StateReadPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim ResumeStateRead = new(false);
        public Func<bool>? GateIsHeld;
        public bool HoldAdd, Disposed;
        public AccountSnapshot State {
            get {
                if (Volatile.Read(ref pauseNextOwnedRead) != 0 && GateIsHeld?.Invoke() == true && Interlocked.Exchange(ref pauseNextOwnedRead, 0) != 0) {
                    StateReadPaused.TrySetResult();
                    if (!ResumeStateRead.Wait(Deadline)) throw new TimeoutException("Owned snapshot pause was not released.");
                }
                return state;
            }
        }
        public event Action? Changed;
        public void PauseNextOwnedStateRead() => Volatile.Write(ref pauseNextOwnedRead, 1);
        public int Count(string command) => calls.GetValueOrDefault(command);
        public Task InitializeAsync() => Task.CompletedTask;
        private void Publish(AccountSnapshot next) { state = next; Changed?.Invoke(); }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            calls.AddOrUpdate(command, 1, (_, count) => count + 1);
            if (command == "add") {
                Publish(state with { IsAddingAccount = true });
                return HoldAdd ? addAcknowledgement.Task : Task.CompletedTask;
            }
            if (command == "cancelAdd") { Publish(state with { IsAddingAccount = false }); addAcknowledgement.TrySetResult(); }
            if (command == "refresh") Publish(state with { UsageRefreshRevision = state.UsageRefreshRevision + 1 });
            if (command == "remove") Publish(state with { Accounts = [], ActiveAccountID = null });
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; addAcknowledgement.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class Credits : IResetCreditService {
        public Task<ResetCreditSnapshot> ReadAsync(LoginCredential credential, CancellationToken ct) => Task.FromResult(new ResetCreditSnapshot(0, null));
        public Task ConsumeAsync(LoginCredential credential, CancellationToken ct) => throw new Exception("Coordination tests never consume credits.");
    }
    private sealed class Validator : ILoginValidator { public Task ValidateAsync(LoginCredential credential, CancellationToken ct) => Task.CompletedTask; }
}
