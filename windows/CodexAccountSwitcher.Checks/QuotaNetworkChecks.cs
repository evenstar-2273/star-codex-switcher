using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class QuotaNetworkChecks
{
    private static int assertions;
    private const string Secret = "synthetic-private-value-never-display";
    private static readonly LoginCredential Credential = new("quota-test", "quota@example.test", Secret, []);
    private const string GoodBody = """{"rate_limit":{"primary_window":{"limit_window_seconds":604800,"used_percent":37,"reset_after_seconds":3600}}} """;

    public static async Task<int> RunAsync()
    {
        try {
            foreach (var (status, expected) in new[] {
                (HttpStatusCode.Unauthorized, QuotaReadException.Unauthorized),
                (HttpStatusCode.Forbidden, QuotaReadException.Forbidden),
                (HttpStatusCode.ProxyAuthenticationRequired, QuotaReadException.ProxyAuthentication),
                (HttpStatusCode.TooManyRequests, QuotaReadException.RateLimited),
                (HttpStatusCode.BadGateway, QuotaReadException.ServiceUnavailable),
                (HttpStatusCode.ServiceUnavailable, QuotaReadException.ServiceUnavailable),
                (HttpStatusCode.BadRequest, QuotaReadException.RequestFailed)
            }) await Failure(new Handler(status, Secret), expected);

            foreach (var code in new[] { "refresh_token_invalidated", "token_invalidated", "token_revoked" }) {
                var response = JsonSerializer.Serialize(new { error = new { code, message = Secret } });
                await Failure(new Handler(HttpStatusCode.Unauthorized, response), QuotaReadException.AuthorizationRevoked);
            }
            foreach (var body in new[] {
                """{"code":"token_revoked"}""",
                """{"error":"refresh_token_invalidated"}""",
                """{"error":{"type":"token_invalidated"}}""",
                """{"error":{"error_code":"token_revoked"}}""",
                """{"code":"invalid_grant","error":{"code":"token_revoked"}}""",
                JsonSerializer.Serialize(new { error = new { message = "Your authentication token has been invalidated. " + Secret } }),
                JsonSerializer.Serialize(new { message = "The refresh token has been revoked. " + Secret })
            }) await Failure(new Handler(HttpStatusCode.Unauthorized, body), QuotaReadException.AuthorizationRevoked);

            foreach (var body in new[] {
                """{"error":{"code":"invalid_grant","message":"Invalid authentication credentials."}}""",
                """{"error":{"code":"token_expired"}}""",
                """{"error":{"code":"refresh_token_reused","message":"Your authentication token has been invalidated."}}""",
                """{"error":{"code":"refresh_token_expired","message":"Your authentication token has been invalidated."}}""",
                """{"error":{"code":"invalid_grant","message":"Your authentication token has been invalidated."}}""",
                """{"code":"refresh_token_reused","error":{"message":"Your authentication token has been invalidated."}}""",
                """{"message":"Your authentication token has been invalidated.","error":{"code":"refresh_token_expired"}}""",
                """{"error":{"code":401,"message":null}}""",
                """{"error":{"message":"The local cache was invalidated."}}""",
                """{"unrelated":{"code":"token_revoked"}}""",
                """{"error":{"code":"token_revoked_extra"}}""",
                """[{"code":"token_revoked"}]""",
                """{"error":{"code":"token_revoked"},""",
                "<html>Your authentication token has been invalidated. " + Secret + "</html>",
                JsonSerializer.Serialize(new { error = new { code = "token_revoked", message = new string('x', 20 * 1024) + Secret } }),
                "{\"error\":{\"code\":\"token_revoked\"},\"nested\":" + new string('[', 17) + "0" + new string(']', 17) + "}"
            }) await Failure(new Handler(HttpStatusCode.Unauthorized, body), QuotaReadException.Unauthorized);

            foreach (var (status, expected) in new[] {
                (HttpStatusCode.Forbidden, QuotaReadException.Forbidden),
                (HttpStatusCode.ProxyAuthenticationRequired, QuotaReadException.ProxyAuthentication),
                (HttpStatusCode.BadRequest, QuotaReadException.RequestFailed)
            }) await Failure(new Handler(status, """{"error":{"code":"token_revoked"}}"""), expected);

            Require(QuotaReadException.IsAuthenticationFailure(QuotaReadException.AuthorizationRevoked) &&
                QuotaReadException.IsAuthenticationFailure(QuotaReadException.Unauthorized), "Both explicit revocation and generic 401 require reauthorization.");
            Require(!QuotaReadException.IsAuthenticationFailure(QuotaReadException.Forbidden) &&
                !QuotaReadException.IsAuthenticationFailure(QuotaReadException.Connection) &&
                !QuotaReadException.IsAuthenticationFailure(null), "Permission, network and absent errors do not mark a saved account as revoked.");

            var oversized = new ProbeStream(Encoding.UTF8.GetBytes(new string(' ', 20 * 1024) + "{\"code\":\"token_revoked\"}"));
            await Failure(new Handler(HttpStatusCode.Unauthorized, new StreamContent(oversized)), QuotaReadException.Unauthorized);
            Require(oversized.BytesRead <= 16 * 1024 + 1, "Unknown-length error bodies are bounded before parsing, without consuming the complete response.");
            foreach (var failure in new Exception[] { new IOException(Secret), new HttpRequestException(Secret), new TaskCanceledException(Secret) })
                await Failure(new Handler(HttpStatusCode.Unauthorized, new StreamContent(new ProbeStream([], failure))), QuotaReadException.Unauthorized);

            using (var cancellation = new CancellationTokenSource()) {
                var stream = new ProbeStream([], onRead: cancellation.Cancel);
                using var reader = new OfficialUsageReader(new Handler(HttpStatusCode.Unauthorized, new StreamContent(stream)));
                try { await reader.ReadAsync(Credential, cancellation.Token); throw new Exception("Expected caller cancellation while reading an error response."); }
                catch (OperationCanceledException) { Require(true, "Caller cancellation while reading a 401 body still propagates."); }
            }

            await Failure(new Handler(HttpStatusCode.OK, "<html>" + Secret + "</html>"), QuotaReadException.InvalidResponse);
            await Failure(new Handler(HttpStatusCode.OK, "{}"), QuotaReadException.MissingWindow);
            await Failure(new Handler(HttpStatusCode.OK, "[]"), QuotaReadException.InvalidResponse);
            await Failure(new Handler(HttpStatusCode.OK, """{"rate_limit":{"primary_window":{"limit_window_seconds":604800,"used_percent":20,"reset_at":9223372036854775807}}} """), QuotaReadException.InvalidResponse);

            var refused = new HttpRequestException(Secret, new SocketException((int)SocketError.ConnectionRefused));
            await Failure(new Handler(refused), QuotaReadException.Connection);
            await Failure(new Handler(refused), QuotaReadException.LocalProxyUnavailable, new Uri("http://user:" + Secret + "@127.0.0.1:7890"));
            await Failure(new Handler(refused), QuotaReadException.LocalProxyUnavailable, new Uri("http://[::1]:7890"));
            await Failure(new Handler(refused), QuotaReadException.ProxyConnection, new Uri("http://proxy.example.test:7890"));
            await Failure(new Handler(new HttpRequestException(Secret)), QuotaReadException.ProxyConnection, new Uri("http://localhost:7890"));
            await Failure(new Handler(new TaskCanceledException(Secret)), QuotaReadException.Timeout);
            await Failure(new Handler(new TaskCanceledException(Secret)), QuotaReadException.ProxyTimeout, new Uri("http://localhost:7890"));
            await Failure(new Handler(new HttpRequestException(Secret, new TimeoutException(Secret))), QuotaReadException.Timeout);
            await Failure(new Handler(new HttpRequestException(Secret, null, HttpStatusCode.ProxyAuthenticationRequired)), QuotaReadException.ProxyAuthentication, new Uri("http://localhost:7890"));

            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel();
                using var reader = new OfficialUsageReader(new Handler(new OperationCanceledException(cancellation.Token)));
                try { await reader.ReadAsync(Credential, cancellation.Token); throw new Exception("Expected caller cancellation."); }
                catch (OperationCanceledException) { Require(true, "Caller cancellation is not mislabeled as a network timeout."); }
            }
            using (var reader = new OfficialUsageReader(new Handler(HttpStatusCode.OK, GoodBody))) {
                var usage = await reader.ReadAsync(Credential, CancellationToken.None);
                Require(usage.RemainingPercent == 63 && usage.FiveHourRemainingPercent == null, "A successful weekly-only response still parses normally.");
            }
            Console.WriteLine($"PASS: {assertions} quota network assertions; bounded revocation detection, generic 401/403, local/direct/proxy transport, timeout, rate limits, malformed responses, caller cancellation and private data redaction (synthetic only).");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task Failure(HttpMessageHandler handler, string expected, Uri? proxy = null)
    {
        using var reader = new OfficialUsageReader(handler, proxy);
        try { await reader.ReadAsync(Credential, CancellationToken.None); throw new Exception("Expected quota read failure."); }
        catch (QuotaReadException error) {
            Require(error.Message == expected, "The failure should explain its specific cause.");
            Require(!error.Message.Contains(Secret) && !error.Message.Contains("example.test") && !error.Message.Contains("<html>"), "Transport details, tokens, proxy userinfo and response bodies never appear in UI errors.");
        }
    }
    private static void Require(bool success, string message) { if (!success) throw new Exception(message); assertions++; }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly HttpContent? content;
        private readonly Exception? failure;
        public Handler(HttpStatusCode status, string body) : this(status, new StringContent(body)) { }
        public Handler(HttpStatusCode status, HttpContent content) { this.status = status; this.content = content; }
        public Handler(Exception failure) { this.failure = failure; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri?.Host == "chatgpt.com" && request.Headers.Authorization?.Parameter == Secret, "Synthetic authenticated request still targets the official quota endpoint.");
            if (failure != null) return Task.FromException<HttpResponseMessage>(failure);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content! });
        }
    }

    private sealed class ProbeStream(byte[] bytes, Exception? failure = null, Action? onRead = null) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (failure != null) return ValueTask.FromException<int>(failure);
            var count = Math.Min(buffer.Length, bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
