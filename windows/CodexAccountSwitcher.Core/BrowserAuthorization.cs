using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountSwitcher.Core;

// The authorization listener and token exchange belong to this app, not a CLI child process.
internal sealed class BrowserAuthorization(HttpMessageHandler? handler = null, int port = 1455) : IDisposable
{
    private readonly HttpClient http = new(handler ?? WindowsHttp.Handler()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length == 2 ? Uri.UnescapeDataString(part[1].Replace('+', ' ')) : "");

    public async Task<LoginCredential> LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(); }
        catch (SocketException) { throw new IOException("登录回调端口被占用，请先取消其他正在进行的授权，再重试。"); }
        var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://localhost:{actualPort}/auth/callback";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var fields = new Dictionary<string, string> {
            ["response_type"] = "code", ["client_id"] = "app_EMoamEEZ73f0CkXaXp7hrann",
            ["redirect_uri"] = redirect, ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["state"] = state,
            ["id_token_add_organizations"] = "true", ["codex_cli_simplified_flow"] = "true", ["originator"] = "codex_cli_rs"
        };
        try {
            await openBrowser(new Uri("https://auth.openai.com/oauth/authorize?" + string.Join('&', fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)))), token);
            while (true) {
                using var connection = await listener.AcceptTcpClientAsync(token);
                using var stream = connection.GetStream();
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                string? code = null; string? providerError = null;
                try {
                    var bytes = new List<byte>(); var buffer = new byte[1];
                    while (bytes.Count < 8192) {
                        if (await stream.ReadAsync(buffer, requestTimeout.Token) == 0) break;
                        bytes.Add(buffer[0]);
                        if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] {13, 10, 13, 10})) break;
                    }
                    var first = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")[0].Split(' ');
                    if (first.Length == 3 && first[0] == "GET" && Uri.TryCreate("http://localhost" + first[1], UriKind.Absolute, out var uri) && uri.AbsolutePath == "/auth/callback") {
                        var query = Query(uri.Query);
                        if (query.GetValueOrDefault("state") == state) { code = query.GetValueOrDefault("code"); providerError = query.GetValueOrDefault("error"); }
                    }
                    var valid = !string.IsNullOrEmpty(code) || providerError != null;
                    var body = Encoding.UTF8.GetBytes(valid ? "授权回调已收到，请返回 Star Codex Switcher 查看结果。" : "无效的授权回调，请返回应用重新授权。");
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
                    await stream.WriteAsync(header, requestTimeout.Token); await stream.WriteAsync(body, requestTimeout.Token);
                } catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or OperationCanceledException or ArgumentException) { continue; }
                if (providerError != null) throw new IOException("授权未完成或已被拒绝，请重新授权。");
                if (string.IsNullOrEmpty(code)) continue;
                using var response = await http.PostAsync("https://auth.openai.com/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["grant_type"] = "authorization_code", ["client_id"] = fields["client_id"], ["code"] = code,
                    ["redirect_uri"] = redirect, ["code_verifier"] = verifier
                }), token);
                if (!response.IsSuccessStatusCode) throw new IOException($"授权凭据交换失败（HTTP {(int)response.StatusCode}），请重新授权。");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                using var export = JsonDocument.Parse(JsonSerializer.Serialize(new { tokens = json.RootElement, last_refresh = DateTimeOffset.UtcNow.ToString("O") }));
                var credential = LoginCredential.FromExport(export.RootElement);
                token.ThrowIfCancellationRequested();
                return credential;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new IOException("授权等待超时，请重新授权。"); }
        catch (HttpRequestException) { throw new IOException("授权网络连接失败，请检查网络或代理后重试。"); }
        catch (JsonException) { throw new IOException("授权服务返回的数据无法识别，请重新授权。"); }
        finally { listener.Stop(); }
    }
    internal async Task<LoginCredential> RefreshAsync(LoginCredential previous, CancellationToken ct)
    {
        var saved = JsonNode.Parse(previous.Bytes)!.AsObject();
        var tokens = saved["tokens"]!.AsObject();
        var refresh = tokens["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(refresh)) throw new IOException("登录凭据已失效，请重新授权。");
        try {
            using var response = await http.PostAsync("https://auth.openai.com/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"] = "refresh_token", ["client_id"] = "app_EMoamEEZ73f0CkXaXp7hrann", ["refresh_token"] = refresh
            }), ct);
            if (!response.IsSuccessStatusCode) {
                if ((int)response.StatusCode is 400 or 401 or 403) throw new IOException("登录凭据已被服务端撤销，请重新授权。 (401)");
                throw new IOException($"登录凭据刷新失败（HTTP {(int)response.StatusCode}），请稍后重试。");
            }
            var updated = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
            if (updated["access_token"]?.GetValue<string>() is not { Length: > 0 }) throw new JsonException();
            foreach (var key in new[] { "access_token", "refresh_token", "id_token" })
                if (updated[key]?.GetValue<string>() is { Length: > 0 } value) tokens[key] = value;
            saved["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");
            var credential = LoginCredential.Parse(Encoding.UTF8.GetBytes(saved.ToJsonString()));
            if (credential.AccountID != previous.AccountID) throw new IOException("刷新后的账号身份不一致，请重新授权。");
            return credential;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { throw new IOException("登录凭据刷新连接失败，请检查网络或代理后重试。"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new IOException("登录凭据刷新响应无法识别，请重新授权。"); }
    }
    public void Dispose() => http.Dispose();
}
