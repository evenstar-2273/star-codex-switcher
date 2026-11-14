using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountSwitcher.Core;

internal static class ProjectAliasChecks
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static readonly string Home = @"C:\fixture\.codex", Host = "local:" + Home;
    private static JsonObject Fixture() => JsonNode.Parse("""
        {"local-projects":{
          "legacy":{"id":"legacy","name":"项目","rootPaths":["D:\\Work"],"createdAt":10,"updatedAt":20},
          "server":{"id":"server","name":"项目","rootPaths":["d:/work"],"createdAt":30,"updatedAt":40}},
         "project-order":["server","legacy"],"pinned-project-ids":["server","legacy"],
         "selected-project":{"type":"local","projectId":"server"},
         "electron-owner-value:selected-project:account":{"type":"local","projectId":"server"},
         "electron-owner-atoms:account":{"new-task-project-by-computer":{"[\"account\",\"user\",\"local\"]":"server"},"thread-workspace-state-v1:chat":{"project":{"projectKind":"local","projectId":"server"},"revision":2,"applied":{"keep":true}}},
         "thread-project-assignments":{"a":{"projectKind":"local","projectId":"legacy"},"b":{"projectKind":"local","projectId":"server"},"json-only":{"projectKind":"local","projectId":"server"}},
         "sidebar-project-thread-orders":{"legacy":{"threadIds":["a"]},"server":{"threadIds":["b","json-only"]}},
         "project-files":{"legacy":["file-a"],"server":["file-b"]},
         "project-appearances":{"legacy":{"color":"purple","marker":"star"},"server":{"color":"purple","marker":"star"}},
         "unrelated":{"setting":42}}
        """)!.AsObject();
    private static JsonObject State() { var s = Fixture(); s[ProjectAccountState.Map] = new JsonObject { [Host] = new JsonObject { ["legacy"] = "server", ["server"] = "server" } }; return s; }
    private static ProjectAccountState.Project Project(params string[] keys) => new("server", new JsonObject(), [@"D:\Work"], keys);
    internal static int Run()
    {
        var state = State(); var original = state.ToJsonString();
        var result = NativeProjectAliases.Normalize(state, Home, [Project("legacy")]); var after = result.State;
        Check(result.Corrected == 1 && after["local-projects"]!.AsObject().Count == 1 && after["local-projects"]!["legacy"] != null, "A proven migration pair uses its original native import ID");
        Check(after["project-order"]!.ToJsonString() == "[\"legacy\"]" && after["pinned-project-ids"]!.ToJsonString() == "[\"legacy\"]", "Order and pins have one stable entry");
        Check(after[ProjectAccountState.Assign]!.AsObject().Count == 3 && after[ProjectAccountState.Assign]!.AsObject().All(p => p.Value!["projectId"]!.GetValue<string>() == "legacy"), "Both sides and JSON-only thread memberships survive");
        Check(after["sidebar-project-thread-orders"]!["legacy"]!["threadIds"]!.AsArray().Count == 3 && after["project-files"]!["legacy"]!.AsArray().Count == 2, "All ordered thread and file references survive");
        Check(after["selected-project"]!["projectId"]!.GetValue<string>() == "legacy" && after["electron-owner-value:selected-project:account"]!["projectId"]!.GetValue<string>() == "legacy", "Both old and native owner-specific selection follow the surviving ID");
        Check(after["electron-owner-atoms:account"]!["thread-workspace-state-v1:chat"]!["project"]!["projectId"]!.GetValue<string>() == "legacy" &&
            after["electron-owner-atoms:account"]!["new-task-project-by-computer"]!.AsObject().All(p => p.Value!.GetValue<string>() == "legacy") &&
            after["electron-owner-atoms:account"]!["thread-workspace-state-v1:chat"]!["revision"]!.GetValue<int>() == 2, "Native per-owner task/workspace selections follow the alias without changing unrelated state");
        Check(after[ProjectAccountState.Map]![Host]!.AsObject().Count == 1 && after[ProjectAccountState.Map]![Host]!["legacy"]!.GetValue<string>() == "server", "Native inverse map has only one destination");
        Check(state.ToJsonString() == original && JsonNode.DeepEquals(NativeProjectAliases.Normalize(after, Home, [Project("legacy")]).State, after), "Planning is pure and repeated startup is idempotent");
        // Mirror native initializeProjects: invert the map, list server projects,
        // then import only cache entries without a server mapping.
        for (var i = 0; i < 6; i++) {
            var map = after[ProjectAccountState.Map]![Host]!.AsObject();
            var inverse = new Dictionary<string, string>(); foreach (var p in map) inverse[p.Value!.GetValue<string>()] = p.Key;
            Check(inverse["server"] == "legacy" && after["local-projects"]!.AsObject().All(p => map[p.Key] != null), "Restart has no fallback server-ID entry or unmapped project to import");
            after = NativeProjectAliases.Normalize(after, Home, [Project("legacy")]).State;
        }
        void Unchanged(JsonObject input, ProjectAccountState.Project[]? registry, string message) => Check(JsonNode.DeepEquals(input, NativeProjectAliases.Normalize(input, Home, registry ?? [Project("legacy")]).State), message);
        Unchanged(State(), [Project()], "A name/path match without native import proof is never enough");
        var bad = State(); bad["local-projects"]!["legacy"]!["rootPaths"] = new JsonArray(@"D:\Other"); Unchanged(bad, null, "Different roots are not collapsed");
        bad = State(); bad["local-projects"]!["legacy"]!["chatGptBacking"] = new JsonObject(); Unchanged(bad, null, "Cloud-backed groups are not changed");
        bad = State(); bad["project-appearances"]!["server"]!["color"] = "blue"; Unchanged(bad, null, "Conflicting presentation metadata is retained rather than overwritten");
        bad = State(); bad["unknown-future-reference"] = new JsonObject { ["projectId"] = "server" }; Unchanged(bad, null, "Unknown reference schemas prevent orphaned references");
        bad = State(); bad["electron-owner-atoms:account"]!["future-project-selection"] = "server"; Unchanged(bad, null, "Unknown owner-atom references also prevent partial repair");
        bad = State(); bad["app-server-pending-project-deletions-by-host"] = new JsonObject { [Host] = new JsonObject { ["legacy"] = new JsonObject { ["projectId"] = "server" } } }; Unchanged(bad, null, "Pending deletion is not resurrected");
        bad = State(); bad[ProjectAccountState.Hosts] = new JsonObject { ["b"] = "ssh-host" }; Unchanged(bad, null, "Remote memberships prevent unsafe local rewrites");
        bad = State(); bad[ProjectAccountState.Map]![Host]!["legacy"] = "other"; Unchanged(bad, null, "Conflicting migration maps cannot be guessed");
        Unchanged(State(), [Project("legacy"), new("legacy", new JsonObject(), [@"D:\Work"])], "Two real database projects sharing a path remain distinct");
        var scoped = State(); var archived = State(); archived.Remove("unrelated");
        scoped[ProjectAccountState.Scopes] = new JsonObject { ["owner"] = "old", ["inactive"] = new JsonObject { ["target"] = new JsonObject { ["globals"] = archived, ["atoms"] = new JsonObject() } } };
        var normalized = NativeProjectAliases.Normalize(scoped, Home, [Project("legacy")]).State;
        Check(normalized[ProjectAccountState.Scopes]!["inactive"]!["target"]!["globals"]!["local-projects"]!.AsObject().Count == 1, "Inactive account archives cannot reintroduce the stale alias");
        for (var i = 0; i < 6; i++) normalized = ProjectAccountState.Prepare(normalized, i % 2 == 0 ? "target" : "old", Home, [Project("legacy")], []);
        Check(normalized["local-projects"]!.AsObject().Count == 1 && normalized[ProjectAccountState.Assign]!.AsObject().Count == 3, "Repeated account round-trips preserve all references and one entry");
        Integration();
        Console.WriteLine($"PASS: {checks} native project alias assertions."); return 0;
    }
    private static void Integration()
    {
        var temp = Path.Combine(Path.GetTempPath(), "star-alias-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try {
            var s = State(); s[ProjectAccountState.Map] = new JsonObject { ["local:" + temp] = s[ProjectAccountState.Map]![Host]!.DeepClone() };
            var path = Path.Combine(temp, ".codex-global-state.json"); var bytes = JsonSerializer.SerializeToUtf8Bytes(s); File.WriteAllBytes(path, bytes);
            var dbPath = Path.Combine(temp, "state_5.sqlite"); File.WriteAllBytes(dbPath, []);
            using (var db = Open(dbPath, true)) {
                db.Query("CREATE TABLE projects(id TEXT PRIMARY KEY,name TEXT,metadata TEXT)"); db.Query("INSERT INTO projects VALUES('server','项目','{}')");
                db.Query("CREATE TABLE project_roots(project_id TEXT,path TEXT)"); db.Query("INSERT INTO project_roots VALUES('server','D:\\Work')");
                db.Query("CREATE TABLE project_idempotency_keys(key TEXT PRIMARY KEY,project_id TEXT)"); db.Query("INSERT INTO project_idempotency_keys VALUES('legacy','server')");
                db.Query("CREATE TABLE threads(id TEXT PRIMARY KEY,project_id TEXT,cwd TEXT,source TEXT,has_user_event INTEGER,model_provider TEXT,preview TEXT,thread_source TEXT)");
                db.Query("INSERT INTO threads VALUES('a','server','D:\\Work','cli',1,'openai','hello','user')");
                db.Query("INSERT INTO threads VALUES('b','server','D:\\Work','cli',1,'openai','hello','user')");
            }
            using var repair = new SessionVisibilityRepair(temp, default); repair.Apply(SessionVisibilityChecks.Credential("user", "account"), null);
            Check(repair.CorrectedProjectAliases == 1 && repair.ProjectNotice == null, "Normal switch maintenance repairs a verified pair instead of just warning");
            Check(JsonNode.Parse(File.ReadAllBytes(path))!["local-projects"]!.AsObject().Count == 1, "Native unscoped state is actually repaired on the guarded launch path");
            using (var db = Open(dbPath, false)) Check(db.Query("SELECT id FROM threads WHERE project_id='server'").Count == 2 && db.Query("SELECT * FROM projects").Count == 1 && db.Query("SELECT * FROM project_idempotency_keys").Count == 1, "No database project, session membership or import identity is removed");
            repair.Restore(); Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Failed launch rolls back exact original state including both aliases");
        } finally { Directory.Delete(temp, true); }
    }
    private static ProfileSqlite Open(string path, bool write) => new(path, write, default, Stopwatch.StartNew(), TimeSpan.FromSeconds(20));
    internal static int Inspect(string home)
    {
        // Explicit read-only audit; never calls Apply/Prepare or writes live files.
        var statePath = Path.Combine(home, ".codex-global-state.json"); var bytes = File.ReadAllBytes(statePath); var before = JsonNode.Parse(bytes)!.AsObject();
        using var db = Open(Path.Combine(home, "state_5.sqlite"), false); db.Query("BEGIN");
        var imports = db.Query("SELECT key,project_id FROM project_idempotency_keys");
        var projects = db.Query("SELECT id,metadata FROM projects").Select(r => new ProjectAccountState.Project(r["id"]!, JsonNode.Parse(r["metadata"] ?? "{}")!.AsObject(),
            db.Query("SELECT path FROM project_roots WHERE project_id=?1", r["id"]).Select(p => p["path"]!).ToArray(), imports.Where(p => p["project_id"] == r["id"]).Select(p => p["key"]!).ToArray())).ToArray();
        var result = NativeProjectAliases.Normalize(before, home, projects); var after = result.State; var host = "local:" + home;
        string? Server(JsonObject s, JsonNode? assignment) { var id = ProjectAccountState.String(assignment?["projectId"]); return id == null ? null : ProjectAccountState.String(s[ProjectAccountState.Map]?[host]?[id]) ?? id; }
        var oldAssignments = ProjectAccountState.Object(before[ProjectAccountState.Assign]); var newAssignments = ProjectAccountState.Object(after[ProjectAccountState.Assign]);
        Check(oldAssignments.Count == newAssignments.Count && oldAssignments.All(p => Server(before, p.Value) == Server(after, newAssignments[p.Key])), "Every live thread assignment resolves to the same database project");
        Check(JsonNode.DeepEquals(NativeProjectAliases.Normalize(after, home, projects).State, after), "Live-data proposal is idempotent");
        var beforeThreadIds = ProjectAccountState.Object(before["sidebar-project-thread-orders"]).SelectMany(p => (p.Value?["threadIds"] as JsonArray ?? []).Select(ProjectAccountState.String)).ToHashSet();
        var afterThreadIds = ProjectAccountState.Object(after["sidebar-project-thread-orders"]).SelectMany(p => (p.Value?["threadIds"] as JsonArray ?? []).Select(ProjectAccountState.String)).ToHashSet();
        Check(beforeThreadIds.SetEquals(afterThreadIds), "Live proposal preserves every ordered thread reference");
        var inverse = new Dictionary<string, string>();
        foreach (var pair in ProjectAccountState.Object(ProjectAccountState.Object(after[ProjectAccountState.Map])[host]))
            if (ProjectAccountState.String(pair.Value) is { } server) inverse[server] = pair.Key;
        var cached = ProjectAccountState.Object(after["local-projects"]);
        Check(projects.All(p => cached.ContainsKey(inverse.GetValueOrDefault(p.Id, p.Id))) && cached.All(p => ProjectAccountState.Object(ProjectAccountState.Object(after[ProjectAccountState.Map])[host]).ContainsKey(p.Key)),
            "Native startup has no fallback server-ID entry or unmapped cached project to recreate");
        Console.WriteLine($"READ-ONLY: registry={projects.Length}; displayed={ProjectAccountState.Object(before["local-projects"]).Count} -> {ProjectAccountState.Object(after["local-projects"]).Count}; verified_pairs={result.Corrected}; memberships={oldAssignments.Count}; ordered_threads={beforeThreadIds.Count}; unresolved={ProjectAccountState.HasAmbiguousProjectMappings(after, home)}. No live writes.");
        return 0;
    }
}
