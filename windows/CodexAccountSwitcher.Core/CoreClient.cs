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
    private int nextID;
    public AccountSnapshot State { get; private set; } = AccountSnapshot.Empty;
    public event Action? Changed;

    public CoreClient(string hostPath, string dataPath, string? activeHome = null, IDesktopOperations? desktop = null,
        Func<Uri, CancellationToken, Task>? openBrowser = null)
        : this(hostPath, dataPath, activeHome, desktop, openBrowser, storageRootsResolved: false) { }

    internal CoreClient(string hostPath, string dataPath, string? activeHome, IDesktopOperations? desktop,
        Func<Uri, CancellationToken, Task>? openBrowser, bool storageRootsResolved)
    {
        activeHome ??= Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (!storageRootsResolved) {
            dataPath = SecureFiles.ResolveStorageRoot(dataPath);
            activeHome = SecureFiles.ResolveStorageRoot(activeHome);
        }
        SecureFiles.RejectLinks(dataPath); SecureFiles.RejectLinks(activeHome);
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
    public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null)
        => SendAsync(new { command, accountID, value, language });

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
                        _ = Task.Run(() => HandlePlatformAsync(operation));
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
        string? error = null;
        try {
            switch (message.GetProperty("method").GetString()) {
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
        } catch (Exception ex) { error = ex.Message; }
        try { await WriteAsync(new { command = "platformReply", id = message.GetProperty("id").GetInt32(), error }); }
        catch (Exception) when (lifetime.IsCancellationRequested || process.HasExited) { }
    }

    private static Task OpenBrowserAsync(Uri url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })) { }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try { process.StandardInput.Close(); }
        catch (IOException) when (process.HasExited) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); // A replacement host may start only after this owned tree stops.
        }
        lifetime.Cancel();
        try { await Task.WhenAll(reader, stderr); } catch (OperationCanceledException) { }
        process.Dispose(); lifetime.Dispose(); writeLock.Dispose();
    }
}
