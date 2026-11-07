using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;

internal static class LoginReauthorizationChecks
{
    private static int passed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }

    public static async Task<int> RunAsync(string? host) {
        try {
            if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) throw new ArgumentException("Pass the freshly built SwitcherHost.exe path.");
            await VerifyAsync(Path.GetFullPath(host)); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    public static async Task VerifyAsync(string host)
    {
        passed = 0;
        var root = Path.Combine(Path.GetTempPath(), "switcher-reauth-中文 空格-" + Guid.NewGuid());
        var data = Path.Combine(root, "data");
        var active = Path.Combine(root, "active");
        Directory.CreateDirectory(active);
        var names = new[] { "CODEX_CLI_PATH", "SWITCHER_LOGIN_FIXTURE_ROOT" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var original = GuardedChecks.Credential("reauthorize-same-account").Bytes;
        var renewed = Rotate(original, "browser-renewed");
        var advanced = Rotate(original, "later-live-rotation");
        var activeAuth = Path.Combine(active, "auth.json");
        File.WriteAllBytes(activeAuth, original);
        File.WriteAllBytes(Path.Combine(root, "login-credential.json"), renewed);
        Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Environment.ProcessPath);
        Environment.SetEnvironmentVariable("SWITCHER_LOGIN_FIXTURE_ROOT", root);
        try {
            Guid existingID;
            string savedAuth;
            var browserCalls = 0;
            await using (var client = new CoreClient(host, data, active, openBrowser: (url, _) => {
                Check(url.Host == "example.test", "Reauthorization uses only a synthetic example.test browser callback.");
                Interlocked.Increment(ref browserCalls);
                var pid = Directory.GetFiles(root, "login-session-*.json").Select(path => {
                    using var session = JsonDocument.Parse(File.ReadAllText(path));
                    return session.RootElement.GetProperty("pid").GetInt32();
                }).Single(IsRunning);
                File.WriteAllText(Path.Combine(root, "complete-" + pid), "complete synthetic authorization");
                return Task.CompletedTask;
            })) {
                await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await Command(client, "refresh");
                Check(client.State.Error == null && client.State.Accounts.Length == 1 && client.State.ActiveAccountID != null,
                    "The real host begins with the one saved synthetic current account.");
                existingID = client.State.Accounts.Single().Profile.Id;
                savedAuth = Path.Combine(data, "accounts", existingID.ToString().ToUpperInvariant(), "auth.json");
                await Command(client, "add");
                await Until(() => Volatile.Read(ref browserCalls) == 1 && !client.State.IsAddingAccount);
                Check(client.State.Error == null, "Successful same-account reauthorization must not report a temporary-profile cleanup failure.");
                Check(client.State.Accounts.Length == 1 && client.State.Accounts[0].Profile.Id == existingID,
                    "Browser reauthorization upserts the same UUID instead of creating a duplicate saved account.");
                Check(File.ReadAllBytes(savedAuth).SequenceEqual(renewed), "The existing saved profile receives the newly authorized credential bytes.");
                Check(File.ReadAllBytes(activeAuth).SequenceEqual(original), "Saving browser authorization preserves the current live login until an explicit switch.");
                using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(savedAuth)!, "reauthorization-live.json")))) {
                    Check(marker.RootElement.ValueKind == JsonValueKind.Array && marker.RootElement.GetArrayLength() is > 0 and <= 2,
                        "Renewal protection retains only the bounded committed/new fingerprint records.");
                    var oldHash = Convert.ToHexString(SHA256.HashData(original));
                    var newHash = Convert.ToHexString(SHA256.HashData(renewed));
                    Check(marker.RootElement.EnumerateArray().Any(entry =>
                        string.Equals(entry.GetProperty("previousLive").GetString(), oldHash, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.GetProperty("savedCredential").GetString(), newHash, StringComparison.OrdinalIgnoreCase)),
                        "Persistent protection records the exact old-live and newly-saved SHA-256 pair.");
                    Check(marker.RootElement.EnumerateArray().All(entry => new[] { "previousLive", "savedCredential" }.All(key =>
                        entry.GetProperty(key).GetString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit))),
                        "Reauthorization protection persists only SHA-256 fingerprints, not copied credential bytes.");
                }
                using (var session = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(root, "login-session-*.json").Single()))) {
                    Check(!IsRunning(session.RootElement.GetProperty("pid").GetInt32()), "A completed browser login stops its owned process before reporting success.");
                    Check(!Directory.Exists(session.RootElement.GetProperty("home").GetString()), "The successfully upserted account leaves no unregistered locked staging directory.");
                }
                Check(Directory.GetDirectories(Path.Combine(data, "accounts")).Length == 1, "Only the original registered profile directory remains after reauthorization.");
                await Command(client, "refresh");
                Check(client.State.Error == null && File.ReadAllBytes(savedAuth).SequenceEqual(renewed),
                    "A manual refresh cannot overwrite renewed saved credentials with the exact old live token.");
                File.WriteAllBytes(Path.Combine(root, "login-credential.json"), GuardedChecks.Credential("different-authorization-account").Bytes);
                await client.CommandAsync("add", existingID).WaitAsync(TimeSpan.FromSeconds(30));
                await Until(() => Volatile.Read(ref browserCalls) == 2 && !client.State.IsAddingAccount);
                Check(client.State.Error != null && client.State.Accounts.Length == 1 && client.State.Accounts[0].Profile.Id == existingID,
                    "Restoring a selected account with a different authorized identity must report failure without adding an account.");
                Check(File.ReadAllBytes(savedAuth).SequenceEqual(renewed) && File.ReadAllBytes(activeAuth).SequenceEqual(original),
                    "Wrong-account browser authorization cannot replace the selected saved credential or current live login.");
                foreach (var path in Directory.GetFiles(root, "login-session-*.json")) {
                    using var session = JsonDocument.Parse(File.ReadAllText(path));
                    Check(!IsRunning(session.RootElement.GetProperty("pid").GetInt32()) &&
                        !Directory.Exists(session.RootElement.GetProperty("home").GetString()),
                        "Rejected targeted reauthorization stops its process and removes only its unregistered staging home.");
                }
                await Command(client, "dismissError");
            }
            await using (var restarted = new CoreClient(host, data, active, openBrowser: (_, _) => throw new InvalidOperationException("Restart must not open a browser."))) {
                await restarted.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await Command(restarted, "refresh");
                Check(restarted.State.Error == null && restarted.State.Accounts.Length == 1 && restarted.State.Accounts[0].Profile.Id == existingID,
                    "A new host process preserves the original UUID and deduplicated account count.");
                Check(File.ReadAllBytes(savedAuth).SequenceEqual(renewed) && File.ReadAllBytes(activeAuth).SequenceEqual(original),
                    "Restart and refresh do not copy the old live token over the saved browser renewal or alter live login.");
                File.WriteAllBytes(activeAuth, advanced);
                await Command(restarted, "refresh");
                Check(restarted.State.Error == null && File.ReadAllBytes(savedAuth).SequenceEqual(advanced),
                    "A genuinely later live token rotation still synchronizes; protection only suppresses the exact pre-reauthorization token.");
                Check(restarted.State.Accounts.Length == 1 && restarted.State.Accounts[0].Profile.Id == existingID,
                    "Accepting a later same-identity live rotation must keep the saved UUID stable.");
            }
            Console.WriteLine($"PASS: {passed} browser reauthorization assertions; real host, same UUID token replacement, locked staging cleanup, old-live suppression and later-token synchronization.");
        } finally {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }

    private static byte[] Rotate(byte[] source, string suffix) {
        var node = JsonNode.Parse(source)!;
        node["tokens"]!["access_token"] = "synthetic-access-" + suffix;
        node["tokens"]!["refresh_token"] = "synthetic-refresh-" + suffix;
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }
    private static Task Command(CoreClient client, string name) => client.CommandAsync(name).WaitAsync(TimeSpan.FromSeconds(30));
    private static bool IsRunning(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task Until(Func<bool> condition) {
        for (var i = 0; i < 300; i++) { if (condition()) return; await Task.Delay(25); }
        throw new TimeoutException("The synthetic browser authorization did not finish.");
    }
}
