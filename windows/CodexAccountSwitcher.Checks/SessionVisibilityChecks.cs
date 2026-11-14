using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;

internal static class SessionVisibilityChecks
{
    private static int passed;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); passed++; }
    internal static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "star-session-check-中文 空格-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var active = Path.Combine(root, "active"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(active); Directory.CreateDirectory(target);
            var a = Credential("user-a", "workspace-a"); var b = Credential("user-b", "workspace-b");
            Check(ProjectAccountState.Owner(a) == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("[\"user-a\",\"workspace-a\"]"))), "Official owner must use access user/workspace hash");
            var aOwner = ProjectAccountState.Owner(a)!; var bOwner = ProjectAccountState.Owner(b)!;
            Check(ProjectAccountState.Owner(Credential("user-a", "workspace-b")) != aOwner && ProjectAccountState.Owner(Credential("user-b", "workspace-a")) != aOwner,
                "Different users and workspaces must never share renderer-owner keys");
            var host = "local:" + active;
            var state = JsonNode.Parse("""
                {"untouched":{"setting":42},"electron-persisted-atom-state":{"thread-sort-key":"recent","private-setting":"A"},
                 "local-projects":{"local-a":{"name":"工作项目","rootPaths":["D:\\Work"]},"cloud-a":{"name":"云端","rootPaths":["D:\\Cloud"],"chatGptBacking":{}}},
                 "project-order":["local-a","cloud-a"],"pinned-project-ids":["local-a"],"pinned-thread-ids":["main"],
                 "project-appearances":{"local-a":{"color":"purple","marker":"star"}},
                 "sidebar-project-thread-orders":{"local-a":{"threadIds":["main","child","missing"]}},
                 "thread-project-assignments":{"main":{"projectKind":"local","projectId":"local-a"}},
                 "thread-project-membership-host-ids":{"main":"local","remote":"ssh"},
                 "remote-projects":{"remote-a":{"secret":"keep-in-a"}}}
                """)!.AsObject();
            state[ProjectAccountState.Map] = new JsonObject { [host] = new JsonObject { ["local-a"] = "registry-project" } };
            state[ProjectAccountState.Scopes] = new JsonObject { ["owner"] = aOwner, ["inactive"] = new JsonObject {
                [bOwner] = new JsonObject { ["atoms"] = new JsonObject { ["private-setting"] = "B" }, ["globals"] = new JsonObject {
                    ["remote-projects"] = new JsonObject { ["remote-b"] = new JsonObject { ["secret"] = "keep-in-b" } },
                    ["local-projects"] = new JsonObject { ["cloud-b"] = new JsonObject { ["chatGptBacking"] = new JsonObject(), ["rootPaths"] = new JsonArray("D:\\Other") } }
                } }
            } };
            var statePath = Path.Combine(active, ".codex-global-state.json");
            var original = JsonSerializer.SerializeToUtf8Bytes(state); File.WriteAllBytes(statePath, original);
            File.WriteAllText(statePath + ".bak", "PRESERVE-OFFICIAL-BACKUP");
            var dbPath = Path.Combine(active, "state_5.sqlite"); File.WriteAllBytes(dbPath, []);
            using (var db = Open(dbPath)) {
                db.Query("CREATE TABLE projects(id TEXT PRIMARY KEY,name TEXT,metadata TEXT)");
                db.Query("CREATE TABLE project_roots(project_id TEXT,path TEXT)");
                db.Query("INSERT INTO projects VALUES('registry-project','工作项目','{}')");
                db.Query("INSERT INTO project_roots VALUES('registry-project','D:\\Work')");
                db.Query("CREATE TABLE threads(id TEXT PRIMARY KEY,project_id TEXT,cwd TEXT,source TEXT,thread_source TEXT,has_user_event INTEGER,first_user_message TEXT,preview TEXT,model_provider TEXT,archived INTEGER,rollout_path TEXT,updated_at INTEGER)");
                foreach (var (id, source, archived) in new[] { ("main", "cli", "0"), ("child", "subagent", "0"), ("archived", "cli", "1"), ("remote", "cli", "0") })
                    db.Query("INSERT INTO threads VALUES(?1,'registry-project',?2,?3,'',0,'hello','','old-provider',?4,?5,1730000000)", id, @"\\?\D:\Work", source, archived, Path.Combine(active, "sessions", id + ".jsonl"));
            }
            Directory.CreateDirectory(Path.Combine(active, "sessions"));
            var rollout = Path.Combine(active, "sessions", "main.jsonl");
            var contents = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"main\",\"source\":\"cli\"}}\n{\"type\":\"message\",\"content\":\"DO NOT EDIT\"}\n";
            File.WriteAllText(rollout, contents); var originalTime = File.GetLastWriteTimeUtc(rollout);
            var index = Path.Combine(active, "session_index.jsonl"); File.WriteAllText(index, "PRESERVE-INDEX");
            File.WriteAllBytes(Path.Combine(active, "auth.json"), a.Bytes); File.WriteAllBytes(Path.Combine(target, "auth.json"), b.Bytes);
            var stages = new List<SwitchProgress>(); var desktop = new Desktop();
            using (var guard = new GuardedSwitch(target, active, new Validator(), desktop, stages.Add)) {
                await guard.CloseAsync(default); File.WriteAllBytes(Path.Combine(active, "auth.json"), b.Bytes); await guard.OpenAsync(default);
                var after = JsonNode.Parse(File.ReadAllBytes(statePath))!;
                Check(guard.OpenSucceeded && desktop.Closed == 1 && desktop.Opened == 1, "Repair must run inside successful guarded handoff");
                Check(after[ProjectAccountState.Scopes]!["owner"]!.GetValue<string>() == bOwner, "Use official target renderer scope");
                Check(after["local-projects"]!["local-a"] != null && after["local-projects"]!["cloud-b"] != null && after["local-projects"]!["cloud-a"] == null, "Only verified local project crosses account scopes");
                Check(after["remote-projects"]!["remote-b"] != null && after["remote-projects"]!["remote-a"] == null, "Remote target state wins; previous remote state must not leak");
                Check(after[ProjectAccountState.Scopes]!["inactive"]![aOwner]!["globals"]!["remote-projects"]!["remote-a"] != null, "Archive old scope intact");
                Check(after[ProjectAccountState.Atoms]!["private-setting"]!.GetValue<string>() == "B" && after[ProjectAccountState.Atoms]!["thread-sort-key"]!.GetValue<string>() == "recent", "Private settings stay scoped; shared sort preference follows user");
                Check(after["project-appearances"]!["local-a"]!["color"]!.GetValue<string>() == "purple", "Preserve verified project appearance");
                Check(after["sidebar-project-thread-orders"]!["local-a"]!["threadIds"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(["main"]), "Only verified user threads carry ordering");
                Check(after[ProjectAccountState.Assign]!["main"] != null && after[ProjectAccountState.Assign]!["child"] == null && after[ProjectAccountState.Assign]!["remote"] == null, "Exclude child and nonlocal memberships");
                using (var db = Open(dbPath)) {
                    var rows = db.Query("SELECT * FROM threads").ToDictionary(r => r["id"]!);
                    Check(rows["main"]["model_provider"] == "openai" && rows["main"]["has_user_event"] == "1" && rows["main"]["preview"] == "hello" && rows["main"]["thread_source"] == "user", "Repair provider/preview/user flags together");
                    Check(rows["child"]["model_provider"] == "old-provider" && rows["archived"]["model_provider"] == "old-provider", "No archived/subagent provider mutation");
                    Check(rows["main"]["cwd"] == @"D:\Work", "Normalize official extended Windows path");
                }
                Check(File.ReadAllText(rollout) == contents && File.GetLastWriteTimeUtc(rollout) == DateTimeOffset.FromUnixTimeSeconds(1730000000).UtcDateTime, "Only referenced rollout timestamp changes, never message contents");
                Check(File.ReadAllText(index) == "PRESERVE-INDEX" && File.ReadAllText(statePath + ".bak") == "PRESERVE-OFFICIAL-BACKUP", "Do not rebuild historical index or replace official backup");
                Check(stages.Select(s => s.Percent).SequenceEqual([12, 30, 44, 58, 72, 88, 100]), "Progress reflects actual completed stages");
                using (var repeat = new SessionVisibilityRepair(active, default)) { repeat.Apply(b, null); Check(repeat.RepairedThreads == 0, "Repeated repair is idempotent"); }
                // This explicit rollback tests a failed launch; no real desktop is used.
                guard.RestoreCredential();
                Check(File.ReadAllBytes(statePath).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(active, "auth.json")).SequenceEqual(a.Bytes), "Rollback restores exact original scope/login");
                using var restoredDb = Open(dbPath);
                Check(restoredDb.Query("SELECT model_provider FROM threads WHERE id='main'")[0]["model_provider"] == "old-provider" && File.GetLastWriteTimeUtc(rollout) == originalTime, "Rollback restores database fields and timestamp");
            }
            // Repeated switches A -> B -> A retain the shared verified project and both remote scopes.
            using (var first = new SessionVisibilityRepair(active, default)) first.Apply(b, null);
            using (var second = new SessionVisibilityRepair(active, default)) second.Apply(a, null);
            var roundTrip = JsonNode.Parse(File.ReadAllBytes(statePath))!;
            Check(roundTrip["remote-projects"]!["remote-a"] != null && roundTrip["local-projects"]!["local-a"] != null, "A-B-A restores account's own remote project and shared local group");
            File.WriteAllBytes(statePath, original);
            var lockedClock = Stopwatch.StartNew();
            using (var locker = Open(dbPath)) {
                locker.Query("BEGIN IMMEDIATE");
                using var repair = new SessionVisibilityRepair(active, default);
                Throws(() => repair.Apply(b, null));
                Check(lockedClock.Elapsed < TimeSpan.FromSeconds(3) && File.ReadAllBytes(statePath).SequenceEqual(original), "Busy database fails promptly without touching renderer state"); locker.Rollback();
            }
            File.WriteAllText(statePath, "{broken");
            using (var repair = new SessionVisibilityRepair(active, default)) Throws(() => repair.Apply(b, null));
            Check(File.ReadAllText(statePath) == "{broken", "Corrupt state is not silently replaced");
            File.WriteAllBytes(statePath, original);
            var localRegistry = new[] { new ProjectAccountState.Project("registry-project", new JsonObject(), [@"D:\Work"]) };
            var deleted = (JsonObject)state.DeepClone();
            deleted["app-server-pending-project-deletions-by-host"] = new JsonObject { [host] = new JsonObject { ["local-a"] = new JsonObject { ["projectId"] = "registry-project" } } };
            Check(ProjectAccountState.Prepare(deleted, bOwner, active, localRegistry, [])["local-projects"]?["local-a"] == null,
                "Pending project deletion must not be resurrected during switch");
            var ambiguous = (JsonObject)state.DeepClone(); ambiguous.Remove(ProjectAccountState.Map);
            ambiguous["local-projects"]!["another-local"] = ambiguous["local-projects"]!["local-a"]!.DeepClone();
            Check(ProjectAccountState.Prepare(ambiguous, bOwner, active, localRegistry, [])["local-projects"]?["local-a"] == null,
                "Ambiguous roots without stable IDs cannot invent project membership");
            var conflicting = (JsonObject)state.DeepClone();
            conflicting[ProjectAccountState.Scopes]!["inactive"]![bOwner]!["globals"]!["local-projects"]!["local-a"] = new JsonObject { ["rootPaths"] = new JsonArray(@"D:\Other"), ["chatGptBacking"] = new JsonObject() };
            var preserved = ProjectAccountState.Prepare(conflicting, bOwner, active, localRegistry, []);
            Check(preserved["local-projects"]!["local-a"]!["chatGptBacking"] is JsonObject && preserved[ProjectAccountState.Assign]?["main"] == null,
                "An existing target cloud project cannot be overwritten by a colliding local ID");
            Check(ProjectAccountState.PathKey(@"\\?\D:\Work\Folder\..") == ProjectAccountState.PathKey("d:/work") && ProjectAccountState.PathKey(@"\\?\UNC\server\share\work") == ProjectAccountState.PathKey(@"\\server\share\work"),
                "Windows path aliases are normalized lexically without accessing network/workspace paths");
            Check(JsonNode.DeepEquals(ProjectAccountState.Prepare(new JsonObject { ["old-state"] = 7 }, bOwner, active, [], []), new JsonObject { ["old-state"] = 7 }),
                "Legacy unscoped state remains intact without inventing an owner");
            var unknownOwner = Credential(null, "workspace-b");
            using (var repair = new SessionVisibilityRepair(active, default)) Throws(() => repair.Apply(unknownOwner, null));
            Check(File.ReadAllBytes(statePath).SequenceEqual(original), "Missing owner claims cannot clear existing project groups");
            using (var repair = new SessionVisibilityRepair(active, new CancellationToken(true))) Throws(() => repair.Apply(b, null));
            Check(File.ReadAllBytes(statePath).SequenceEqual(original), "Cancellation never writes partial project state");
            using (var repair = new SessionVisibilityRepair(active, default)) {
                repair.Apply(b, null);
                File.WriteAllText(statePath, "{\"written-by-another-process\":true}");
                Throws(repair.Restore);
                Check(File.ReadAllText(statePath).Contains("written-by-another-process"), "Rollback refuses to overwrite another process's renderer-state write");
            }
            File.WriteAllBytes(statePath, original);
            var secondDb = Path.Combine(active, "sqlite"); Directory.CreateDirectory(secondDb); File.WriteAllText(Path.Combine(secondDb, "state_5.sqlite"), "invalid-database");
            using (var repair = new SessionVisibilityRepair(active, default)) Throws(() => repair.Apply(b, null));
            Check(File.ReadAllBytes(statePath).SequenceEqual(original) && File.ReadAllText(Path.Combine(secondDb, "state_5.sqlite")) == "invalid-database", "Invalid secondary database cannot partially rewrite state or be recreated");
            var empty = Path.Combine(root, "new-computer"); Directory.CreateDirectory(empty);
            using (var repair = new SessionVisibilityRepair(empty, default)) repair.Apply(b, null);
            Check(!Directory.EnumerateFileSystemEntries(empty).Any(), "New computer without histories creates no phantom state/database");
            var largeDb = Path.Combine(empty, "state_5.sqlite"); File.WriteAllBytes(largeDb, []);
            using (var db = Open(largeDb)) {
                db.Query("CREATE TABLE threads(id TEXT PRIMARY KEY,model_provider TEXT,preview TEXT,source TEXT,thread_source TEXT,has_user_event INTEGER,first_user_message TEXT,archived INTEGER,rollout_path TEXT,cwd TEXT)");
                db.Query("WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<25000) INSERT INTO threads SELECT CAST(x AS TEXT),'openai','hello','cli','user',1,'hello',0,'missing.jsonl','D:\\Work' FROM n");
            }
            using (var repair = new SessionVisibilityRepair(empty, default)) { repair.Apply(b, null); Check(repair.RepairedThreads == 0, "Large already-correct histories do not hit the changed-row cap or get rewritten"); }
            var external = Path.Combine(root, "external"); Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "state_5.sqlite"), "KEEP");
            var linked = Path.Combine(root, "linked"); Directory.CreateDirectory(linked);
            StoragePathChecks.CreateJunction(Path.Combine(linked, "sqlite"), external);
            using (var repair = new SessionVisibilityRepair(linked, default)) Throws(() => repair.Apply(b, null));
            Check(File.ReadAllText(Path.Combine(external, "state_5.sqlite")) == "KEEP", "Inner junction cannot mutate external files");
            Directory.Delete(Path.Combine(linked, "sqlite"));
            Console.WriteLine($"PASS: {passed} isolated project/session and guarded-switch assertions."); return 0;
        } finally { Directory.Delete(root, true); }
    }
    private static ProfileSqlite Open(string path) => new(path, true, default, Stopwatch.StartNew(), TimeSpan.FromSeconds(10));
    private static void Throws(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { passed++; return; } throw new Exception("Expected protected failure"); }
    internal static LoginCredential Credential(string? user, string account)
    {
        string Jwt(object claims) => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
        var auth = new Dictionary<string, object?> { ["user_id"] = user, ["chatgpt_account_id"] = account };
        var access = Jwt(new Dictionary<string, object?> { ["https://api.openai.com/auth"] = auth });
        var id = Jwt(new Dictionary<string, object?> { ["email"] = "fixture@example.test", ["https://api.openai.com/auth"] = auth });
        return LoginCredential.Parse(JsonSerializer.SerializeToUtf8Bytes(new { tokens = new { access_token = access, id_token = id, account_id = account, refresh_token = "synthetic" } }));
    }
    private sealed class Validator : ILoginValidator { public Task ValidateAsync(LoginCredential credential, CancellationToken ct) => Task.CompletedTask; }
    private sealed class Desktop : IDesktopOperations {
        internal int Closed, Opened;
        public Task CloseAsync(CancellationToken ct) { Closed++; return Task.CompletedTask; }
        public Task OpenAsync(CancellationToken ct) { Opened++; return Task.CompletedTask; }
    }
}
