using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class AccountRemovalChecks
{
    private static int passed;
    private static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
        passed++;
    }

    public static async Task<int> RunAsync(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) throw new ArgumentException("Pass the freshly built SwitcherHost.exe path.");
        passed = 0;
        var root = Path.Combine(Path.GetTempPath(), "switcher-removal-中文 空格-" + Guid.NewGuid());
        var priorCli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        Directory.CreateDirectory(root);
        try {
            // The host's optional CLI usage calls are handled by this executable's synthetic RPC fixture.
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Environment.ProcessPath);
            var data = Path.Combine(root, "data");
            var active = Path.Combine(root, "active");
            Directory.CreateDirectory(active);
            var activePath = Path.Combine(active, "auth.json");
            var historyPath = Path.Combine(active, "session_index.jsonl");
            File.WriteAllText(historyPath, "synthetic history must survive account removal");
            var first = GuardedChecks.Credential("removal-personal");
            var second = GuardedChecks.Credential("removal-workspace");
            File.WriteAllBytes(activePath, first.Bytes);
            var desktop = new Desktop();
            Guid firstID, secondID;
            byte[] secondSaved;

            await using (var client = new GuardedAccountClient(host, data, active, desktop, new Validator())) {
                await InitializeAndRefresh(client);
                Check(client.State.Error == null && client.State.Accounts.Length == 1 && client.State.ActiveAccountID != null,
                    "The real host must recognize the initial synthetic current login.");
                firstID = client.State.ActiveAccountID!.Value;
                var imported = await client.ImportTextAsync(Encoding.UTF8.GetString(second.Bytes)).WaitAsync(TimeSpan.FromSeconds(30));
                Check(imported.Added == 1 && client.State.Accounts.Length == 2, "The alternative saved account must import successfully.");
                secondID = FindAccount(client, data, second.AccountID);
                secondSaved = File.ReadAllBytes(ProfileAuth(data, secondID));
                await Command(client, "remove", firstID);
                await Command(client, "refresh");
                Check(client.State.Error == null && client.State.Accounts.Length == 1 && client.State.Accounts[0].Profile.Id == secondID,
                    "Deleting the current account must remove it from the live host list.");
                Check(client.State.ActiveAccountID == null && client.State.ActiveIdentityConfirmed,
                    "An intentionally removed current login must be unlisted without an unconfirmed-login warning.");
                Check(!Directory.Exists(Path.GetDirectoryName(ProfileAuth(data, firstID))), "Only the removed saved profile directory must be deleted.");
                Check(File.ReadAllBytes(activePath).SequenceEqual(first.Bytes), "Removing the current saved account must preserve live auth.json byte-for-byte.");
                Check(File.ReadAllBytes(ProfileAuth(data, secondID)).SequenceEqual(secondSaved), "Removing current must preserve the other saved account.");
                Check(File.ReadAllText(historyPath) == "synthetic history must survive account removal", "Account removal must preserve Codex session history.");
                Check(desktop.Closed == 0 && desktop.Opened == 0, "Deleting the current account must not close or launch Codex Desktop.");
                Check(RemovalMarkers(data).Contains(first.AccountID), "The removed identity must be remembered without retaining its saved credential.");
            }

            await using (var restarted = new GuardedAccountClient(host, data, active, desktop, new Validator())) {
                await InitializeAndRefresh(restarted);
                Check(restarted.State.Error == null && restarted.State.Accounts.Length == 1 && restarted.State.Accounts[0].Profile.Id == secondID,
                    "A new host process must not recreate the removed current account.");
                Check(restarted.State.ActiveAccountID == null && restarted.State.ActiveIdentityConfirmed && File.ReadAllBytes(activePath).SequenceEqual(first.Bytes),
                    "Restart must preserve the deliberately unlisted live login.");

                await Command(restarted, "switch", secondID);
                await Command(restarted, "refresh");
                Check(restarted.State.Error == null && restarted.State.ActiveAccountID == secondID && restarted.State.ActiveIdentityConfirmed,
                    "The actual guarded switch must work after deleting the old current saved profile.");
                Check(restarted.State.Accounts.Length == 1 && File.ReadAllBytes(activePath).SequenceEqual(secondSaved),
                    "Switching must install the target without recreating the removed account.");
                Check(desktop.Closed == 1 && desktop.Opened == 1, "The successful guarded switch must close and reopen Desktop exactly once.");

                await Command(restarted, "remove", secondID);
                await Command(restarted, "refresh");
                Check(restarted.State.Error == null && restarted.State.Accounts.Length == 0 && restarted.State.ActiveAccountID == null,
                    "The last saved account must also be removable while current.");
                await Command(restarted, "register");
                await Command(restarted, "refresh");
                var registeredID = restarted.State.ActiveAccountID;
                Check(restarted.State.Error == null && restarted.State.Accounts.Length == 1 && registeredID != null && registeredID != secondID,
                    "Explicitly saving the current login must restore a removed account as a saved profile.");
                Check(File.ReadAllBytes(activePath).SequenceEqual(secondSaved) && desktop.Closed == 1 && desktop.Opened == 1,
                    "Saving a removed current login must not change its live credential or control Desktop.");
                Check(!RemovalMarkers(data).Contains(second.AccountID), "Explicit registration must clear that identity's removal marker.");

                await Command(restarted, "remove", registeredID);
                await Command(restarted, "refresh");
                var reimport = await restarted.ImportTextAsync(Encoding.UTF8.GetString(second.Bytes)).WaitAsync(TimeSpan.FromSeconds(30));
                await Command(restarted, "refresh");
                Check(reimport.Added == 1 && reimport.SkippedActive == 0 && restarted.State.Accounts.Length == 1,
                    "JSON import must restore a removed current account instead of skipping it as already saved.");
                Check(restarted.State.Error == null && restarted.State.ActiveAccountID != null && restarted.State.ActiveIdentityConfirmed,
                    "The host restarted by JSON import must recognize the restored saved current account.");
                Check(File.ReadAllBytes(activePath).SequenceEqual(secondSaved) && !RemovalMarkers(data).Contains(second.AccountID),
                    "JSON restoration must clear only its removal marker and preserve the live login.");
                Check(desktop.Closed == 1 && desktop.Opened == 1, "Restoring saved accounts through JSON must not control Desktop.");
            }

            Console.WriteLine($"PASS: {passed} current-account removal assertions; real host, guarded switch, refresh/restart persistence, explicit save and JSON restoration (synthetic credentials only).");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", priorCli);
            SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }

    private static async Task InitializeAndRefresh(GuardedAccountClient client) {
        await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Command(client, "refresh");
    }
    private static Task Command(GuardedAccountClient client, string name, Guid? id = null)
        => client.CommandAsync(name, id).WaitAsync(TimeSpan.FromSeconds(30));
    private static string ProfileAuth(string data, Guid id) => Path.Combine(data, "accounts", id.ToString().ToUpperInvariant(), "auth.json");
    private static Guid FindAccount(GuardedAccountClient client, string data, string accountID)
        => client.State.Accounts.Single(row => LoginCredential.Read(ProfileAuth(data, row.Profile.Id)).AccountID == accountID).Profile.Id;
    private static string[] RemovalMarkers(string data) {
        using var registry = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "accounts.json")));
        return registry.RootElement.GetProperty("removedAccountIdentities").EnumerateArray()
            .Where(identity => identity.TryGetProperty("accountID", out _)).Select(identity => identity.GetProperty("accountID").GetString()!).ToArray();
    }
    private sealed class Desktop : IDesktopOperations {
        public int Closed, Opened;
        public Task CloseAsync(CancellationToken cancellationToken) { Closed++; return Task.CompletedTask; }
        public Task OpenAsync(CancellationToken cancellationToken) { Opened++; return Task.CompletedTask; }
    }
    private sealed class Validator : ILoginValidator {
        public Task ValidateAsync(LoginCredential credential, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
