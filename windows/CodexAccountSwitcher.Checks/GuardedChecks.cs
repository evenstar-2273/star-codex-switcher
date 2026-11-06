using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;

internal static class GuardedChecks
{
    private static int passed;
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); passed++; }
    public static async Task<int> RunAsync(string? host)
    {
        var root = Path.Combine(Path.GetTempPath(), "switcher-guarded-check-中文 空格-" + Guid.NewGuid());
        var cli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        Directory.CreateDirectory(root);
        try {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Environment.ProcessPath);
            var active = Path.Combine(root, "active"); var target = Path.Combine(root, "target");
            Directory.CreateDirectory(active); Directory.CreateDirectory(target);
            var a = Credential("workspace-a"); var b = Credential("workspace-b");
            File.WriteAllBytes(Path.Combine(active, "auth.json"), a.Bytes);
            File.WriteAllBytes(Path.Combine(target, "auth.json"), b.Bytes);
            foreach (var status in new[] { 401, 403, 429, 500, 302 }) {
                var desktop = new FakeDesktop();
                using var online = new OnlineLoginValidator(new Handler(status, "{}"));
                using var guard = new GuardedSwitch(target, active, online, desktop);
                await Throws(() => guard.CloseAsync(default));
                Require(desktop.Closed == 0 && !guard.CloseAttempted, $"HTTP {status} must not close Desktop");
                Require(File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(a.Bytes), "Rejected preflight changed active credential");
            }
            foreach (var body in new[] { "<html>login</html>", "{}" }) {
                using var online = new OnlineLoginValidator(new Handler(200, body));
                var desktop = new FakeDesktop(); using var guard = new GuardedSwitch(target, active, online, desktop);
                await Throws(() => guard.CloseAsync(default)); Require(desktop.Closed == 0, "HTML/empty response must not close Desktop");
            }
            using (var online = new OnlineLoginValidator(new Handler(200, "{\"plan_type\":\"plus\",\"rate_limit\":{}}"))) {
                await online.ValidateAsync(b, default); Require(true, "Valid authenticated response");
            }
            var desktopSuccess = new FakeDesktop();
            using (var guard = new GuardedSwitch(target, active, new Validator(), desktopSuccess)) {
                await guard.CloseAsync(default);
                File.WriteAllBytes(Path.Combine(active, "auth.json"), b.Bytes);
                await guard.OpenAsync(default);
                Require(guard.OpenSucceeded && desktopSuccess.Closed == 1 && desktopSuccess.Opened == 1, "Successful switch must open once");
                guard.RestoreCredential();
                Require(File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(a.Bytes), "Rollback must restore original bytes");
            }
            var failureDesktop = new FakeDesktop();
            using (var guard = new GuardedSwitch(target, active, new Validator(failOn: 2), failureDesktop)) {
                await guard.CloseAsync(default); File.WriteAllBytes(Path.Combine(active, "auth.json"), b.Bytes);
                await Throws(() => guard.OpenAsync(default)); guard.RestoreCredential();
                Require(failureDesktop.Opened == 0 && LoginCredential.Read(Path.Combine(active, "auth.json")).AccountID == a.AccountID,
                    "Post-write validation failure must restore old login");
            }
            using (var guard = new GuardedSwitch(target, active, new Validator(), new FakeDesktop())) {
                await guard.CloseAsync(default);
                File.WriteAllBytes(Path.Combine(active, "auth.json"), Credential("third-party").Bytes);
                await Throws(() => { guard.RestoreCredential(); return Task.CompletedTask; });
                Require(LoginCredential.Read(Path.Combine(active, "auth.json")).AccountID == "third-party", "Do not overwrite another tool's switch");
            }
            File.WriteAllBytes(Path.Combine(active, "auth.json"), a.Bytes);
            File.WriteAllText(Path.Combine(active, "config.toml"), "cli_auth_credentials_store = 'keyring'\n");
            using (var guard = new GuardedSwitch(target, active, new Validator(), new FakeDesktop()))
                await Throws(() => guard.CloseAsync(default));
            File.Delete(Path.Combine(active, "config.toml"));

            // Nested auth plus flat and nested account exports, multiple workspaces per email.
            var flat = JsonNode.Parse(b.Bytes)!["tokens"]!.DeepClone();
            flat["email"] = "same@example.test";
            var file = Path.Combine(root, "导入 账号.json");
            File.WriteAllText(file, new JsonArray(JsonNode.Parse(a.Bytes), flat, JsonNode.Parse(Credential("workspace-c").Bytes)).ToJsonString());
            var imports = AccountImport.ParseFiles([file]);
            var pasted = AccountImport.ParseText("\uFEFF" + File.ReadAllText(file));
            Require(pasted.Select(c => c.AccountID).SequenceEqual(imports.Select(c => c.AccountID)), "Pasted and file JSON must produce identical accounts");
            await Throws(() => { AccountImport.ParseText(" \n\t "); return Task.CompletedTask; });
            Require(imports.Length == 3, "Import must preserve distinct workspaces with same email");
            var data = Path.Combine(root, "data");
            var imported = AccountImport.Commit(data, active, imports);
            Require(imported.Added == 2 && imported.SkippedActive == 1, "Active login must be skipped; two new accounts added");
            imported = AccountImport.Commit(data, active, imports);
            Require(imported.Updated == 2 && imported.Added == 0, "Duplicate workspace must update rather than duplicate");
            Require(File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(a.Bytes), "Import changed active auth");
            File.WriteAllText(file, "[{\"access_token\":\"masked***\"}]");
            await Throws(() => { AccountImport.ParseFiles([file]); return Task.CompletedTask; });
            File.WriteAllText(file, "not json SECRET_MARKER");
            try { AccountImport.ParseFiles([file]); throw new Exception("Invalid JSON accepted"); }
            catch (IOException ex) { Require(!ex.Message.Contains("SECRET_MARKER"), "Parse error leaked source content"); }

            if (host != null) {
                var nativeDesktop = new FakeDesktop();
                await using (var client = new GuardedAccountClient(host, data, active, nativeDesktop, new Validator())) {
                    await client.InitializeAsync(); await Until(() => client.State.Accounts.Length == 3);
                    Require(client.State.ActiveAccountID != null, "Original host must read imported registry and active login");
                    var targetID = client.State.Accounts.First(r => r.Profile.Id != client.State.ActiveAccountID).Profile.Id;
                    await client.CommandAsync("switch", targetID);
                    await Until(() => client.State.ActiveAccountID == targetID);
                    Require(nativeDesktop.Closed == 1 && nativeDesktop.Opened == 1, "End-to-end host handoff must use guarded callbacks");
                    var result = await client.ImportTextAsync(Encoding.UTF8.GetString(Credential("workspace-d").Bytes));
                    await Until(() => client.State.Accounts.Length == 4);
                    Require(result.Added == 1, "Pasted import must restart host and populate list immediately");
                }
                var recoveryDesktop = new FakeDesktop();
                var previous = File.ReadAllBytes(Path.Combine(active, "auth.json"));
                await using (var client = new GuardedAccountClient(host, data, active, recoveryDesktop, new Validator(failOn: 2))) {
                    await client.InitializeAsync(); await Until(() => client.State.Accounts.Length == 4);
                    var originalID = client.State.ActiveAccountID;
                    var targetID = client.State.Accounts.First(r => r.Profile.Id != originalID).Profile.Id;
                    await client.CommandAsync("switch", targetID);
                    await Until(() => client.State.ActiveAccountID == originalID);
                    Require(File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(previous), "Host recovery lost previous credentials");
                    Require(recoveryDesktop.Opened == 1 && client.State.Error?.Contains("Previous login restored") == true, "Recovery must reopen and report failure");
                }
            }
            Console.WriteLine($"PASS: {passed} guarded switching/import assertions (synthetic credentials only).");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Environment.SetEnvironmentVariable("CODEX_CLI_PATH", cli); SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root); }
    }

    private static async Task Throws(Func<Task> action)
    {
        try { await action(); } catch (IOException) { passed++; return; }
        throw new Exception("Expected a safe validation failure");
    }
    private static async Task Until(Func<bool> condition) {
        for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(50); }
        throw new Exception("Expected host snapshot was not received");
    }
    internal static LoginCredential Credential(string id) {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> {
            ["email"] = "same@example.test", ["https://api.openai.com/auth"] = new { chatgpt_account_id = id }
        });
        var payload = Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return LoginCredential.Parse(JsonSerializer.SerializeToUtf8Bytes(new { auth_mode = "chatgpt", tokens = new {
            account_id = id, id_token = "e30." + payload + ".fixture", access_token = "synthetic-" + id, refresh_token = "synthetic-refresh"
        }}));
    }
    private sealed class FakeDesktop : IDesktopOperations {
        public int Closed, Opened;
        public Task CloseAsync(CancellationToken ct) { Closed++; return Task.CompletedTask; }
        public Task OpenAsync(CancellationToken ct) { Opened++; return Task.CompletedTask; }
    }
    private sealed class Validator(int failOn = -1) : ILoginValidator {
        private int calls;
        public Task ValidateAsync(LoginCredential credential, CancellationToken ct) {
            if (++calls == failOn) throw new IOException("Synthetic revoked credential");
            return Task.CompletedTask;
        }
    }
    private sealed class Handler(int status, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Require(request.RequestUri?.Host == "chatgpt.com" && request.Headers.Authorization?.Scheme == "Bearer", "Validate only against official endpoint");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }
}
