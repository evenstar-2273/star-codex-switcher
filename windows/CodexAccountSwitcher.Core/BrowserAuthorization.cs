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

    public async Task<LoginCredential> LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct, Func<string>? accentPreference = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var listener = Listen(port);
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
                    var body = Encoding.UTF8.GetBytes(CallbackPage(valid, providerError != null, accentPreference?.Invoke()));
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
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
    internal static string CallbackPage(bool valid, bool declined, string? accentPreference = null)
    {
        var received = valid && !declined;
        var title = received ? "授权信息已收到" : "授权未完成";
        var description = received ? "请返回应用查看账号添加结果。" : "请返回应用，重新发起浏览器授权。";
        // Only predefined colors reach the HTML; settings never become arbitrary CSS.
        var (glow, endGlow, start, middle, end) = accentPreference switch {
            "blue" => ("#1457C5", "#08739E", "#122344", "#173968", "#122e40"),
            "teal" => ("#08765F", "#087B83", "#122e2a", "#174c44", "#15343f"),
            "orange" => ("#A94B12", "#BB441D", "#352219", "#60301e", "#382337"),
            _ => ("#7040CE", "#963C9D", "#201b40", "#39245b", "#192b49")
        };
        return $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{title}} · Star Codex Switcher</title>
              <style>
                * { box-sizing: border-box; }
                body { margin: 0; min-height: 100vh; min-height: 100svh; display: grid; place-items: center; padding: 28px;
                  font-family: "Segoe UI", "Microsoft YaHei", sans-serif; color: #f8f6ff;
                  background: radial-gradient(ellipse at 18% 15%, {{glow}}80, transparent 55%),
                    radial-gradient(ellipse at 88% 85%, {{endGlow}}6b, transparent 55%), linear-gradient(135deg, {{start}}, {{middle}} 55%, {{end}}); }
                main { width: min(100%, 560px); padding: 52px 36px 38px; text-align: center; border: 1px solid #ffffff29;
                  border-radius: 28px; background: #ffffff0d; box-shadow: 0 24px 80px #10092140; }
                .icon { width: 72px; height: 72px; margin: 0 auto 26px; display: grid; place-items: center; border-radius: 50%;
                  color: #c9ffe8; background: #71e9bb18; border: 1px solid #91edc54d; }
                .check { display: block; width: 27px; height: 15px; border-left: 3px solid; border-bottom: 3px solid; transform: translateY(-4px) rotate(-45deg); }
                .warning { color: #ffe0a3; background: #ffcb6618; border-color: #ffcb664d; font-size: 34px; font-weight: 600; }
                .brand { margin: 0 0 14px; font-size: 17px; font-weight: 600; letter-spacing: .5px; color: #f0edf7; }
                h1 { margin: 0; font-size: clamp(26px, 5vw, 34px); line-height: 1.4; font-weight: 650; }
                .description { margin: 18px 0 0; font-size: 17px; line-height: 1.8; color: #ffffffde; }
                .hint { margin: 32px 0 0; padding-top: 24px; border-top: 1px solid #ffffff1a; font-size: 14px; line-height: 1.8; color: #ffffffb8; }
                @media (max-width: 480px) { body { padding: 20px; } main { padding: 40px 22px 28px; border-radius: 24px; } }
              </style>
            </head>
            <body>
              <main aria-labelledby="title">
                <div class="icon{{(received ? "" : " warning")}}" aria-hidden="true">{{(received ? "<span class=\"check\"></span>" : "!")}}</div>
                <p class="brand">Star Codex Switcher</p>
                <h1 id="title">{{title}}</h1>
                <p class="description">{{description}}</p>
                <p class="hint">现在可以关闭此浏览器页面</p>
              </main>
            </body>
            </html>
            """;
    }
    private static TcpListener Listen(int port)
    {
        foreach (var candidate in port == 1455 ? new[] { 1455, 1457 } : new[] { port }) {
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(); return listener; }
            catch (SocketException) { listener.Stop(); listener.Dispose(); }
        }
        throw new IOException("登录回调端口被占用，请先取消其他正在进行的授权，再重试。");
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
