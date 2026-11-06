using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher;

internal static class UpdateHttpChecks
{
    private static readonly string Root = "https://github.com/" + Distribution.Repository + "/releases/";
    private static readonly Uri Feed = new(Root + "latest/download/update.json");
    private static readonly Uri Tagged = new(Root + "download/v0.1.27.4/update.json");
    private static readonly Uri Asset = new("https://release-assets.githubusercontent.com/github-production-release-asset/123/feed?sig=synthetic");
    private const string Hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    public static async Task RunAsync()
    {
        await RedirectChecksAsync();
        await FeedChecksAsync();
        ParseChecks();
        Console.WriteLine("PASS: GitHub update redirects, allowlist, redirect limit/cancellation, bounded feed bodies, package type/hash/platform/version/path validation; synthetic HTTP only.");
    }

    private static async Task RedirectChecksAsync()
    {
        var seen = new List<Uri>();
        using (var client = Client((request, _) => {
            Check(request.Method == HttpMethod.Get && request.Headers.Authorization == null && !request.Headers.Contains("Cookie"), "Update requests must be GET without account credentials.");
            seen.Add(request.RequestUri!);
            return Task.FromResult(seen.Count switch {
                1 => Redirect(new Uri("/" + Distribution.Repository + "/releases/download/v0.1.27.4/update.json", UriKind.Relative)),
                2 => Redirect(Asset),
                _ => Ok("payload")
            });
        })) {
            using var response = await UpdateHttp.GetAsync(client, Feed);
            Check(await response.Content.ReadAsStringAsync() == "payload", "Final asset response must reach the caller.");
            Check(seen.Count == 3 && seen[0] == Feed && seen[1] == Tagged && seen[2] == Asset, "Latest -> tag -> signed GitHub asset chain is preserved.");
        }

        foreach (var host in new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" }) {
            var calls = 0;
            using var client = Client((_, _) => Task.FromResult(++calls == 1 ? Redirect(new Uri("https://" + host + "/asset?sig=synthetic")) : Ok("ok")));
            using var response = await UpdateHttp.GetAsync(client, Tagged);
            Check(calls == 2 && response.IsSuccessStatusCode, "Recognized GitHub asset host must work: " + host);
        }
        foreach (var address in new[] {
            "https://example.test/asset.exe", "http://release-assets.githubusercontent.com/asset.exe",
            "https://release-assets.githubusercontent.com.example.test/asset.exe",
            "https://github.com/another/repository/releases/download/v1/asset.exe",
            "https://github.com/" + Distribution.Repository + "-other/releases/download/v1/asset.exe",
            "https://github.com/" + Distribution.Repository + "/issues/1",
            "https://user:password@release-assets.githubusercontent.com/asset.exe",
            "https://release-assets.githubusercontent.com:444/asset.exe",
            "https://release-assets.githubusercontent.com/asset.exe#fragment"
        }) {
            var calls = 0;
            using var client = Client((_, _) => { calls++; return Task.FromResult(Redirect(new Uri(address))); });
            await RejectAsync<IOException>(() => UpdateHttp.GetAsync(client, Tagged), "Unsafe redirect must be rejected: " + address);
            Check(calls == 1, "Unsafe redirect destination must never be requested.");
        }
        foreach (var address in new[] { "https://example.test/file", "http://github.com/" + Distribution.Repository + "/releases/download/v1/a.exe", Asset.AbsoluteUri }) {
            var calls = 0;
            using var client = Client((_, _) => { calls++; return Task.FromResult(Ok("unexpected")); });
            await RejectAsync<IOException>(() => UpdateHttp.GetAsync(client, new Uri(address)), "Initial request must be scoped to repository.");
            Check(calls == 0, "Invalid initial URL must not be sent.");
        }
        var loopCalls = 0;
        using (var client = Client((_, _) => { loopCalls++; return Task.FromResult(Redirect(Tagged)); })) {
            await RejectAsync<IOException>(() => UpdateHttp.GetAsync(client, Feed), "Redirect loop must be bounded.");
            Check(loopCalls == 6, "Allow at most five redirect hops.");
        }
        using (var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))))
            await RejectAsync<IOException>(() => UpdateHttp.GetAsync(client, Feed), "Missing Location must fail.");
        using (var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))) {
            using var response = await UpdateHttp.GetAsync(client, Feed);
            Check(response.StatusCode == HttpStatusCode.NotFound, "Non-redirect HTTP failure is returned to caller.");
        }
        using (var client = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Ok("unexpected"); })) {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
            await RejectAsync<OperationCanceledException>(() => UpdateHttp.GetAsync(client, Feed, cancel.Token), "Cancellation reaches HTTP handler.");
        }
    }

    private static async Task FeedChecksAsync()
    {
        var feed = Json(Distribution.PackageType);
        using (var client = Client((_, _) => Task.FromResult(Ok(feed)))) {
            var release = await UpdateFeed.FetchAsync(client, Feed);
            Check(release.Version == new Version(0, 1, 27, 4), "Valid feed parses through HTTP.");
        }
        using (var client = Client((_, _) => {
            var response = Ok(feed); response.Content.Headers.ContentLength = 65537; return Task.FromResult(response);
        })) await RejectAsync<FormatException>(() => UpdateFeed.FetchAsync(client, Feed), "Declared oversized body must fail.");
        using (var client = Client((_, _) => Task.FromResult(Streaming(new byte[65537]))))
            await RejectAsync<FormatException>(() => UpdateFeed.FetchAsync(client, Feed), "Unknown-length oversized streamed body must fail.");
        var exact = Encoding.UTF8.GetBytes(feed.PadRight(65536, ' '));
        using (var client = Client((_, _) => Task.FromResult(Streaming(exact)))) {
            var release = await UpdateFeed.FetchAsync(client, Feed);
            Check(release.Version == new Version(0, 1, 27, 4), "A complete 64 KiB feed remains valid.");
        }
        using (var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))))
            await RejectAsync<HttpRequestException>(() => UpdateFeed.FetchAsync(client, Feed), "Feed HTTP failure must not parse as success.");
        using (var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) }))) {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
            await RejectAsync<OperationCanceledException>(() => UpdateFeed.FetchAsync(client, Feed, cancel.Token), "Cancellation must interrupt a body that hangs after response headers.");
        }
    }

    private static void ParseChecks()
    {
        foreach (var type in new[] { "portable", "installer" }) {
            var release = UpdateFeed.Parse(Json(type), type);
            Check(release.PackageType == type && release.Sha256 == Hash.ToLowerInvariant(), "Accept matching package type and normalize checksum.");
            Check(type != "installer" || release.AppSha256 == Hash.ToLowerInvariant(), "Installer app checksum is required and normalized.");
        }
        Reject<FormatException>(() => UpdateFeed.Parse(Json("portable"), "installer"), "Portable feed cannot update installer.");
        Reject<FormatException>(() => UpdateFeed.Parse(Json("installer"), "portable"), "Installer feed cannot update portable.");
        foreach (var hash in new[] { "", new string('0', 63), new string('0', 65), new string('z', 64) }) {
            Reject<FormatException>(() => UpdateFeed.Parse(Json("portable", hash: hash), "portable"), "Reject invalid setup checksum.");
            Reject<FormatException>(() => UpdateFeed.Parse(Json("installer", appHash: hash), "installer"), "Reject invalid installed-app checksum.");
        }
        foreach (var version in new[] { "1.2.3", "01.2.3.4", "1.2.3.-1", "1.2.3.4-beta", "2147483648.1.1.1" })
            Reject<FormatException>(() => UpdateFeed.Parse(Json("portable", version: version), "portable"), "Reject invalid version.");
        Reject<FormatException>(() => UpdateFeed.Parse(Json("portable", platform: "windows-arm64"), "portable"), "Reject incompatible platform.");
        foreach (var path in new[] {
            Root + "download/", Root + "download/v1/", Root + "download/v1/a/b.exe", Root + "latest/download/",
            Root + "latest/download/a/b.exe", Root + "download/v1/a%2fb.exe", Root + "download/v1/a%5cb.exe",
            Root + "download/v1/a%00b.exe", "https://github.com/another/repository/releases/download/v1/a.exe",
            Root.Replace("https:", "http:") + "download/v1/a.exe", Root + "download/v1/a.exe#fragment",
            Root.Replace("github.com", "github.com:444") + "download/v1/a.exe"
        }) {
            Check(!UpdateHttp.IsRepositoryAsset(new Uri(path)), "Unsafe or incomplete release path cannot pass allowlist: " + path);
            Reject<FormatException>(() => UpdateFeed.Parse(Json("portable", download: path), "portable"), "Reject unsafe package location.");
        }
    }

    private static string Json(string type, string? download = null, string hash = Hash, string appHash = Hash,
        string version = "0.1.27.4", string platform = "windows-x64") => JsonSerializer.Serialize(new {
            version, platform, package_type = type, download_url = download ?? Root + "download/v0.1.27.4/setup.exe",
            sha256 = hash, app_sha256 = appHash, notes = "synthetic fixture"
        });
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Streaming(byte[] body) => new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekingStream(body)) };
    private static HttpResponseMessage Redirect(Uri location) { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = location; return response; }
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new SyntheticHandler(send));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception {
        try { action(); } catch (T) { return; } throw new Exception(message);
    }
    private static async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception {
        try { await action(); } catch (T) { return; } throw new Exception(message);
    }
    private sealed class SyntheticHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes) {
        public override bool CanSeek => false;
    }
    private sealed class HangingStream : MemoryStream {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
