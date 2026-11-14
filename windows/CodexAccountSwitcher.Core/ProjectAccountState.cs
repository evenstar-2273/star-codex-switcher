using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountSwitcher.Core;

// Desktop scopes are keyed by the official access-token user + workspace,
// never by the switcher's UUID, email, or the ID-token subject.
internal static class ProjectAccountState
{
    internal const string Scopes = "electron-account-renderer-state-v1", Atoms = "electron-persisted-atom-state";
    internal const string Map = "app-server-project-id-by-legacy-project-id-by-host", Assign = "thread-project-assignments", Hosts = "thread-project-membership-host-ids";
    private static readonly string[] Scoped = [
        "open-in-target-preferences", "worktree-repository-preferences", "desktop-app-beta-preferences-by-account-id",
        "active-workspace-roots", "electron-saved-workspace-roots", "electron-workspace-root-labels", "local-projects", Map,
        "app-server-projects-migration-by-host", "app-server-pending-project-deletions-by-host", "selected-project", "project-writable-roots",
        "project-appearances", "project-files", "pinned-thread-ids", "app-server-migrated-pinned-thread-ids-by-host", "pinned-project-ids",
        "sidebar-project-thread-orders", "sidebar-thread-metadata", Assign, Hosts, "thread-writable-roots", "thread-workspace-root-hints",
        "thread-projectless-output-directories", "projectless-thread-ids", "selected-remote-host-id", "remote-projects", "active-remote-project-id",
        "project-order", "connection-group-order", "remote-cwds-by-host-and-workspace", "host-id-remote-control-allowed", "added-remote-control-env-ids",
        "remote-project-connection-backfill-completed", "remote-connection-auto-connect-by-host-id", "queued-follow-ups"
    ];
    private static readonly HashSet<string> SharedAtoms = ["last_completed_onboarding", "electron:onboarding-projectless-completed",
        "electron:bedrock-govcloud-guidance-acknowledged", "ctrl-tab-shortcut-notice-shown", "number-shortcut-notice-shown", "thread-sort-key"];
    internal sealed record Project(string Id, JsonObject Metadata, string[] Roots);
    internal sealed record Thread(string Id, string? ProjectId, string? Cwd, bool User, string? Section);
    internal static string? Owner(LoginCredential credential)
    {
        try {
            var encoded = credential.AccessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
            var claims = JsonNode.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            var auth = claims?["https://api.openai.com/auth"];
            var user = String(auth?["user_id"]) ?? String(auth?["chatgpt_user_id"]);
            var account = String(auth?["chatgpt_account_id"]) ?? String(auth?["account_id"]);
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(account)) return null;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { user, account }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        } catch (Exception ex) when (ex is JsonException or FormatException or IndexOutOfRangeException or InvalidOperationException) { return null; }
    }
    internal static string? String(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    internal static JsonObject Object(JsonNode? value) => value as JsonObject ?? new();
    private static IEnumerable<string> Ids(JsonNode? value) => (value as JsonArray ?? []).Select(String).OfType<string>();
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static JsonObject Globals(JsonObject state) => new(Scoped.Where(state.ContainsKey).Select(key => KeyValuePair.Create(key, state[key]?.DeepClone())));
    internal static bool Cloud(JsonObject entry) => new[] { "codex.chatGptBacking", "codex.chatGptBackingCreation", "codex.retiredChatGptBacking", "chatGptBacking", "chatGptBackingCreation", "retiredChatGptBacking" }.Any(key => entry[key] != null);
    internal static string PathKey(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\")) path = path[4..];
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' || path.StartsWith(@"\\"))
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', '\\'))).ToLowerInvariant();
        return path.TrimEnd('/');
    }
    private static string RootsKey(IEnumerable<string> paths) => string.Join("\n", paths.Select(PathKey).Distinct().Order(StringComparer.Ordinal));
    private static bool LocalRoot(string root) => Path.IsPathFullyQualified(root) && !root.Split('/', '\\').Any(part => part.Equals(".chatgpt-projects", StringComparison.OrdinalIgnoreCase));
    internal static JsonObject Prepare(JsonObject before, string owner, string home, IReadOnlyList<Project> projects, IReadOnlyList<Thread> threads)
    {
        // Unscoped older profiles belong to Desktop; don't invent an owner or clear their state.
        if (!before.ContainsKey(Scopes)) return (JsonObject)before.DeepClone();
        if (before[Scopes] is not JsonObject original || !original.ContainsKey("owner") || original["owner"] != null && String(original["owner"]) == null ||
            original["inactive"] is not JsonObject archives || archives.Any(pair => pair.Value is not JsonObject entry || entry["globals"] is not JsonObject || entry["atoms"] is not JsonObject) ||
            before.ContainsKey(Atoms) && before[Atoms] is not JsonObject) throw new IOException("项目显示状态格式不兼容，已保留原文件并停止切换。");
        var after = (JsonObject)before.DeepClone();
        var scopes = (JsonObject)original.DeepClone(); var inactive = (JsonObject)scopes["inactive"]!;
        var current = Globals(before); var atoms = Object(before[Atoms]);
        if (String(scopes["owner"]) is { } currentOwner)
            inactive[currentOwner] = new JsonObject { ["globals"] = current.DeepClone(), ["atoms"] = new JsonObject(atoms.Where(pair => !SharedAtoms.Contains(pair.Key)).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))) };
        var target = inactive[owner]?.DeepClone() as JsonObject ?? new JsonObject { ["globals"] = new JsonObject(), ["atoms"] = new JsonObject() };
        var globals = (JsonObject)target["globals"]!;
        var sources = new[] { globals, current }.Concat(inactive.Select(pair => (JsonObject)pair.Value!["globals"]!)).ToArray();
        foreach (var source in sources) {
            if (new[] { "local-projects", Map, Assign, Hosts }.Any(key => source.ContainsKey(key) && source[key] is not JsonObject) ||
                source.ContainsKey("project-order") && source["project-order"] is not JsonArray)
                throw new IOException("项目分组记录格式不兼容，已保留原文件并停止切换。");
        }
        MergeVerifiedLocal(globals, current, sources, home, projects, threads);
        foreach (var key in Scoped) { after.Remove(key); if (globals.ContainsKey(key)) after[key] = globals[key]?.DeepClone(); }
        var nextAtoms = (JsonObject)target["atoms"]!;
        foreach (var pair in atoms.Where(pair => SharedAtoms.Contains(pair.Key))) nextAtoms[pair.Key] = pair.Value?.DeepClone();
        after[Atoms] = nextAtoms.DeepClone();
        scopes["owner"] = owner; inactive.Remove(owner); after[Scopes] = scopes;
        return after;
    }
    private static void MergeVerifiedLocal(JsonObject target, JsonObject current, JsonObject[] sources, string home, IReadOnlyList<Project> projects, IReadOnlyList<Thread> threads)
    {
        var host = "local:" + home;
        string SourceHost(JsonObject source) => Object(source[Map]).Select(pair => pair.Key).FirstOrDefault(key => key.StartsWith("local:") && PathKey(key[6..]) == PathKey(home)) ?? host;
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources) foreach (var pair in Object(Object(source["app-server-pending-project-deletions-by-host"])[SourceHost(source)])) {
            blocked.Add(pair.Key); if (String(Object(pair.Value)["projectId"]) is { } id) blocked.Add(id);
        }
        var registry = projects.Where(p => !Cloud(p.Metadata) && p.Roots.Length > 0 && p.Roots.All(LocalRoot)).ToArray();
        var rootCounts = registry.GroupBy(p => RootsKey(p.Roots)).ToDictionary(group => group.Key, group => group.Count());
        var recovered = new JsonObject(); var mapping = new JsonObject();
        var aliases = new Dictionary<string, string>(); var currentAliases = new Dictionary<string, string>(); var serverToLegacy = new Dictionary<string, string>();
        foreach (var project in registry) {
            if (blocked.Contains(project.Id)) continue;
            var roots = RootsKey(project.Roots); var candidates = new List<(int Source, string Id, JsonObject Entry)>(); var ambiguous = false;
            for (var i = 0; i < sources.Length; i++) {
                var source = sources[i]; var sourceMap = Object(Object(source[Map])[SourceHost(source)]);
                var matches = Object(source["local-projects"]).Where(pair => pair.Value is JsonObject entry && !Cloud(entry) && RootsKey(Ids(entry["rootPaths"])) == roots).ToArray();
                var explicitIds = matches.Where(pair => sourceMap.ContainsKey(pair.Key) ? String(sourceMap[pair.Key]) == project.Id : pair.Key == project.Id).ToArray();
                if (explicitIds.Length > 1) { ambiguous = true; break; }
                var chosen = explicitIds;
                if (chosen.Length == 0 && rootCounts[roots] == 1) chosen = matches.Where(pair => !sourceMap.ContainsKey(pair.Key) || String(sourceMap[pair.Key]) == project.Id).ToArray();
                if (chosen.Length > 1) { ambiguous = true; break; }
                if (chosen.Length == 1) candidates.Add((i, chosen[0].Key, (JsonObject)chosen[0].Value!));
            }
            if (ambiguous || candidates.Count == 0) continue;
            var first = candidates[0]; var legacy = first.Id;
            if (blocked.Contains(legacy) || recovered.ContainsKey(legacy)) continue;
            if (Object(target["local-projects"])[legacy] is JsonObject existing && (Cloud(existing) || RootsKey(Ids(existing["rootPaths"])) != roots ||
                Object(Object(target[Map])[SourceHost(target)]).ContainsKey(legacy) && String(Object(Object(target[Map])[SourceHost(target)])[legacy]) != project.Id)) continue;
            recovered[legacy] = first.Entry.DeepClone(); mapping[legacy] = project.Id; serverToLegacy[project.Id] = legacy; aliases[project.Id] = legacy;
            foreach (var candidate in candidates) { aliases[candidate.Id] = legacy; if (candidate.Source == 1) currentAliases[candidate.Id] = legacy; }
        }
        var nonlocal = new HashSet<string>(); var explicitAssignments = new Dictionary<string, JsonObject>();
        foreach (var source in sources) {
            foreach (var pair in Object(source[Hosts])) if (String(pair.Value) is { } location && location != "local") nonlocal.Add(pair.Key);
            foreach (var pair in Object(source[Assign])) {
                var assignment = Object(pair.Value);
                if (String(assignment["projectKind"]) is { } kind && kind != "local") nonlocal.Add(pair.Key);
                explicitAssignments.TryAdd(pair.Key, assignment);
            }
        }
        var ungrouped = Ids(target["projectless-thread-ids"]).Concat(Object(target["thread-projectless-output-directories"]).Select(pair => pair.Key)).ToHashSet();
        var assignments = new JsonObject(); var membership = new JsonObject();
        foreach (var thread in threads.Where(t => t.User)) {
            if (nonlocal.Contains(thread.Id) || ungrouped.Contains(thread.Id)) continue;
            string? legacy = null;
            if (thread.ProjectId is { } server) serverToLegacy.TryGetValue(server, out legacy);
            else if (explicitAssignments.TryGetValue(thread.Id, out var saved) && String(saved["projectKind"]) == "local" &&
                String(saved["projectId"]) is { } old && aliases.TryGetValue(old, out var mapped) && thread.Cwd is { } cwd && Ids(recovered[mapped]?["rootPaths"]).Select(PathKey).Contains(PathKey(cwd))) legacy = mapped;
            if (legacy == null) continue;
            assignments[thread.Id] = new JsonObject { ["projectKind"] = "local", ["projectId"] = legacy }; membership[thread.Id] = "local";
        }
        foreach (var (key, additions) in new[] { ("local-projects", recovered), (Assign, assignments), (Hosts, membership) }) {
            var entries = (JsonObject)Object(target[key]).DeepClone();
            foreach (var pair in additions) if (!entries.ContainsKey(pair.Key)) entries[pair.Key] = pair.Value?.DeepClone();
            if (entries.Count > 0 || target.ContainsKey(key)) target[key] = entries;
        }
        var maps = (JsonObject)Object(target[Map]).DeepClone(); var localMap = (JsonObject)Object(maps[SourceHost(target)]).DeepClone();
        foreach (var pair in mapping) if (!localMap.ContainsKey(pair.Key)) localMap[pair.Key] = pair.Value?.DeepClone();
        maps[SourceHost(target)] = localMap; target[Map] = maps;
        var order = Ids(target["project-order"]).ToList();
        foreach (var id in sources.SelectMany(source => Ids(source["project-order"])).Select(id => aliases.GetValueOrDefault(id)).OfType<string>().Concat(recovered.Select(pair => pair.Key)))
            if (!order.Contains(id)) order.Add(id);
        target["project-order"] = Array(order);
        if (current["pinned-project-ids"] is JsonArray && currentAliases.Count > 0) {
            var verified = currentAliases.Values.ToHashSet();
            string? Alias(string id) => currentAliases.GetValueOrDefault(id) ?? (serverToLegacy.TryGetValue(id, out var legacy) && verified.Contains(legacy) ? legacy : null);
            var pins = Ids(target["pinned-project-ids"]).Where(id => !verified.Contains(id) && Alias(id) == null)
                .Concat(Ids(current["pinned-project-ids"]).Select(Alias).OfType<string>()).Distinct();
            target["pinned-project-ids"] = Array(pins);
        }
        var appearances = (JsonObject)Object(target["project-appearances"]).DeepClone();
        var orders = (JsonObject)Object(target["sidebar-project-thread-orders"]).DeepClone();
        foreach (var pair in currentAliases) {
            var serverId = serverToLegacy.FirstOrDefault(p => p.Value == pair.Value).Key;
            var appearance = Object(current["project-appearances"])[pair.Key] ?? (serverId == null ? null : Object(current["project-appearances"])[serverId]);
            if (appearance is JsonObject a && a.ContainsKey("color") && a.ContainsKey("marker")) appearances[pair.Value] = new JsonObject { ["color"] = a["color"]?.DeepClone(), ["marker"] = a["marker"]?.DeepClone() };
            var oldOrder = Object(current["sidebar-project-thread-orders"])[pair.Key] ?? (serverId == null ? null : Object(current["sidebar-project-thread-orders"])[serverId]);
            if (oldOrder is not JsonObject o || o["threadIds"] is not JsonArray ids) continue;
            bool Belongs(string id) => String(Object(assignments[id])["projectId"]) == pair.Value;
            var selected = Ids(ids).Where(Belongs).Distinct().ToArray();
            if (selected.Length == 0 && ids.Count > 0) continue;
            orders[pair.Value] = new JsonObject { ["threadIds"] = Array(selected.Concat(Ids(Object(orders[pair.Value])["threadIds"]).Where(id => !Belongs(id)))) };
        }
        if (appearances.Count > 0) target["project-appearances"] = appearances;
        if (orders.Count > 0) target["sidebar-project-thread-orders"] = orders;
        var migrated = sources.SelectMany(source => Ids(Object(source["app-server-migrated-pinned-thread-ids-by-host"])[SourceHost(source)])).ToHashSet();
        var currentUngrouped = Ids(current["projectless-thread-ids"]).Concat(Object(current["thread-projectless-output-directories"]).Select(pair => pair.Key)).ToHashSet();
        var pinnable = threads.Where(t => t.User && t.Section == null && !migrated.Contains(t.Id) && !nonlocal.Contains(t.Id) &&
            (assignments.ContainsKey(t.Id) || t.ProjectId == null && (currentUngrouped.Contains(t.Id) || String(Object(current[Hosts])[t.Id]) == "local") && t.Cwd is { } cwd && LocalRoot(cwd))).Select(t => t.Id).ToHashSet();
        if (current["pinned-thread-ids"] is JsonArray && pinnable.Count > 0)
            target["pinned-thread-ids"] = Array(Ids(target["pinned-thread-ids"]).Where(id => !pinnable.Contains(id)).Concat(Ids(current["pinned-thread-ids"]).Where(pinnable.Contains)).Distinct());
    }
}
