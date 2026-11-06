namespace CodexAccountSwitcher.Core;

// Retain the signed-in-account UI/core protocol, wrap desktop handoff
// with online validation and recovery, and stop the host around file import.
public sealed class GuardedAccountClient : IAccountClient, IAccountImporter, IAsyncDisposable, IDesktopOperations
{
    private readonly string dataPath, activeHome;
    private readonly Func<IAccountRuntime> runtimeFactory;
    private readonly IResetCreditService resetCredits;
    private readonly bool ownsResetCredits;
    private readonly Func<LoginCredential, CancellationToken, Task<Usage>> usageReader;
    private readonly ISubscriptionReader subscriptionReader;
    private readonly bool ownsSubscriptionReader;
    private AccountSnapshot pausedState = AccountSnapshot.Empty;
    private bool initialized;
    private readonly IDesktopOperations desktop;
    private readonly ILoginValidator validator;
    private readonly SemaphoreSlim commands = new(1);
    private IAccountRuntime? core;
    private GuardedSwitch? transaction;
    private bool busy;
    private string? error;
    private readonly bool directUsageEnabled;
    private readonly CancellationTokenSource usageLifetime = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (Usage? Usage, string? Error, DateTimeOffset At, bool UnknownCredits)> directUsage = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> usageTasks = new();
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new SynchronizationContext();
    private int usageGeneration;
    private readonly object usageStateLock = new();
    private readonly SemaphoreSlim usageReads = new(4);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> planHints = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> credentialFingerprints = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> resetNeedsRefresh = new();
    private sealed record SubscriptionEntry(SubscriptionSnapshot? Snapshot, string? PlanType, string Status, string? Error, DateTimeOffset At);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SubscriptionEntry> subscriptions = new();
    public AccountSnapshot State {
        get {
            var state = initialized && core != null ? core.State : pausedState;
            return state with {
                IsMutating = busy || state.IsMutating, Error = error ?? state.Error,
                Accounts = state.Accounts.Select(row => {
                    var current = directUsage.TryGetValue(row.Profile.Id, out var entry)
                        ? row with { Usage = entry.Usage ?? row.Usage, UsageError = entry.Error, UsageStatus = entry.Usage != null ? "loaded" : row.Usage != null ? row.UsageStatus : "unavailable" }
                        : row;
                    if ((entry.UnknownCredits || resetNeedsRefresh.ContainsKey(row.Profile.Id)) && current.Usage is { } knownUsage) current = current with { Usage = knownUsage with { ResetCreditsRemaining = null, ResetCreditsExpireAt = null } };
                    var hasSubscription = subscriptions.TryGetValue(row.Profile.Id, out var subscription);
                    var status = subscription?.Status ?? (directUsageEnabled ? "checking" : "unverified");
                    var snapshot = subscription?.Snapshot;
                    if (!hasSubscription || status == "checking" && snapshot == null)
                        snapshot = row.Subscription?.ActiveUntil > DateTimeOffset.UtcNow ? row.Subscription with { IsVerified = false } : null;
                    if (status != "verified" && snapshot?.ActiveUntil <= DateTimeOffset.UtcNow) snapshot = null;
                    var actualExpiry = status == "verified" && snapshot?.IsVerified == true ? snapshot.ActiveUntil : (DateTimeOffset?)null;
                    return current with {
                        Subscription = snapshot, SubscriptionStatus = status, SubscriptionError = subscription?.Error,
                        PlanType = SubscriptionPlan.Resolve(subscription?.PlanType, entry.Usage?.PlanType,
                            planHints.GetValueOrDefault(row.Profile.Id) ?? current.Usage?.PlanType ?? row.PlanType, actualExpiry)
                    };
                }).ToArray()
            };
        }
    }
    public event Action? Changed;
    private void Notify() {
        DetectChangedCredentials();
        Changed?.Invoke();
        if (!directUsageEnabled || usageLifetime.IsCancellationRequested || busy || core == null || !initialized || core.State.IsMutating || core.State.IsAddingAccount) return;
        foreach (var row in core.State.Accounts) {
            var id = row.Profile.Id;
            if (resetNeedsRefresh.ContainsKey(id)) continue;
            if (directUsage.TryGetValue(id, out var entry) && DateTimeOffset.UtcNow - entry.At < TimeSpan.FromMinutes(5)) continue;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (usageTasks.TryAdd(id, completion.Task)) _ = ReadDirectUsageAsync(id, Volatile.Read(ref usageGeneration), completion);
        }
    }
    private async Task ReadDirectUsageAsync(Guid id, int generation, TaskCompletionSource completion) {
        try { await RefreshDirectUsageAsync(id, generation); }
        finally {
            usageTasks.TryRemove(id, out _); completion.TrySetResult();
            if (!usageLifetime.IsCancellationRequested) context.Post(_ => Notify(), null);
        }
    }
    private async Task<string?> RefreshDirectUsageAsync(Guid id, int generation, bool afterReset = false, bool reconcileReset = false) {
        var entered = false;
        Task? subscriptionTask = null;
        try {
            await usageReads.WaitAsync(usageLifetime.Token); entered = true;
            if (generation != Volatile.Read(ref usageGeneration)) return null;
            var path = CredentialPath(id);
            var credential = LoginCredential.Read(path);
            lock (usageStateLock) {
                if (generation != usageGeneration) return null;
                credentialFingerprints[id] = Fingerprint(credential.Bytes);
                if (credential.PlanType != null) planHints[id] = credential.PlanType;
                else planHints.TryRemove(id, out _);
            }
            // The token's optional plan is local metadata: show it before either network request.
            context.Post(_ => { if (!usageLifetime.IsCancellationRequested) Changed?.Invoke(); }, null);
            subscriptionTask = RefreshSubscriptionAsync(id, credential, generation, force: reconcileReset);
            Usage? fresh = null; string? failure = null; var creditsConfirmed = false;
            try { fresh = await usageReader(credential, usageLifetime.Token); }
            catch (OperationCanceledException) when (usageLifetime.IsCancellationRequested) { throw; }
            catch { failure = "读取额度失败，请检查网络或重新授权后刷新。"; }
            try {
                var credits = await resetCredits.ReadAsync(credential, usageLifetime.Token);
                creditsConfirmed = credits.AvailableCount.HasValue;
                fresh ??= State.Accounts.FirstOrDefault(row => row.Profile.Id == id)?.Usage;
                if (fresh != null) fresh = fresh with { ResetCreditsRemaining = credits.AvailableCount ?? fresh.ResetCreditsRemaining, ResetCreditsExpireAt = credits.EarliestExpiry };
            } catch (OperationCanceledException) when (usageLifetime.IsCancellationRequested) { throw; }
            catch { failure ??= "额度已读取，但重置次数明细暂时不可用。"; }
            lock (usageStateLock) {
                if (generation == usageGeneration && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(credential.Bytes)) {
                    directUsage[id] = (fresh, failure, DateTimeOffset.UtcNow, afterReset && !creditsConfirmed);
                    if (creditsConfirmed && (afterReset || reconcileReset)) resetNeedsRefresh.TryRemove(id, out _);
                }
            }
            if (afterReset && !creditsConfirmed) failure ??= "重置次数尚未确认，请刷新后核对。";
            return failure;
        } catch (OperationCanceledException) when (usageLifetime.IsCancellationRequested) { return null; }
        catch {
            const string message = "读取账号信息失败，请检查登录文件、网络或重新授权。";
            lock (usageStateLock) { if (generation == usageGeneration) directUsage[id] = (null, message, DateTimeOffset.UtcNow, afterReset); }
            return message;
        } finally {
            if (subscriptionTask != null) await subscriptionTask;
            if (entered) usageReads.Release();
        }
    }
    private async Task RefreshSubscriptionAsync(Guid id, LoginCredential credential, int generation, bool force) {
        var checkedAt = DateTimeOffset.UtcNow;
        subscriptions.TryGetValue(id, out var previous);
        if (!force && previous != null && previous.Status != "checking" && checkedAt - previous.At < TimeSpan.FromMinutes(30)) return;
        lock (usageStateLock) {
            if (generation != usageGeneration) return;
            subscriptions[id] = new(previous?.Snapshot, previous?.PlanType, "checking", null, previous?.At ?? checkedAt);
        }
        SubscriptionEntry next;
        try {
            var result = await subscriptionReader.ReadAsync(credential, usageLifetime.Token);
            var snapshot = result.ActiveUntil is { } until ? new SubscriptionSnapshot(until, checkedAt, result.Source, IsVerified: true) : null;
            var retained = previous?.Snapshot is { IsVerified: true } last && last.ActiveUntil > checkedAt ? last : null;
            next = snapshot != null ? new(snapshot, result.PlanType, "verified", null, checkedAt)
                : new(retained, result.PlanType ?? previous?.PlanType, retained == null ? "unavailable" : "stale",
                    "官方订阅接口暂未返回有效期，请稍后刷新核验。", checkedAt);
        } catch (OperationCanceledException) when (usageLifetime.IsCancellationRequested) { return; }
        catch {
            var retained = previous?.Snapshot is { IsVerified: true } last && last.ActiveUntil > checkedAt ? last : null;
            next = new(retained, previous?.PlanType, retained == null ? "unavailable" : "stale",
                "订阅有效期尚未核验，请检查网络或重新授权后刷新。", checkedAt);
        }
        lock (usageStateLock) {
            try {
                var path = CredentialPath(id);
                if (generation == usageGeneration && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(credential.Bytes)) subscriptions[id] = next;
            } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        context.Post(_ => { if (!usageLifetime.IsCancellationRequested) Changed?.Invoke(); }, null);
    }
    private int AdvanceUsageGeneration(bool clear = false) {
        lock (usageStateLock) {
            if (clear) { directUsage.Clear(); planHints.Clear(); credentialFingerprints.Clear(); subscriptions.Clear(); }
            return ++usageGeneration;
        }
    }
    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    private void DetectChangedCredentials() {
        if (core == null || !initialized || usageLifetime.IsCancellationRequested) return;
        foreach (var row in core.State.Accounts) {
            var id = row.Profile.Id;
            if (!credentialFingerprints.TryGetValue(id, out var observed)) continue;
            string? current = null;
            try {
                var path = CredentialPath(id); SecureFiles.RejectLinks(path);
                if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024) current = Fingerprint(File.ReadAllBytes(path));
            } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (current == observed) continue;
            lock (usageStateLock) {
                if (credentialFingerprints.TryGetValue(id, out var latest) && latest == observed) {
                    ++usageGeneration;
                    credentialFingerprints.TryRemove(id, out _); planHints.TryRemove(id, out _); directUsage.TryRemove(id, out _); subscriptions.TryRemove(id, out _);
                }
            }
        }
    }
    private string CredentialPath(Guid id) => Path.Combine(dataPath, "accounts", id.ToString().ToUpperInvariant(), "auth.json");
    private static async Task<Usage> ReadOfficialUsageAsync(LoginCredential credential, CancellationToken ct) {
        using var reader = new OfficialUsageReader(); return await reader.ReadAsync(credential, ct);
    }

    public GuardedAccountClient(string hostPath, string dataPath, string? activeHome = null,
        IDesktopOperations? desktop = null, ILoginValidator? validator = null,
        IResetCreditService? resetCredits = null, Func<LoginCredential, CancellationToken, Task<Usage>>? usageReader = null,
        ISubscriptionReader? subscriptionReader = null)
    {
        this.dataPath = dataPath;
        this.desktop = desktop ?? new DesktopController(ct => DesktopController.DiscoverAsync(null, ct));
        this.validator = validator ?? new OnlineLoginValidator();
        directUsageEnabled = validator == null || resetCredits != null || usageReader != null || subscriptionReader != null;
        this.resetCredits = resetCredits ?? new OfficialResetCredits(); ownsResetCredits = resetCredits == null;
        this.usageReader = usageReader ?? ReadOfficialUsageAsync;
        this.subscriptionReader = subscriptionReader ?? new OfficialSubscriptionReader(); ownsSubscriptionReader = subscriptionReader == null;
        this.activeHome = activeHome ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        runtimeFactory = () => new HostedRuntime(new CoreClient(hostPath, dataPath, this.activeHome, this));
    }

    internal GuardedAccountClient(string dataPath, string activeHome, Func<IAccountRuntime> runtimeFactory,
        IResetCreditService resetCredits, Func<LoginCredential, CancellationToken, Task<Usage>> usageReader, ILoginValidator validator,
        ISubscriptionReader? subscriptionReader = null)
        : this("", dataPath, activeHome, validator: validator, resetCredits: resetCredits, usageReader: usageReader, subscriptionReader: subscriptionReader ?? new EmptySubscriptionReader())
        => this.runtimeFactory = runtimeFactory;

    public async Task InitializeAsync()
    {
        var runtime = runtimeFactory();
        core = runtime; initialized = false; runtime.Changed += Notify;
        try { await runtime.InitializeAsync(); initialized = true; Notify(); }
        catch { runtime.Changed -= Notify; await runtime.DisposeAsync(); if (core == runtime) core = null; throw; }
    }

    private async Task StopCoreAsync()
    {
        if (core != null) {
            var runtime = core;
            if (initialized) pausedState = runtime.State;
            initialized = false; runtime.Changed -= Notify;
            // Keep the runtime assigned until disposal completes so initialization cannot overlap it.
            await runtime.DisposeAsync(); core = null;
        }
    }

    private async Task ResetQuotaAsync(Guid id)
    {
        if (core == null || core.State.IsMutating || core.State.IsAddingAccount) return;
        if (resetNeedsRefresh.ContainsKey(id)) { error = "上次重置结果尚待核对，请先点击刷新，再决定是否重置。"; return; }
        var account = State.Accounts.FirstOrDefault(row => row.Profile.Id == id)
            ?? throw new IOException("该账号已不存在，请刷新列表。");
        busy = true; error = null; Notify();
        var generation = AdvanceUsageGeneration();
        var succeeded = false; var hostStopped = false;
        try {
            await StopCoreAsync(); hostStopped = true;
            var credential = LoginCredential.Read(CredentialPath(id));
            await resetCredits.ConsumeAsync(credential, usageLifetime.Token);
            succeeded = true;
            resetNeedsRefresh[id] = 0;
            // Do not show the old count while the confirmed result is being refreshed.
            directUsage[id] = (account.Usage, null, DateTimeOffset.UtcNow, true);
            var refreshError = await RefreshDirectUsageAsync(id, generation, afterReset: true);
            if (refreshError != null) error = "重置请求已成功，但最新额度或次数尚未完整读取，请刷新后核对。";
        } catch (Exception ex) {
            if (ex is not ResetCreditException || ((ResetCreditException)ex).OutcomeUncertain) resetNeedsRefresh[id] = 0;
            var knownCount = (ex as ResetCreditException)?.AvailableCount;
            directUsage[id] = (account.Usage is { } prior ? prior with { ResetCreditsRemaining = knownCount, ResetCreditsExpireAt = null } : null,
                "请刷新次数和额度核对。", DateTimeOffset.UtcNow, knownCount == null);
            error = ex is ResetCreditException safe ? safe.Message : hostStopped ? "重置未能完成，请刷新次数和额度核对后再操作。" : "未能停止账号服务，本次未执行重置。请重新打开工具。";
        } finally {
            try { if (hostStopped) await InitializeAsync(); }
            catch { error = (succeeded ? "重置请求已成功。" : "重置操作未完成。") + "账号服务恢复失败，请重新打开本工具后核对额度。"; }
        }
    }

    public async Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null)
    {
        if (!await commands.WaitAsync(0)) return;
        try {
            if (core == null) throw new IOException("Account core is unavailable. Restart the app.");
            if (command == "dismissError") error = null;
            if (command == "resetQuota") { if (accountID is { } resetId) await ResetQuotaAsync(resetId); return; }
            if (command == "refresh" && directUsageEnabled) {
                if (core.State.IsMutating || core.State.IsAddingAccount) return;
                busy = true; error = null; Notify();
                // The shared refresh also synchronizes the active login and subscription snapshots.
                // Its quota task continues asynchronously after the transport command completes.
                await core.CommandAsync("refresh", accountID);
                var generation = AdvanceUsageGeneration();
                var ids = State.Accounts.Where(row => accountID == null || row.Profile.Id == accountID).Select(row => row.Profile.Id).ToArray();
                await Task.WhenAll(ids.Select(id => RefreshDirectUsageAsync(id, generation, reconcileReset: true)));
                return;
            }
            if (command != "switch") {
                if (command == "refresh") foreach (var entry in directUsage.Where(x => x.Value.Usage == null).ToArray()) directUsage.TryRemove(entry.Key, out _);
                await core.CommandAsync(command, accountID, value, language);
                if (command == "remove" && accountID is { } removed) { AdvanceUsageGeneration(); directUsage.TryRemove(removed, out _); planHints.TryRemove(removed, out _); credentialFingerprints.TryRemove(removed, out _); resetNeedsRefresh.TryRemove(removed, out _); subscriptions.TryRemove(removed, out _); }
                return;
            }
            if (accountID == null || core.State.IsMutating || core.State.IsAddingAccount) return;
            busy = true; error = null; AdvanceUsageGeneration(clear: true); Notify();
            await core.CommandAsync("dismissError");
            transaction = new GuardedSwitch(Path.Combine(dataPath, "accounts", accountID.Value.ToString().ToUpperInvariant()), activeHome, validator, desktop);
            Exception? transportError = null;
            try { await core.CommandAsync(command, accountID); }
            catch (Exception ex) { transportError = ex; }
            if (transaction.CloseAttempted && !transaction.OpenSucceeded) {
                var failure = core.State.Error ?? transportError?.Message ?? "切换未完成。 / Switch failed.";
                await StopCoreAsync();
                try {
                    transaction.RestoreCredential();
                    await InitializeAsync(); // Re-read restored identity; never edit a live host's cached registry.
                    await desktop.OpenAsync(CancellationToken.None);
                    error = failure + "\n已恢复原登录并重新打开 Codex。 / Previous login restored.";
                } catch {
                    error = failure + "\n自动恢复未完成。请手动打开 Codex 并核对当前账号。 / Recovery needs manual attention.";
                    if (core == null) await InitializeAsync();
                }
            } else if (transportError != null) { throw transportError; }
        } finally {
            transaction?.Dispose(); transaction = null; busy = false; commands.Release(); Notify();
        }
    }

    public Task<ImportResult> ImportFilesAsync(string[] paths) => ImportAsync(() => AccountImport.ParseFiles(paths));
    public Task<ImportResult> ImportTextAsync(string json) => ImportAsync(() => AccountImport.ParseText(json));

    private async Task<ImportResult> ImportAsync(Func<LoginCredential[]> parse)
    {
        if (!await commands.WaitAsync(0)) throw new IOException("请等待当前操作完成。 / Another operation is running.");
        try {
            if (core?.State.IsMutating == true || core?.State.IsAddingAccount == true)
                throw new IOException("请先完成或取消登录操作。 / Finish the current login first.");
            busy = true; error = null; Notify();
            var credentials = parse(); // Fully parse before stopping or writing anything. Pasted text is never saved to a temporary file.
            AdvanceUsageGeneration(clear: true);
            await StopCoreAsync();
            try { return AccountImport.Commit(dataPath, activeHome, credentials); }
            finally { await InitializeAsync(); }
        } finally { busy = false; commands.Release(); Notify(); }
    }

    public Task CloseAsync(CancellationToken ct) => transaction?.CloseAsync(ct)
        ?? Task.FromException(new IOException("A validated switch is required."));
    public Task OpenAsync(CancellationToken ct) => transaction?.OpenAsync(ct)
        ?? Task.FromException(new IOException("A validated switch is required."));
    public async ValueTask DisposeAsync() {
        usageLifetime.Cancel(); await Task.WhenAll(usageTasks.Values.ToArray());
        await commands.WaitAsync();
        try { await StopCoreAsync(); }
        finally { commands.Release(); }
        (validator as IDisposable)?.Dispose(); if (ownsResetCredits) (resetCredits as IDisposable)?.Dispose();
        if (ownsSubscriptionReader) (subscriptionReader as IDisposable)?.Dispose();
        commands.Dispose(); usageReads.Dispose(); usageLifetime.Dispose();
    }
    private sealed class HostedRuntime(CoreClient client) : IAccountRuntime {
        public AccountSnapshot State => client.State;
        public event Action? Changed { add => client.Changed += value; remove => client.Changed -= value; }
        public Task InitializeAsync() => client.InitializeAsync();
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) => client.CommandAsync(command, accountID, value, language);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
    private sealed class EmptySubscriptionReader : ISubscriptionReader {
        public Task<SubscriptionLookup> ReadAsync(LoginCredential credential, CancellationToken ct) => Task.FromResult(new SubscriptionLookup(credential.AccountID, null, null, "unavailable"));
    }
}

internal interface IAccountRuntime : IAccountClient, IAsyncDisposable { Task InitializeAsync(); }
