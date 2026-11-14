using System.Text.Json.Nodes;
using static CodexAccountSwitcher.Core.ProjectAccountState;

namespace CodexAccountSwitcher.Core;

// Repair only the native import's proven old-ID/server-ID pair. Names and paths
// alone never establish identity. SQLite projects, sessions and import keys are
// deliberately not modified; the original import ID remains the renderer ID.
internal static class NativeProjectAliases
{
    internal sealed record Result(JsonObject State, int Corrected);
    internal static Result Normalize(JsonObject before, string home, IReadOnlyList<Project> projects, Action? check = null)
    {
        var after = (JsonObject)before.DeepClone(); var corrected = 0;
        var registeredIds = projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var project in projects) {
            check?.Invoke();
            if (Cloud(project.Metadata) || project.Roots.Length == 0 || project.Roots.Any(p => !Path.IsPathFullyQualified(p) || p.Split('/', '\\').Any(x => x.Equals(".chatgpt-projects", StringComparison.OrdinalIgnoreCase)))) continue;
            var sources = Sources(after).ToArray();
            var aliases = (project.ImportKeys ?? []).Where(id => id != project.Id && !registeredIds.Contains(id) &&
                sources.Any(s => Object(s["local-projects"])[id] != null)).Distinct().ToArray();
            if (aliases.Length != 1) continue;
            var legacy = aliases[0];
            // A duplicate must actually exist. Do not rename unrelated native IDs.
            if (!sources.Any(s => Object(s["local-projects"])[legacy] != null && Object(s["local-projects"])[project.Id] != null)) continue;
            var proposed = (JsonObject)after.DeepClone(); var count = 0; var safe = true;
            foreach (var source in Sources(proposed)) {
                check?.Invoke();
                if (!RewriteSource(source, home, project, legacy, ref count)) { safe = false; break; }
            }
            foreach (var archive in Object(Object(proposed[Scopes])["inactive"]))
                if (!RewriteAtoms(Object(archive.Value)["atoms"], project.Id, legacy)) safe = false;
            if (safe && count > 0) { after = proposed; corrected++; }
        }
        return new(after, corrected);
    }
    private static IEnumerable<JsonObject> Sources(JsonObject state) => new[] { state }.Concat(Object(Object(state[Scopes])["inactive"])
        .Select(pair => Object(Object(pair.Value)["globals"])));
    private static string Roots(JsonNode? value) => value is JsonArray a ? Roots(a.Select(String).OfType<string>()) : "";
    private static string Roots(IEnumerable<string> values) => string.Join("\n", values.Select(PathKey).Distinct().Order(StringComparer.Ordinal));
    private static bool ContainsId(JsonNode? node, string id) => node switch {
        JsonObject o => o.Any(pair => pair.Key == id || ContainsId(pair.Value, id)),
        JsonArray a => a.Any(v => ContainsId(v, id)),
        _ => String(node) == id
    };
    private static bool RewriteSource(JsonObject source, string home, Project project, string legacy, ref int count)
    {
        var server = project.Id; var entries = Object(source["local-projects"]);
        var old = entries[legacy] as JsonObject; var native = entries[server] as JsonObject;
        if (old == null && native == null) return !ContainsId(source, server);
        var roots = Roots(project.Roots);
        if (new[] { old, native }.OfType<JsonObject>().Any(p => Cloud(p) || Roots(p["rootPaths"]) != roots)) return false;
        var maps = Object(source[Map]);
        var hosts = maps.Where(p => p.Key.StartsWith("local:") && PathKey(p.Key[6..]) == PathKey(home)).ToArray();
        if (hosts.Length != 1 || hosts[0].Value is not JsonObject mapping) return false;
        if (mapping.ContainsKey(legacy) && String(mapping[legacy]) != server || mapping.ContainsKey(server) && String(mapping[server]) != server) return false;
        // Require both the persisted migration map and the database import key.
        if (String(mapping[legacy]) != server) return false;
        if (mapping.Any(p => String(p.Value) == server && p.Key != legacy && p.Key != server)) return false;
        if (maps.Any(p => p.Key != hosts[0].Key && (ContainsId(p.Value, legacy) || ContainsId(p.Value, server)))) return false;
        var pending = source["app-server-pending-project-deletions-by-host"];
        if (ContainsId(pending, server) || ContainsId(pending, legacy)) return false;
        JsonObject replacement;
        if (native != null && old != null) {
            var a = (JsonObject)old.DeepClone(); var b = (JsonObject)native.DeepClone();
            foreach (var key in new[] { "id", "createdAt", "updatedAt", "rootPaths" }) { a.Remove(key); b.Remove(key); }
            if (!Union(a, b)) return false;
            replacement = a; replacement["id"] = legacy; replacement["rootPaths"] = old["rootPaths"]?.DeepClone();
            foreach (var key in new[] { "createdAt", "updatedAt" }) {
                if (old[key] is JsonValue ov && native[key] is JsonValue nv && ov.TryGetValue<long>(out var o) && nv.TryGetValue<long>(out var n))
                    replacement[key] = key == "createdAt" ? Math.Min(o, n) : Math.Max(o, n);
                else if (old[key] != null && native[key] != null && !JsonNode.DeepEquals(old[key], native[key])) return false;
                else if ((old[key] ?? native[key]) is { } value) replacement[key] = value.DeepClone();
            }
        } else { replacement = (JsonObject)(old ?? native)!.DeepClone(); replacement["id"] = legacy; }
        var known = new HashSet<string>(["local-projects", Map, Scopes, Assign, Hosts, "project-order", "pinned-project-ids", "selected-project",
            "project-appearances", "sidebar-project-thread-orders", "project-writable-roots", "project-files"], StringComparer.Ordinal);
        foreach (var pair in source.ToArray()) {
            var key = pair.Key.StartsWith("electron-owner-value:") ? pair.Key.Split(':').ElementAtOrDefault(1) ?? pair.Key : pair.Key;
            if (key is "project-order" or "pinned-project-ids") {
                if (pair.Value is not JsonArray a) { if (ContainsId(pair.Value, server)) return false; continue; }
                var next = new JsonArray();
                foreach (var item in a) {
                    var value = String(item) == server ? JsonValue.Create(legacy) : item?.DeepClone();
                    if (String(value) != legacy || !next.Any(n => String(n) == legacy)) next.Add(value);
                }
                source[pair.Key] = next;
            } else if (key == "selected-project") {
                if (pair.Value is JsonObject selection && String(selection["type"]) == "local" && String(selection["projectId"]) == server) selection["projectId"] = legacy;
                else if (ContainsId(pair.Value, server)) return false;
            } else if (key is "project-appearances" or "sidebar-project-thread-orders" or "project-writable-roots" or "project-files") {
                if (pair.Value is not JsonObject values) { if (ContainsId(pair.Value, server)) return false; continue; }
                if (values[server] is { } addition) {
                    if (!values.ContainsKey(legacy)) values[legacy] = addition.DeepClone();
                    else if (!MergeValue(values, legacy, addition)) return false;
                    values.Remove(server);
                }
                if (ContainsId(values, server)) return false;
            } else if (pair.Key.StartsWith("electron-owner-atoms:") || pair.Key == Atoms) {
                if (!RewriteAtoms(pair.Value, server, legacy)) return false;
            } else if (!known.Contains(key) && ContainsId(pair.Value, server)) return false;
        }
        foreach (var pair in Object(source[Assign])) {
            if (pair.Value is not JsonObject assignment || String(assignment["projectId"]) != server) continue;
            if (String(assignment["projectKind"]) != "local" || String(Object(source[Hosts])[pair.Key]) is { } host && host != "local") return false;
            assignment["projectId"] = legacy;
        }
        entries[legacy] = replacement; entries.Remove(server);
        // Removing only the redundant identity mapping prevents Desktop's inverse
        // map from choosing the server-ID cache entry on the next startup.
        mapping.Remove(server); mapping[legacy] = server;
        if (native != null) count++;
        return true;
    }
    private static bool RewriteAtoms(JsonNode? node, string server, string legacy)
    {
        if (!ContainsId(node, server)) return true;
        if (node is not JsonObject atoms) return false;
        foreach (var atom in atoms) {
            if (atom.Key == "new-task-project-by-computer" && atom.Value is JsonObject computers) {
                foreach (var computer in computers.ToArray().Where(p => String(p.Value) == server)) {
                    try {
                        if (JsonNode.Parse(computer.Key) is not JsonArray identity || identity.Count != 3 || String(identity[2]) != "local") return false;
                    } catch (System.Text.Json.JsonException) { return false; }
                    computers[computer.Key] = legacy;
                }
            } else if (atom.Key.StartsWith("thread-workspace-state-v1:") && atom.Value is JsonObject workspace && workspace["project"] is JsonObject project &&
                String(project["projectKind"]) == "local" && String(project["projectId"]) == server) project["projectId"] = legacy;
        }
        return !ContainsId(atoms, server);
    }
    private static bool Union(JsonObject target, JsonObject extra)
    {
        foreach (var pair in extra) {
            if (!target.ContainsKey(pair.Key)) target[pair.Key] = pair.Value?.DeepClone();
            else if (!MergeValue(target, pair.Key, pair.Value)) return false;
        }
        return true;
    }
    private static bool MergeValue(JsonObject target, string key, JsonNode? extra)
    {
        var current = target[key];
        if (JsonNode.DeepEquals(current, extra)) return true;
        if (current is JsonObject a && extra is JsonObject b) return Union(a, b);
        if (current is JsonArray c && extra is JsonArray d) {
            foreach (var item in d) if (!c.Any(n => JsonNode.DeepEquals(n, item))) c.Add(item?.DeepClone());
            return true;
        }
        return false;
    }
}
