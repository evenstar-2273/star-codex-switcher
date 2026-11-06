using System.Net;
using System.Net.Sockets;
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
            Console.WriteLine($"PASS: {assertions} quota network assertions; local/direct/proxy transport, timeout, HTTP authentication/permission/rate limits, malformed responses, caller cancellation and private data redaction (synthetic only).");
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
        private readonly string? body;
        private readonly Exception? failure;
        public Handler(HttpStatusCode status, string body) { this.status = status; this.body = body; }
        public Handler(Exception failure) { this.failure = failure; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri?.Host == "chatgpt.com" && request.Headers.Authorization?.Parameter == Secret, "Synthetic authenticated request still targets the official quota endpoint.");
            if (failure != null) return Task.FromException<HttpResponseMessage>(failure);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body!) });
        }
    }
}
