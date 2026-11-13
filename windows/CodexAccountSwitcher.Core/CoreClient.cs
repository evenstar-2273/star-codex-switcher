using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// Thin stdio transport to the exact Swift core used by macOS.
public sealed class CoreClient : IAccountClient, IAsyncDisposable
{
    private readonly Process process;
    private readonly SynchronizationContext context;
    private readonly SemaphoreSlim writeLock = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reader;
    private readonly Task stderr;
    private readonly IDesktopOperations desktop;
    private readonly Func<Uri, CancellationToken, Task> openBrowser;
    private readonly string profilesPath;
    private readonly string activeCredentialPath;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> platformCancellation = new();
    private readonly ConcurrentDictionary<int, Task> platformTasks = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> profileWrites = new();
    private readonly ConcurrentDictionary<int, Guid> nativeReadAccounts = new();
    private readonly ConcurrentDictionary<Guid, byte> deletedAccounts = new();
    internal Func<BrowserAuthorization> AuthorizationFactory { get; set; } = () => new();
    internal Func<string>? AuthorizationAccentPreference { get; init; }
    internal Func<OfficialUsageReader> NativeUsageFactory { get; set; } = () => new();
    internal Func<OfficialResetCredits> NativeCreditsFactory { get; set; } = () => new();
    private int nextID;
    public AccountSnapshot State { get; private set; } = AccountSnapshot.Empty;
    public event Action? Changed;

    public CoreClient(string hostPath, string dataPath, string? activeHome = null, IDesktopOperations? desktop = null,
        Func<Uri, CancellationToken, Task>? openBrowser = null)
        : this(hostPath, dataPath, activeHome, desktop, openBrowser, storageRootsResolved: false) { }

    internal CoreClient(string hostPath, string dataPath, string? activeHome, IDesktopOperations? desktop,
        Func<Uri, CancellationToken, Task>? openBrowser, bool storageRootsResolved, bool nativeAccountApi = false)
    {
        activeHome ??= Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (!storageRootsResolved) {
            dataPath = SecureFiles.ResolveStorageRoot(dataPath);
            activeHome = SecureFiles.ResolveStorageRoot(activeHome);
        }
        SecureFiles.RejectLinks(dataPath); SecureFiles.RejectLinks(activeHome);
        profilesPath = Path.Combine(dataPath, "accounts");
        activeCredentialPath = Path.Combine(activeHome, "auth.json");
        this.desktop = desktop ?? new DesktopController(ct => DesktopController.DiscoverAsync(null, ct));
        this.openBrowser = openBrowser ?? OpenBrowserAsync;
        context = SynchronizationContext.Current ?? new SynchronizationContext();
        var start = new ProcessStartInfo(hostPath) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!
        };
        start.Environment["CODEX_SWITCHER_DATA_HOME"] = dataPath;
        start.Environment["CODEX_SWITCHER_NATIVE_AUTH"] = nativeAccountApi ? "1" : "0";
        if (activeHome != null) start.Environment["CODEX_HOME"] = activeHome;
        start.Environment["PATH"] = Path.GetDirectoryName(hostPath) + ";" + Environment.GetEnvironmentVariable("PATH");
        process = Process.Start(start) ?? throw new IOException("The shared account core could not start.");
        stderr = Task.Run(async () => {
            var buffer = new char[2048];
            while (await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token) > 0) { }
        });
        reader = ReadAsync();
    }

    public Task InitializeAsync() => SendAsync(new { command = "initialize" });
    public async Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null)
    {
        SemaphoreSlim? gate = null;
        if (command == "remove" && accountID is { } removed) {
            gate = profileWrites.GetOrAdd(removed, _ => new(1));
            await gate.WaitAsync(lifetime.Token);
            deletedAccounts[removed] = 0;
            foreach (var read in nativeReadAccounts.Where(pair => pair.Value == removed))
                if (platformCancellation.TryGetValue(read.Key, out var cancellation)) {
                    try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                }
        }
        try { await SendAsync(new { command, accountID, value, language }); }
        finally {
            if (gate != null) {
                if (State.Accounts.Any(row => row.Profile.Id == accountID)) deletedAccounts.TryRemove(accountID!.Value, out _);
                gate.Release();
            }
        }
    }

    private async Task SendAsync(object command)
    {
        var id = Interlocked.Increment(ref nextID);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try {
            var message = JsonSerializer.SerializeToNode(command)!.AsObject();
            message["id"] = id;
            await WriteAsync(message);
            await completion.Task;
        } finally { pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object message)
    {
        await writeLock.WaitAsync(lifetime.Token);
        try {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync(lifetime.Token);
        } finally { writeLock.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("The shared account core stopped.");
        try {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line) {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                switch (message.GetProperty("kind").GetString()) {
                    case "snapshot":
                        var state = message.GetProperty("state").Deserialize<AccountSnapshot>(AccountSnapshot.JsonOptions)
                            ?? throw new IOException("Invalid account state.");
                        State = state; // Publish before completing the command; only UI notification needs dispatch.
                        context.Post(_ => Changed?.Invoke(), null);
                        break;
                    case "completed":
                        if (pending.TryRemove(message.GetProperty("id").GetInt32(), out var completion)) {
                            if (message.TryGetProperty("error", out var error)) completion.TrySetException(new IOException(error.GetString()));
                            else completion.TrySetResult();
                        }
                        break;
                    case "platform":
                        // Shell launch may block inside Windows. It must never block reading
                        // cancellation acknowledgements and updated account snapshots.
                        var operation = message.Clone();
                        platformCancellation[message.GetProperty("id").GetInt32()] = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var operationId = message.GetProperty("id").GetInt32();
                        var operationTask = Task.Run(() => HandlePlatformAsync(operation));
                        platformTasks[operationId] = operationTask;
                        _ = operationTask.ContinueWith(_ => platformTasks.TryRemove(operationId, out var completed), TaskScheduler.Default);
                        break;
                    case "platformCancel":
                        if (platformCancellation.TryGetValue(message.GetProperty("id").GetInt32(), out var cancellation)) {
                            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                        }
                        break;
                    case "protocolError": throw new IOException("The shared core protocol failed.");
                }
            }
        } catch (Exception ex) { failure = ex; }
        finally {
            foreach (var completion in pending.Values) completion.TrySetException(failure);
            if (!lifetime.IsCancellationRequested)
                context.Post(_ => { State = State with { Error = failure.Message }; Changed?.Invoke(); }, null);
        }
    }

    private async Task HandlePlatformAsync(JsonElement message)
    {
        var id = message.GetProperty("id").GetInt32();
        var cancellation = platformCancellation[id];
        string? error = null;
        string? result = null;
        try {
            switch (message.GetProperty("method").GetString()) {
                case "authorizeAccount":
                    var path = ProfilePath(message);
                    using (var authorization = AuthorizationFactory()) {
                        var credential = await authorization.LoginAsync(openBrowser, cancellation.Token, AuthorizationAccentPreference);
                        cancellation.Token.ThrowIfCancellationRequested();
                        SecureFiles.AtomicWrite(Path.Combine(path, "auth.json"), credential.Bytes);
                    }
                    break;
                case "readAccountUsage":
                    var credentialPath = Path.Combine(ProfilePath(message), "auth.json");
                    var profileId = Guid.Parse(Path.GetFileName(Path.GetDirectoryName(credentialPath)!));
                    nativeReadAccounts[id] = profileId;
                    if (deletedAccounts.ContainsKey(profileId)) throw new IOException("该账号已删除。");
                    var login = LoginCredential.Read(credentialPath);
                    using (var usageReader = NativeUsageFactory())
                    using (var creditReader = NativeCreditsFactory()) {
                        var nativeResult = await ReadNativeUsageAsync(profileId, credentialPath, usageReader, cancellation.Token);
                        login = nativeResult.Login;
                        var usage = nativeResult.Usage;
                        ResetCreditSnapshot? credits = null;
                        try { credits = await creditReader.ReadAsync(login, cancellation.Token); }
                        catch (ResetCreditException) { }
                        var longest = usage.DisplayWindows().OrderByDescending(w => w.Seconds).FirstOrDefault();
                        var remaining = usage.RemainingPercent ?? longest?.RemainingPercent;
                        var resets = usage.ResetsAt ?? longest?.ResetsAt;
                        if (remaining == null || resets == null) throw new IOException("服务暂未返回可识别的额度窗口，请稍后刷新。");
                        result = JsonSerializer.Serialize(new {
                            remainingPercent = remaining, resetsAt = WireDate(resets),
                            fiveHourRemainingPercent = usage.FiveHourRemainingPercent, fiveHourResetsAt = WireDate(usage.FiveHourResetsAt),
                            resetCreditsRemaining = credits?.AvailableCount,
                            resetCredits = credits?.Credits?.Select(c => new { id = c.Id, status = c.Status, resetType = c.ResetType, expiresAt = WireDate(c.ExpiresAt) })
                        });
                    }
                    break;
                case "consumeAccountCredit":
                    var target = LoginCredential.Read(Path.Combine(ProfilePath(message), "auth.json"));
                    using (var credits = NativeCreditsFactory())
                        await credits.ConsumeAsync(target, message.GetProperty("creditID").GetString(), message.GetProperty("requestID").GetString()!, cancellation.Token);
                    break;
                case "openBrowser":
                    var url = new Uri(message.GetProperty("url").GetString()!);
                    if (url.Scheme is not ("https" or "http")) throw new IOException("Invalid sign-in address.");
                    await openBrowser(url, lifetime.Token);
                    break;
                case "closeDesktop":
                    await desktop.CloseAsync(lifetime.Token);
                    break;
                case "reopenDesktop":
                    await desktop.OpenAsync(lifetime.Token);
                    break;
                default: throw new IOException("Unknown platform operation.");
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { error = "已取消授权。"; }
        catch (Exception ex) { error = ex.Message; }
        finally { platformCancellation.TryRemove(id, out _); nativeReadAccounts.TryRemove(id, out _); }
        try { await WriteAsync(new { command = "platformReply", id = message.GetProperty("id").GetInt32(), error, result }); }
        catch (Exception) when (lifetime.IsCancellationRequested || process.HasExited) { }
        finally { cancellation.Dispose(); }
    }

    private static string? WireDate(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> nativeUsageGates = new();
    internal async Task RefreshAccountCredentialAsync(Guid id, CancellationToken ct) {
        using var reader = NativeUsageFactory();
        await ReadNativeUsageAsync(id, Path.Combine(profilesPath, id.ToString().ToUpperInvariant(), "auth.json"), reader, ct);
    }
    private async Task<(LoginCredential Login, Usage Usage)> ReadNativeUsageAsync(Guid profileId, string credentialPath, OfficialUsageReader usageReader, CancellationToken ct) {
        var readGate = nativeUsageGates.GetOrAdd(profileId, _ => new(1));
        await readGate.WaitAsync(ct);
        try {
            if (deletedAccounts.ContainsKey(profileId)) throw new IOException("该账号已删除。");
            var login = LoginCredential.Read(credentialPath);
            login = await SyncOfficialCredentialAsync(profileId, credentialPath, login, ct);
            Usage usage;
            try { usage = await usageReader.ReadAsync(login, ct); }
            catch (QuotaReadException ex) when (QuotaReadException.IsAuthenticationFailure(ex.Message) && ex.Message != QuotaReadException.AuthorizationRevoked) {
                // The default desktop/CLI owns a refresh chain still present in its auth store.
                // Never rotate that chain behind its back, even when the desktop is currently closed.
                if (CredentialRefreshPolicy.Owns(activeCredentialPath, login)) throw new CredentialRenewalDeferredException();
                using var authorization = AuthorizationFactory();
                // Token rotation must finish even if a read is cancelled; persist only to a still-live, unchanged profile.
                var refreshed = await authorization.RefreshAsync(login, lifetime.Token);
                var writeGate = profileWrites.GetOrAdd(profileId, _ => new(1));
                await writeGate.WaitAsync(lifetime.Token);
                try {
                    if (deletedAccounts.ContainsKey(profileId) || !File.Exists(credentialPath) || !LoginCredential.Read(credentialPath).Bytes.SequenceEqual(login.Bytes))
                        throw new IOException("账号凭据已变化，已停止写回，请刷新重试。");
                    PersistRefreshedCredential(credentialPath, login, refreshed);
                } finally { writeGate.Release(); }
                login = refreshed;
                usage = await usageReader.ReadAsync(login, ct);
            }

            return (login, usage);
        } finally { readGate.Release(); }
    }
    private async Task<LoginCredential> SyncOfficialCredentialAsync(Guid id, string path, LoginCredential saved, CancellationToken ct) {
        if (!File.Exists(activeCredentialPath)) return saved;
        var live = LoginCredential.Read(activeCredentialPath);
        if (!CredentialRefreshPolicy.SameIdentity(live, saved) || live.Bytes.SequenceEqual(saved.Bytes)) return saved;
        var markerPath = Path.Combine(Path.GetDirectoryName(path)!, "reauthorization-live.json");
        SecureFiles.RejectLinks(markerPath);
        if (File.Exists(markerPath)) {
            static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var markers = JsonSerializer.Deserialize<CredentialMarker[]>(File.ReadAllBytes(markerPath), AccountSnapshot.JsonOptions) ?? [];
            if (markers.Any(marker => marker.PreviousLive == Hash(live.Bytes) && marker.SavedCredential == Hash(saved.Bytes))) return saved;
        }
        var gate = profileWrites.GetOrAdd(id, _ => new(1));
        await gate.WaitAsync(ct);
        try {
            if (deletedAccounts.ContainsKey(id) || !File.Exists(path) || !LoginCredential.Read(path).Bytes.SequenceEqual(saved.Bytes))
                throw new IOException("账号凭据已变化，请稍后刷新。");
            if (!File.Exists(activeCredentialPath) || !LoginCredential.Read(activeCredentialPath).Bytes.SequenceEqual(live.Bytes))
                throw new IOException("Codex 正在更新登录凭据，请稍后刷新。");
            SecureFiles.AtomicWrite(path, live.Bytes);
            return live;
        } finally { gate.Release(); }
    }
    private void PersistRefreshedCredential(string path, LoginCredential previous, LoginCredential refreshed)
    {
        if (File.Exists(activeCredentialPath)) {
            var live = LoginCredential.Read(activeCredentialPath);
            if (live.AccountID == refreshed.AccountID) {
                static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                var markerPath = Path.Combine(Path.GetDirectoryName(path)!, "reauthorization-live.json");
                SecureFiles.RejectLinks(markerPath);
                var liveHash = Hash(live.Bytes); var oldHash = Hash(previous.Bytes);
                var markers = new List<CredentialMarker>();
                if (File.Exists(markerPath)) {
                    var saved = JsonSerializer.Deserialize<CredentialMarker[]>(File.ReadAllBytes(markerPath), AccountSnapshot.JsonOptions) ?? [];
                    markers.AddRange(saved.Where(m => m.PreviousLive == liveHash && m.SavedCredential == oldHash).Take(1));
                }
                markers.Add(new(liveHash, Hash(refreshed.Bytes)));
                SecureFiles.AtomicWrite(markerPath, JsonSerializer.SerializeToUtf8Bytes(markers, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            }
        }
        SecureFiles.AtomicWrite(path, refreshed.Bytes);
    }
    private sealed record CredentialMarker(string PreviousLive, string SavedCredential);
    private string ProfilePath(JsonElement message)
    {
        var profile = new Uri(message.GetProperty("url").GetString()!);
        if (!profile.IsFile) throw new IOException("账号目录无效。");
        var path = Path.GetFullPath(profile.LocalPath).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(path), profilesPath, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(path), out _))
            throw new IOException("账号目录无效。");
        SecureFiles.RejectLinks(path);
        return path;
    }

    private static Task OpenBrowserAsync(Uri url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })) { }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cancellation in platformCancellation.Values) {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        try { process.StandardInput.Close(); }
        catch (IOException) when (process.HasExited) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); // A replacement host may start only after this owned tree stops.
        }
        lifetime.Cancel();
        try { await Task.WhenAll(reader, stderr); } catch (OperationCanceledException) { }
        await Task.WhenAll(platformTasks.Values);
        process.Dispose(); lifetime.Dispose(); writeLock.Dispose();
    }
}
