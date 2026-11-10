using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class NativeAuthorizationChecks
{
    private static int assertions;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
    public static async Task<int> RunAsync(string? host)
    {
        try {
            await BrowserAsync();
            if (host != null) await HostAsync(Path.GetFullPath(host));
            Console.WriteLine($"PASS: {assertions} native authorization assertions; no CLI, state/PKCE, cancellation, reuse, reauthorization and local deletion.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Callback(Uri authorization, string? stateOverride = null)
    {
        var query = BrowserAuthorization.Query(authorization.Query);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var result = await http.GetAsync(query["redirect_uri"] + "?code=synthetic-code&state=" + Uri.EscapeDataString(stateOverride ?? query["state"]));
        Check(result.StatusCode == (stateOverride == null ? HttpStatusCode.OK : HttpStatusCode.BadRequest), "Only the matching OAuth state is accepted.");
        var page = await result.Content.ReadAsStringAsync();
        Check(result.Content.Headers.ContentType?.MediaType == "text/html" && page.Contains("<html lang=\"zh-CN\">"), "Callback serves a Chinese HTML page.");
        Check(page.Contains(stateOverride == null ? "授权信息已收到" : "授权未完成") && !page.Contains("synthetic-code") && !page.Contains(query["state"]), "Callback status is accurate without exposing OAuth data.");
        Check(result.Headers.CacheControl?.NoStore == true && result.Headers.Contains("Content-Security-Policy"), "Callback HTML disallows caching and external resources.");
    }
    private static async Task BrowserAsync()
    {
        foreach (var (accent, color) in new[] { ("purple", "#7040CE"), ("blue", "#1457C5"), ("teal", "#08765F"), ("orange", "#A94B12") })
            Check(BrowserAuthorization.CallbackPage(true, false, accent).Contains(color), "Callback uses the chosen theme palette.");
        Check(BrowserAuthorization.CallbackPage(true, true).Contains("授权未完成"), "Declined authorization never displays success.");
        Check(BrowserAuthorization.CallbackPage(true, false, "<script>") == BrowserAuthorization.CallbackPage(true, false), "Unknown theme falls back to purple without injecting settings into HTML.");
        var handler = new TokenHandler();
        using var auth = new BrowserAuthorization(handler, 0);
        Task callback = Task.CompletedTask;
        var credential = await auth.LoginAsync((uri, ct) => {
            handler.Challenge = BrowserAuthorization.Query(uri.Query)["code_challenge"];
            callback = Task.Run(async () => { await Callback(uri, "wrong"); await Callback(uri); });
            return Task.CompletedTask;
        }, CancellationToken.None);
        await callback;
        Check(credential.AccountID == "native-test" && handler.Calls == 1, "Valid callback exchanges one code and reads account identity.");
        using var cancel = new CancellationTokenSource();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Uri? cancelledUrl = null;
        var pending = auth.LoginAsync((uri, ct) => { cancelledUrl = uri; opened.SetResult(); return Task.CompletedTask; }, cancel.Token);
        await opened.Task; cancel.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "Cancelling closes local authorization without a process timeout."); }
        var releasedPort = new Uri(BrowserAuthorization.Query(cancelledUrl!.Query)["redirect_uri"]).Port;
        using var nextListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, releasedPort);
        nextListener.Start();
        Check(true, "The callback port can immediately be reused after cancellation.");
    }
    private static async Task HostAsync(string host)
    {
        var root = Path.Combine(Path.GetTempPath(), "native-account-check-" + Guid.NewGuid());
        var data = Path.Combine(root, "data"); var active = Path.Combine(root, "active");
        var priorCli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Path.Combine(root, "does-not-exist.exe"));
        var autoComplete = false; Uri? opened = null; Task callback = Task.CompletedTask;
        var tokenHandler = new TokenHandler();
        var quota = new QuotaHandler();
        try {
            var desktop = new Desktop();
            await using var client = new CoreClient(host, data, active, desktop, (uri, ct) => {
                opened = uri; tokenHandler.Challenge = BrowserAuthorization.Query(uri.Query)["code_challenge"];
                if (autoComplete) callback = Task.Run(() => Callback(uri));
                return Task.CompletedTask;
            }, storageRootsResolved: true, nativeAccountApi: true) {
                AuthorizationFactory = () => new BrowserAuthorization(tokenHandler, 0),
                NativeUsageFactory = () => new OfficialUsageReader(quota),
                NativeCreditsFactory = () => new OfficialResetCredits(new CreditsHandler())
            };
            await client.InitializeAsync();
            Check(client.State.Error == null && client.State.Accounts.Length == 0, "Empty fresh installation starts without CLI or authentication errors.");
            await client.CommandAsync("quotaRefreshInterval", language: "0");
            await client.CommandAsync("add"); await Until(() => opened != null);
            await client.CommandAsync("cancelAdd").WaitAsync(TimeSpan.FromSeconds(2));
            Check(!client.State.IsAddingAccount && client.State.Error == null && client.State.Accounts.Length == 0, "Native cancellation is acknowledged after listener cleanup.");
            Check(!Directory.GetDirectories(Path.Combine(data, "accounts")).Any(), "Cancelled authorization leaves no staging profile.");
            autoComplete = true; opened = null;
            await client.CommandAsync("add"); await Until(() => !client.State.IsAddingAccount && client.State.Accounts.Length == 1); await callback;
            Check(client.State.Error == null && !File.Exists(Path.Combine(active, "auth.json")), "Authorization saves a profile without changing the active desktop login.");
            var id = client.State.Accounts.Single().Profile.Id;
            await client.CommandAsync("add", id); await Until(() => !client.State.IsAddingAccount); await callback;
            Check(client.State.Accounts.Single().Profile.Id == id && client.State.Error == null, "Reauthorization updates the existing account in place.");
            quota.RejectOnce = true;
            await client.CommandAsync("refresh");
            Check(tokenHandler.RefreshCalls == 1 && client.State.Accounts.Single().Usage != null, "Expired access tokens refresh without a CLI and persist the rotated credential.");
            await client.CommandAsync("switch", id);
            Check(client.State.ActiveAccountID == id && desktop.Opened == 1 && client.State.Error == null, "A fresh unsigned-in desktop switches using the saved native authorization without a CLI.");
            var activeBytes = File.ReadAllBytes(Path.Combine(active, "auth.json"));
            await client.CommandAsync("refresh");
            quota.RejectOnce = true;
            await client.CommandAsync("refresh");
            var savedPath = Path.Combine(data, "accounts", id.ToString().ToUpperInvariant(), "auth.json");
            var renewed = File.ReadAllBytes(savedPath);
            await client.CommandAsync("refresh");
            Check(tokenHandler.RefreshCalls == 2 && File.ReadAllBytes(savedPath).SequenceEqual(renewed) && !renewed.SequenceEqual(activeBytes),
                "Refreshing the active account preserves the renewed credential against the desktop's older local copy.");
            quota.Block = true;
            var refresh = client.CommandAsync("refresh"); await Until(() => quota.Waiting);
            await client.CommandAsync("remove", id).WaitAsync(TimeSpan.FromSeconds(2)); await refresh;
            Check(client.State.Accounts.Length == 0 && client.State.Error == null, "Deletion cancels a blocked native network read and completes locally.");
            Check(!Directory.GetDirectories(Path.Combine(data, "accounts")).Any(), "Local account files are removed after deletion.");
            Check(File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(activeBytes), "Deleting the current saved account preserves the desktop's active login.");
            await client.CommandAsync("refresh");
            Check(client.State.Accounts.Length == 0, "A later refresh cannot restore the deliberately removed active account.");
        } finally {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", priorCli);
            SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class TokenHandler : HttpMessageHandler
    {
        public string? Challenge; public int Calls, RefreshCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var fields = BrowserAuthorization.Query(await request.Content!.ReadAsStringAsync(ct));
            if (fields["grant_type"] == "refresh_token") RefreshCalls++;
            else Check(BrowserAuthorization.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]))) == Challenge, "PKCE verifier matches the browser challenge.");
            using var doc = JsonDocument.Parse(GuardedChecks.Credential("native-test").Bytes);
            return new(HttpStatusCode.OK) { Content = new StringContent(doc.RootElement.GetProperty("tokens").GetRawText()) };
        }
        protected override void Dispose(bool disposing) { }
    }
    private sealed class QuotaHandler : HttpMessageHandler
    {
        public bool Block, Waiting, RejectOnce;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Block) { Waiting = true; await Task.Delay(Timeout.Infinite, ct); }
            if (RejectOnce) { RejectOnce = false; return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }; }
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"plan_type":"free","rate_limit":{"primary_window":{"limit_window_seconds":3024000,"used_percent":10,"reset_after_seconds":3600}}}""") };
        }
        protected override void Dispose(bool disposing) { }
    }
    private sealed class CreditsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"available_count\":0,\"credits\":[]}") });
    }
    private sealed class Desktop : IDesktopOperations
    {
        public int Opened;
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task OpenAsync(CancellationToken ct) { Opened++; return Task.CompletedTask; }
    }
}
