namespace CodexAccountSwitcher.Core;

// Retain the signed-in-account UI/core protocol, wrap desktop handoff
// with online validation and recovery, and stop the host around file import.
public sealed class GuardedAccountClient : IAccountClient, IAccountImporter, IAccountExporter, IAsyncDisposable, IDesktopOperations
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
    private readonly object commandStateLock = new();
    private volatile string? activeCommand;
    private readonly SemaphoreSlim loginCancellation = new(1);
    private TimeSpan loginCancellationTimeout = TimeSpan.FromSeconds(8);
    private IAccountRuntime? core;
    private GuardedSwitch? transaction;
    private bool busy;
    private int foregroundWaiting;
    private volatile bool cancellingLogin;
    private volatile bool stopping;
    private string? error;
    private readonly bool directUsageEnabled;
    private readonly CancellationTokenSource usageLifetime = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (Usage? Usage, string? Error, DateTimeOffset At, bool UnknownCredits, string? ReportedPlan)> directUsage = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> usageRevisions = new();
    private Task? automaticUsageTask;
    private CancellationTokenSource? automaticUsageCancellation;
    private volatile bool initialCycleMayReset;
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new SynchronizationContext();
    private int usageGeneration;
    private readonly object usageStateLock = new();
    private readonly SemaphoreSlim usageReads = new(4);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> accountReads = new();
    private readonly Dictionary<Guid, Task> manualReads = new();
    private readonly Dictionary<Guid, CancellationTokenSource> manualReadCancellation = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> planHints = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> credentialFingerprints = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> resetNeedsRefresh = new();
    private sealed record SubscriptionEntry(SubscriptionSnapshot? Snapshot, string? PlanType, string Status, string? Error, DateTimeOffset At, string? Identity = null) {
        public bool ConfirmedFree => Status == "verified" && Snapshot == null && PlanType == "free";
    }
    private static string SubscriptionIdentity(LoginCredential credential) => credential.AccountID + "|" + credential.OrganizationID;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SubscriptionEntry> subscriptions = new();
    public AccountSnapshot State {
        get {
            var state = initialized && core != null ? core.State : pausedState;
            return state with {
                IsMutating = busy || Volatile.Read(ref foregroundWaiting) != 0 || cancellingLogin || state.IsMutating, Error = error ?? state.Error,
                IsAddingAccount = state.IsAddingAccount || activeCommand == "add",
                Accounts = state.Accounts.Select(row => {
                    var current = directUsage.TryGetValue(row.Profile.Id, out var entry)
                        ? row with { Usage = entry.Usage ?? row.Usage, UsageError = entry.Error, UsageStatus = entry.Usage != null ? "loaded" : row.Usage != null ? row.UsageStatus : "unavailable" }
                        : row;
                    if ((entry.UnknownCredits || resetNeedsRefresh.ContainsKey(row.Profile.Id)) && current.Usage is { } knownUsage) current = current with { Usage = knownUsage with { ResetCreditsRemaining = null, ResetCreditsExpireAt = null, ResetCards = null } };
                    var hasSubscription = subscriptions.TryGetValue(row.Profile.Id, out var subscription);
                    var status = subscription?.Status ?? (directUsageEnabled ? "checking" : "unverified");
                    var snapshot = subscription?.Snapshot;
                    if (subscription?.ConfirmedFree != true && (!hasSubscription || status == "checking" && snapshot == null))
                        snapshot = row.Subscription?.ActiveUntil > DateTimeOffset.UtcNow ? row.Subscription with { IsVerified = false } : null;
                    if (status != "verified" && snapshot?.ActiveUntil <= DateTimeOffset.UtcNow) snapshot = null;
                    var actualExpiry = status == "verified" && snapshot?.IsVerified == true ? snapshot.ActiveUntil : (DateTimeOffset?)null;
                    return current with {
                        Subscription = snapshot, SubscriptionStatus = status, SubscriptionError = subscription?.Error,
                        PlanType = subscription?.ConfirmedFree == true ? "free" : SubscriptionPlan.Resolve(subscription?.Status == "verified" ? subscription.PlanType : null, entry.ReportedPlan ?? entry.Usage?.PlanType,
                            subscription?.PlanType ?? planHints.GetValueOrDefault(row.Profile.Id) ?? current.Usage?.PlanType ?? row.PlanType, actualExpiry)
                            ?? (entry.Usage?.Windows is { Length: > 0 } ? "free" : null)
                    };
                }).ToArray()
            };
        }
    }
    public event Action? Changed;
    private void Notify() {
        DetectChangedCredentials();
        // An initial Swift cycle may spend a reset credit. Remember that even
        // if auto reset is disabled again before the cycle finishes, so its
        // completion still invalidates an earlier, pre-consumption fast read.
        if (!initialCycleMayReset && core is { } runtime && runtime.State.UsageRefreshRevision <= 1 && runtime.State.Settings.AutoResetEnabled) {
            initialCycleMayReset = true;
            foreach (var receipt in usageRevisions) if (receipt.Value == 1) usageRevisions.TryUpdate(receipt.Key, 0, 1);
        }
        Changed?.Invoke();
        if (!directUsageEnabled || usageLifetime.IsCancellationRequested || busy || Volatile.Read(ref foregroundWaiting) != 0 || cancellingLogin || activeCommand == "add" || core == null || !initialized || core.State.IsMutating || core.State.IsAddingAccount) return;
        // The host owns the only periodic clock. A fresh revision, rather than
        // a second cache-age timer, means its configured interval is respected.
        // Startup reads also cover the first host cycle, which can finish later.
        TaskCompletionSource completion;
        lock (commandStateLock) {
            if (stopping || cancellingLogin || Volatile.Read(ref foregroundWaiting) != 0 || !commands.Wait(0)) return;
            activeCommand = "automaticQuotaRefresh";
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            automaticUsageTask = completion.Task;
            automaticUsageCancellation = CancellationTokenSource.CreateLinkedTokenSource(usageLifetime.Token);
        }
        var handedOff = false;
        try {
            if (usageLifetime.IsCancellationRequested || cancellingLogin || core == null || !initialized || core.State.IsMutating || core.State.IsAddingAccount) return;
            var hostRevision = core.State.UsageRefreshRevision;
            var revision = Math.Max(initialCycleMayReset ? 0 : 1, hostRevision);
            var ids = core.State.Accounts.Select(row => row.Profile.Id).Where(id => !resetNeedsRefresh.ContainsKey(id) &&
                (!directUsage.ContainsKey(id) || !usageRevisions.TryGetValue(id, out var consumed) || consumed < revision)).ToArray();
            if (ids.Length == 0) return;
            handedOff = true;
            _ = ReadAutomaticUsageAsync(ids, revision, Volatile.Read(ref usageGeneration), completion, automaticUsageCancellation.Token, initialFastRead: hostRevision == 0);
        } finally { if (!handedOff) CompleteAutomaticCommand(completion); }
    }
    private async Task ReadAutomaticUsageAsync(Guid[] ids, long revision, int generation, TaskCompletionSource completion, CancellationToken ct, bool initialFastRead) {
        try {
            await Task.WhenAll(ids.Select(id => ReadRevisionAsync(id, revision, generation, initialFastRead: initialFastRead, quiet: true, cancellation: ct)));
        }
        finally {
            CompleteAutomaticCommand(completion);
            if (!usageLifetime.IsCancellationRequested) context.Post(_ => Notify(), null);
        }
    }
    private void CompleteAutomaticCommand(TaskCompletionSource completion) {
        lock (commandStateLock) { automaticUsageCancellation?.Dispose(); automaticUsageCancellation = null; activeCommand = null; commands.Release(); }
        completion.TrySetResult();
    }
    private async Task ReadRevisionAsync(Guid id, long revision, int generation, bool reconcileReset = false, bool initialFastRead = false, bool quiet = false, CancellationToken? cancellation = null) {
        await RefreshDirectUsageAsync(id, generation, reconcileReset: reconcileReset, quiet: quiet, cancellation: cancellation);
        // Failed reads count too: duplicate snapshots must not form a retry loop.
        lock (usageStateLock) { if (generation == usageGeneration) usageRevisions[id] = initialFastRead && initialCycleMayReset ? 0 : revision; }
    }
    private async Task<string?> RefreshDirectUsageAsync(Guid id, int generation, bool afterReset = false, bool reconcileReset = false, bool quiet = false, CancellationToken? cancellation = null) {
        var ct = cancellation ?? usageLifetime.Token;
        var entered = false; var accountEntered = false;
        var accountRead = accountReads.GetOrAdd(id, _ => new(1));
        Task? subscriptionTask = null;
        try {
            await accountRead.WaitAsync(ct); accountEntered = true;
            await usageReads.WaitAsync(ct); entered = true;
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
            if (!quiet) context.Post(_ => { if (!usageLifetime.IsCancellationRequested) Changed?.Invoke(); }, null);
            subscriptionTask = RefreshSubscriptionAsync(id, credential, generation, force: reconcileReset, quiet: quiet, ct);
            string? reportedPlan = null;
            Usage? fresh = null; string? failure = null; var creditsConfirmed = false;
            try { fresh = await usageReader(credential, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (QuotaReadException ex) {
                failure = ex.Message; reportedPlan = ex.PlanType;
                if (QuotaReadException.IsAuthenticationFailure(failure) && failure != QuotaReadException.AuthorizationRevoked && CredentialRefreshPolicy.Owns(Path.Combine(activeHome, "auth.json"), credential))
                    failure = CredentialRefreshPolicy.Deferred;
            }
            catch { failure = "读取额度失败，请检查网络后重试。"; }
            reportedPlan ??= fresh != null ? fresh.PlanType : directUsage.GetValueOrDefault(id).ReportedPlan;
            fresh ??= directUsage.GetValueOrDefault(id).Usage ?? State.Accounts.FirstOrDefault(row => row.Profile.Id == id)?.Usage;
            try {
                var credits = await resetCredits.ReadAsync(credential, ct);
                creditsConfirmed = credits.AvailableCount.HasValue;
                fresh ??= State.Accounts.FirstOrDefault(row => row.Profile.Id == id)?.Usage;
                if (fresh != null) fresh = fresh with { ResetCreditsRemaining = credits.AvailableCount ?? fresh.ResetCreditsRemaining, ResetCreditsExpireAt = credits.EarliestExpiry, ResetCards = credits.Credits };
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failure ??= "额度已读取，但重置次数明细暂时不可用。"; }
            lock (usageStateLock) {
                if (generation == usageGeneration && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(credential.Bytes)) {
                    directUsage[id] = (fresh, failure, DateTimeOffset.UtcNow, afterReset && !creditsConfirmed, reportedPlan);
                    if (creditsConfirmed && (afterReset || reconcileReset)) resetNeedsRefresh.TryRemove(id, out _);
                }
            }
            if (afterReset && !creditsConfirmed) failure ??= "重置次数尚未确认，请刷新后核对。";
            return failure;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch {
            const string message = "读取账号信息失败，请检查登录文件、网络或重新授权。";
            lock (usageStateLock) { if (generation == usageGeneration) directUsage[id] = (null, message, DateTimeOffset.UtcNow, afterReset, null); }
            return message;
        } finally {
            if (subscriptionTask != null) await subscriptionTask;
            if (entered) usageReads.Release();
            if (accountEntered) accountRead.Release();
        }
    }
    private async Task RefreshSubscriptionAsync(Guid id, LoginCredential credential, int generation, bool force, bool quiet, CancellationToken ct) {
        var checkedAt = DateTimeOffset.UtcNow;
        subscriptions.TryGetValue(id, out var previous);
        if (previous != null && !ShouldRefreshSubscription(previous.Status, previous.Snapshot, previous.At, checkedAt, force)) return;
        lock (usageStateLock) {
            if (generation != usageGeneration) return;
            if (!quiet || previous == null) subscriptions[id] = previous?.ConfirmedFree == true ? previous : new(previous?.Snapshot, previous?.PlanType, "checking", null, previous?.At ?? checkedAt);
        }
        SubscriptionEntry next;
        // A failed check may keep a valid snapshot, but never certify it as fresh.
        // Prefer the last server date over the older date embedded in login credentials.
        SubscriptionSnapshot? Retained() => previous?.ConfirmedFree == true ? null : previous?.Snapshot is { } last && last.ActiveUntil > checkedAt ? last
            : credential.SubscriptionActiveUntil is { } until && until > checkedAt
                ? new SubscriptionSnapshot(until, checkedAt, "login_snapshot", false) : null;
        try {
            var result = await subscriptionReader.ReadAsync(credential, ct);
            var snapshot = result.ActiveUntil is { } until ? new SubscriptionSnapshot(until, checkedAt, result.Source, result.IsVerified) : null;
            var retained = Retained();
            next = result.IsConfirmedFree(checkedAt) ? new(null, "free", "verified", null, checkedAt, SubscriptionIdentity(credential)) : snapshot != null ? new(snapshot, result.PlanType, result.IsVerified ? "verified" : "login_snapshot", null, checkedAt)
                : previous?.ConfirmedFree == true && result.PlanType == null ? previous with { Error = "订阅状态暂未刷新，请稍后重试。" }
                : new(retained, result.PlanType ?? previous?.PlanType, retained == null ? "unavailable" : "stale",
                    "官方订阅接口暂未返回有效期，请稍后刷新核验。", checkedAt);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch {
            var retained = Retained();
            next = previous?.ConfirmedFree == true ? previous with { Error = "订阅状态暂未刷新，请稍后重试。" } : new(retained, previous?.PlanType, retained == null ? "unavailable" : "stale",
                "订阅有效期尚未核验，请检查网络或重新授权后刷新。", checkedAt);
        }
        lock (usageStateLock) {
            try {
                var path = CredentialPath(id);
                if (generation == usageGeneration && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(credential.Bytes)) subscriptions[id] = next;
            } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (!quiet) context.Post(_ => { if (!usageLifetime.IsCancellationRequested) Changed?.Invoke(); }, null);
    }
    internal static bool ShouldRefreshSubscription(string status, SubscriptionSnapshot? snapshot,
        DateTimeOffset cachedAt, DateTimeOffset now, bool force) => force || status == "checking" ||
        snapshot is { Source: "login_snapshot" } login && login.ActiveUntil <= now || now - cachedAt >= TimeSpan.FromMinutes(30);
    private int AdvanceUsageGeneration(bool clear = false) {
        lock (usageStateLock) {
            if (clear) { directUsage.Clear(); usageRevisions.Clear(); planHints.Clear(); credentialFingerprints.Clear(); subscriptions.Clear(); }
            return ++usageGeneration;
        }
    }
    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    private void DetectChangedCredentials() {
        if (core == null || !initialized || usageLifetime.IsCancellationRequested) return;
        foreach (var row in core.State.Accounts) {
            var id = row.Profile.Id;
            if (!credentialFingerprints.TryGetValue(id, out var observed)) continue;
            string? current = null; string? identity = null;
            try {
                var path = CredentialPath(id); SecureFiles.RejectLinks(path);
                if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024) { var credential = LoginCredential.Read(path); current = Fingerprint(credential.Bytes); identity = SubscriptionIdentity(credential); }
            } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (current == observed) continue;
            lock (usageStateLock) {
                if (credentialFingerprints.TryGetValue(id, out var latest) && latest == observed) {
                    ++usageGeneration;
                    credentialFingerprints.TryRemove(id, out _); planHints.TryRemove(id, out _); directUsage.TryRemove(id, out _); usageRevisions.TryRemove(id, out _);
                    if (!subscriptions.TryGetValue(id, out var verified) || !verified.ConfirmedFree || verified.Identity != identity) subscriptions.TryRemove(id, out _);
                }
            }
        }
    }
    private string CredentialPath(Guid id) => Path.Combine(dataPath, "accounts", id.ToString().ToUpperInvariant(), "auth.json");
    public string ExportAccount(Guid id, AccountExportFormat format)
    {
        var row = State.Accounts.FirstOrDefault(account => account.Profile.Id == id)
            ?? throw new IOException("此账号已删除，请刷新列表后重试。");
        return AccountExport.Build(LoginCredential.Read(CredentialPath(id)), row, format);
    }
    private static async Task<Usage> ReadOfficialUsageAsync(LoginCredential credential, CancellationToken ct) {
        using var reader = new OfficialUsageReader(); return await reader.ReadAsync(credential, ct);
    }

    public GuardedAccountClient(string hostPath, string dataPath, string? activeHome = null,
        IDesktopOperations? desktop = null, ILoginValidator? validator = null,
        IResetCreditService? resetCredits = null, Func<LoginCredential, CancellationToken, Task<Usage>>? usageReader = null,
        ISubscriptionReader? subscriptionReader = null, Func<string>? authorizationAccentPreference = null)
    {
        this.dataPath = SecureFiles.ResolveStorageRoot(dataPath);
        this.desktop = desktop ?? new DesktopController(ct => DesktopController.DiscoverAsync(null, ct));
        this.validator = validator ?? new OnlineLoginValidator();
        directUsageEnabled = validator == null || resetCredits != null || usageReader != null || subscriptionReader != null;
        this.resetCredits = resetCredits ?? new OfficialResetCredits(); ownsResetCredits = resetCredits == null;
        this.usageReader = usageReader ?? ReadOfficialUsageAsync;
        this.subscriptionReader = subscriptionReader ?? new OfficialSubscriptionReader(); ownsSubscriptionReader = subscriptionReader == null;
        this.activeHome = SecureFiles.ResolveStorageRoot(activeHome ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        runtimeFactory = () => new HostedRuntime(new CoreClient(hostPath, this.dataPath, this.activeHome, this, null, storageRootsResolved: true, nativeAccountApi: directUsageEnabled) {
            AuthorizationAccentPreference = authorizationAccentPreference
        });
    }

    internal GuardedAccountClient(string dataPath, string activeHome, Func<IAccountRuntime> runtimeFactory,
        IResetCreditService resetCredits, Func<LoginCredential, CancellationToken, Task<Usage>> usageReader, ILoginValidator validator,
        ISubscriptionReader? subscriptionReader = null, TimeSpan? loginCancellationTimeout = null, IDesktopOperations? desktop = null)
        : this("", dataPath, activeHome, desktop: desktop, validator: validator, resetCredits: resetCredits, usageReader: usageReader, subscriptionReader: subscriptionReader ?? new EmptySubscriptionReader())
    {
        this.runtimeFactory = runtimeFactory;
        if (loginCancellationTimeout is { } timeout) this.loginCancellationTimeout = timeout;
    }

    public async Task InitializeAsync()
    {
        var runtime = runtimeFactory();
        usageRevisions.Clear(); initialCycleMayReset = false;
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
            directUsage[id] = (account.Usage, null, DateTimeOffset.UtcNow, true, account.PlanType);
            var refreshError = await RefreshDirectUsageAsync(id, generation, afterReset: true);
            if (refreshError != null) error = "重置请求已成功，但最新额度或次数尚未完整读取，请刷新后核对。";
        } catch (Exception ex) {
            if (ex is not ResetCreditException || ((ResetCreditException)ex).OutcomeUncertain) resetNeedsRefresh[id] = 0;
            var knownCount = (ex as ResetCreditException)?.AvailableCount;
            directUsage[id] = (account.Usage is { } prior ? prior with { ResetCreditsRemaining = knownCount, ResetCreditsExpireAt = null, ResetCards = null } : null,
                "请刷新次数和额度核对。", DateTimeOffset.UtcNow, knownCount == null, account.PlanType);
            error = ex is ResetCreditException safe ? safe.Message : hostStopped ? "重置未能完成，请刷新次数和额度核对后再操作。" : "未能停止账号服务，本次未执行重置。请重新打开工具。";
        } finally {
            try { if (hostStopped) await InitializeAsync(); }
            catch { error = (succeeded ? "重置请求已成功。" : "重置操作未完成。") + "账号服务恢复失败，请重新打开本工具后核对额度。"; }
        }
    }

    private Task RefreshAccountsAsync(Guid? accountId, bool quiet) {
        lock (commandStateLock) {
            if (stopping || cancellingLogin || busy || foregroundWaiting != 0 || core == null || !initialized || core.State.IsMutating || core.State.IsAddingAccount)
                return Task.CompletedTask;
            var tasks = State.Accounts.Where(row => accountId == null || row.Profile.Id == accountId).Select(row => {
                var id = row.Profile.Id;
                if (manualReads.TryGetValue(id, out var existing)) return existing;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                manualReads[id] = completion.Task;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(usageLifetime.Token);
                manualReadCancellation[id] = cancellation;
                _ = ReadManualAccountAsync(id, quiet, Volatile.Read(ref usageGeneration), completion, cancellation);
                return completion.Task;
            }).ToArray();
            return Task.WhenAll(tasks);
        }
    }
    private async Task ReadManualAccountAsync(Guid id, bool quiet, int generation, TaskCompletionSource completion, CancellationTokenSource cancellation) {
        // Yield until the request is registered; no UI-wide command lock is held during network I/O.
        await Task.Yield();
        try {
            var runtime = core;
            if (runtime != null && generation == Volatile.Read(ref usageGeneration)) {
                try {
                    await usageReads.WaitAsync(cancellation.Token);
                    try { await runtime.RefreshAccountCredentialAsync(id, cancellation.Token); }
                    finally { usageReads.Release(); }
                }
                catch { /* The direct reader below reports the account-specific failure. */ }
            }
            await ReadRevisionAsync(id, Math.Max(1, core?.State.UsageRefreshRevision ?? 1), generation,
                reconcileReset: true, quiet: quiet, cancellation: cancellation.Token);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally {
            lock (commandStateLock) { manualReads.Remove(id); manualReadCancellation.Remove(id); }
            cancellation.Dispose();
            completion.TrySetResult();
            if (!usageLifetime.IsCancellationRequested) context.Post(_ => Notify(), null);
        }
    }
    private async Task StopManualReadsAsync() {
        Task[] reads;
        lock (commandStateLock) {
            reads = manualReads.Values.ToArray();
            foreach (var cancellation in manualReadCancellation.Values) cancellation.Cancel();
        }
        await Task.WhenAll(reads);
    }
    public async Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null)
    {
        var intervalSetting = command == "quotaRefreshInterval";
        if (intervalSetting && (!int.TryParse(language, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes is < 0 or > 1440))
            throw new IOException("刷新间隔须为 1–1440 的整数分钟；禁用请选择禁用选项。");
        if (directUsageEnabled && command is "refresh" or "refreshQuiet") {
            await RefreshAccountsAsync(accountID, command == "refreshQuiet"); return;
        }
        // Cancellation must reach the login even when its start acknowledgement
        // or another ordinary command is still waiting on the shared transport.
        if (command == "cancelAdd") { await CancelAddingAccountAsync(); return; }
        if (cancellingLogin || !await EnterCommandsAsync(command)) {
            if (intervalSetting) throw new IOException("请等待当前账号操作完成后再保存刷新间隔。");
            return;
        }
        try {
            if (core == null) throw new IOException("Account core is unavailable. Restart the app.");
            if (command is "switch" or "restart" or "remove" or "resetQuota" or "add" or "register") {
                busy = true;
                try { await StopManualReadsAsync(); }
                finally { busy = false; }
            }
            if (command == "remove" && !core.State.Accounts.Any(row => row.Profile.Id == accountID)) return;
            if (command == "add") Notify();
            if (command == "dismissError") error = null;
            if (command == "resetQuota") { if (accountID is { } resetId) await ResetQuotaAsync(resetId); return; }
            if (command == "restart") {
                if (accountID == null || accountID != State.ActiveAccountID || core.State.IsMutating || core.State.IsAddingAccount) return;
                if (QuotaReadException.IsAuthenticationFailure(State.Accounts.FirstOrDefault(row => row.Profile.Id == accountID)?.UsageError))
                    throw new IOException("该账号登录已失效，请先重新授权。");
                busy = true; error = null; AdvanceUsageGeneration(); Notify();
                await desktop.CloseAsync(usageLifetime.Token);
                await desktop.OpenAsync(usageLifetime.Token);
                return;
            }
            if (command != "switch") {
                if (command == "refresh") foreach (var entry in directUsage.Where(x => x.Value.Usage == null).ToArray()) directUsage.TryRemove(entry.Key, out _);
                try { await core.CommandAsync(command, accountID, value, language); }
                catch when (command == "add" && cancellingLogin) { return; }
                if (command == "remove" && accountID is { } removed) { AdvanceUsageGeneration(); directUsage.TryRemove(removed, out _); usageRevisions.TryRemove(removed, out _); planHints.TryRemove(removed, out _); credentialFingerprints.TryRemove(removed, out _); resetNeedsRefresh.TryRemove(removed, out _); subscriptions.TryRemove(removed, out _); }
                return;
            }
            if (accountID == null || core.State.IsMutating || core.State.IsAddingAccount) return;
            DetectChangedCredentials();
            if (QuotaReadException.IsAuthenticationFailure(State.Accounts.FirstOrDefault(row => row.Profile.Id == accountID)?.UsageError)) {
                error = "该账号登录已失效，请先重新授权后再切换。";
                return;
            }
            // Invalidate pending results, not the account information already on
            // screen. Keep it until fresh results arrive after the handoff.
            busy = true; error = null; AdvanceUsageGeneration(); usageRevisions.Clear(); Notify();
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
            transaction?.Dispose(); transaction = null; busy = false; ExitCommands(); Notify();
        }
    }

    private async Task<bool> EnterCommandsAsync(string command)
    {
        Task wait;
        lock (commandStateLock) {
            if (stopping || cancellingLogin || Volatile.Read(ref foregroundWaiting) != 0) return false;
            if (commands.Wait(0)) { activeCommand = command; return true; }
            if (activeCommand != "automaticQuotaRefresh") return false;
            // Ownership and the single foreground reservation change together.
            // No click can observe a held gate without its background owner, and
            // another command cannot steal a gate released to this waiter.
            Volatile.Write(ref foregroundWaiting, 1);
            wait = commands.WaitAsync(usageLifetime.Token);
            if (command is "remove" or "add" or "import" or "switch" or "restart" or "resetQuota") {
                // Invalidate first: even a reader that completes during cancellation cannot publish stale data.
                AdvanceUsageGeneration();
                automaticUsageCancellation?.Cancel();
            }
        }
        Notify();
        try {
            await wait;
            lock (commandStateLock) {
                if (stopping || cancellingLogin) {
                    Volatile.Write(ref foregroundWaiting, 0); commands.Release(); return false;
                }
                activeCommand = command;
                Volatile.Write(ref foregroundWaiting, 0);
            }
            return true;
        } catch (OperationCanceledException) when (usageLifetime.IsCancellationRequested) {
            lock (commandStateLock) { Volatile.Write(ref foregroundWaiting, 0); }
            return false;
        } catch {
            lock (commandStateLock) { Volatile.Write(ref foregroundWaiting, 0); }
            Notify(); throw;
        }
    }
    private void ExitCommands() {
        lock (commandStateLock) {
            activeCommand = null; Volatile.Write(ref foregroundWaiting, 0); commands.Release();
        }
    }

    private async Task CancelAddingAccountAsync()
    {
        if (!await loginCancellation.WaitAsync(0)) return;
        try {
            var runtime = core;
            if (runtime == null || !initialized || (!runtime.State.IsAddingAccount && activeCommand != "add")) return;
            cancellingLogin = true; error = null; Notify();
            try {
                // A command acknowledgement alone is insufficient: cleanup must
                // have published the end of the login before controls are enabled.
                using var timeout = new CancellationTokenSource(loginCancellationTimeout);
                // A just-sent add may not have published its first snapshot yet.
                // Wait for that start, or stop the owned host if the start itself hangs.
                while (!runtime.State.IsAddingAccount && activeCommand == "add") await Task.Delay(20, timeout.Token);
                if (!runtime.State.IsAddingAccount) return;
                await runtime.CommandAsync("cancelAdd").WaitAsync(timeout.Token);
                while (runtime.State.IsAddingAccount || activeCommand == "add") await Task.Delay(20, timeout.Token);
            } catch {
                // A damaged/older host must not strand the UI. Stop only the owned
                // host tree, then reread its registry. Never write the live login.
                if (core != runtime) return;
                await StopCoreAsync();
                pausedState = pausedState with { IsAddingAccount = false, IsMutating = false };
                await commands.WaitAsync();
                try { if (!usageLifetime.IsCancellationRequested) await InitializeAsync(); }
                catch { error = "登录等待已停止，账号服务暂未恢复。请重新打开工具；已有账号和当前登录未被删除。"; }
                finally { commands.Release(); }
            }
        } finally { cancellingLogin = false; loginCancellation.Release(); Notify(); }
    }

    public Task<ImportResult> ImportFilesAsync(string[] paths) => ImportAsync(() => AccountImport.ParseFiles(paths));
    public Task<ImportResult> ImportTextAsync(string json) => ImportAsync(() => AccountImport.ParseText(json));

    private async Task<ImportResult> ImportAsync(Func<LoginCredential[]> parse)
    {
        if (!await EnterCommandsAsync("import")) throw new IOException("请等待当前操作完成。 / Another operation is running.");
        try {
            if (core?.State.IsMutating == true || core?.State.IsAddingAccount == true)
                throw new IOException("请先完成或取消登录操作。 / Finish the current login first.");
            busy = true; error = null; Notify();
            var credentials = parse(); // Fully parse before stopping or writing anything. Pasted text is never saved to a temporary file.
            AdvanceUsageGeneration(clear: true);
            await StopCoreAsync();
            try { return AccountImport.CommitResolved(dataPath, activeHome, credentials); }
            finally { await InitializeAsync(); }
        } finally { busy = false; ExitCommands(); Notify(); }
    }

    public Task CloseAsync(CancellationToken ct) => transaction?.CloseAsync(ct)
        ?? Task.FromException(new IOException("A validated switch is required."));
    public Task OpenAsync(CancellationToken ct) => transaction?.OpenAsync(ct)
        ?? Task.FromException(new IOException("A validated switch is required."));
    public async ValueTask DisposeAsync() {
        Task? automatic;
        lock (commandStateLock) { stopping = true; automatic = automaticUsageTask; }
        usageLifetime.Cancel(); if (automatic != null) await automatic;
        Task[] reads; lock (commandStateLock) reads = manualReads.Values.ToArray();
        await Task.WhenAll(reads);
        // A pending add can own commands until the host stops. Cancel it first;
        // taking that gate before stopping the login would deadlock shutdown.
        await CancelAddingAccountAsync();
        await loginCancellation.WaitAsync();
        try {
            await commands.WaitAsync();
            try { await StopCoreAsync(); }
            finally { commands.Release(); }
        } finally { loginCancellation.Release(); }
        (validator as IDisposable)?.Dispose(); if (ownsResetCredits) (resetCredits as IDisposable)?.Dispose();
        if (ownsSubscriptionReader) (subscriptionReader as IDisposable)?.Dispose();
        commands.Dispose(); loginCancellation.Dispose(); usageReads.Dispose(); usageLifetime.Dispose();
    }
    private sealed class HostedRuntime(CoreClient client) : IAccountRuntime {
        public AccountSnapshot State => client.State;
        public event Action? Changed { add => client.Changed += value; remove => client.Changed -= value; }
        public Task InitializeAsync() => client.InitializeAsync();
        public Task RefreshAccountCredentialAsync(Guid id, CancellationToken ct) => client.RefreshAccountCredentialAsync(id, ct);
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) => client.CommandAsync(command, accountID, value, language);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
    private sealed class EmptySubscriptionReader : ISubscriptionReader {
        public Task<SubscriptionLookup> ReadAsync(LoginCredential credential, CancellationToken ct) => Task.FromResult(new SubscriptionLookup(credential.AccountID, null, null, "unavailable"));
    }
}

internal interface IAccountRuntime : IAccountClient, IAsyncDisposable { Task InitializeAsync(); Task RefreshAccountCredentialAsync(Guid id, CancellationToken ct) => Task.CompletedTask; }
