using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

public sealed record SwitchProgress(int Percent, string Message, string? Notice = null);
public interface ISwitchProgressSource { event Action<SwitchProgress>? SwitchProgressChanged; }

// Local, bounded launch maintenance. Apply only after Desktop stops and target
// credentials are committed. Keep rollback information in memory until launch succeeds.
internal sealed class SessionVisibilityRepair : IDisposable
{
    private readonly string home;
    private readonly List<DatabaseEdit> edits = [];
    private readonly List<TimeEdit> times = [];
    private string StatePath => Path.Combine(home, ".codex-global-state.json");
    private byte[]? beforeState, afterState;
    private bool stateWritten;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationToken cancellation;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);
    private sealed record RowEdit(string Id, Dictionary<string, string?> Before, Dictionary<string, string?> After);
    private sealed class DatabaseEdit(string path, ProfileSqlite db) {
        internal readonly string Path = path;
        internal readonly ProfileSqlite Db = db;
        internal readonly List<RowEdit> Rows = [];
        internal bool Committed;
    }
    private sealed record TimeEdit(string Path, long Length, DateTime Before, DateTime After);
    internal int RepairedThreads { get; private set; }
    internal bool RestoredProjects => afterState != null;
    internal string? ProjectNotice { get; private set; }
    internal int CorrectedProjectAliases { get; private set; }
    internal SessionVisibilityRepair(string home, CancellationToken cancellation) { this.home = Path.GetFullPath(home); this.cancellation = cancellation; }
    private void Check() { cancellation.ThrowIfCancellationRequested(); if (clock.Elapsed > Budget) throw new IOException("项目与会话检查超时，已停止本次修复，请稍后重试。"); }
    internal void Apply(LoginCredential target, Action<SwitchProgress>? report)
    {
        try {
            Check(); SecureFiles.RejectLinks(home);
            var before = ReadState();
            var provider = TargetProvider();
            // Existing profiles only: never create an empty database on a new computer.
            foreach (var relative in new[] { "state_5.sqlite", "sqlite/state_5.sqlite" }) {
                var path = Path.Combine(home, relative); SecureFiles.RejectLinks(path);
                if (!File.Exists(path)) continue;
                var db = new ProfileSqlite(path, true, cancellation, clock, Budget);
                var edit = new DatabaseEdit(path, db); edits.Add(edit); db.Query("BEGIN IMMEDIATE");
            }
            report?.Invoke(new(58, "正在恢复本地项目分组…"));
            if (before != null && (before.ContainsKey(ProjectAccountState.Scopes) || before.ContainsKey("local-projects"))) {
                var (projects, threads) = ReadProjects(before);
                var aliases = NativeProjectAliases.Normalize(before, home, projects, Check);
                CorrectedProjectAliases = aliases.Corrected;
                var after = aliases.State;
                if (before.ContainsKey(ProjectAccountState.Scopes)) {
                    var owner = ProjectAccountState.Owner(target) ?? throw new IOException("目标凭据缺少项目归属信息，请重新授权后再切换；原项目状态已保留。");
                    after = ProjectAccountState.Prepare(after, owner, home, projects, threads);
                }
                if (ProjectAccountState.HasAmbiguousProjectMappings(after, home))
                    ProjectNotice = "部分重复项目入口缺少一致的导入记录，已保留原项目与会话关联，未自动处理。";
                if (!JsonNode.DeepEquals(before, after)) {
                    afterState = JsonSerializer.SerializeToUtf8Bytes(after);
                    if (afterState.Length > 20_000_000) throw new IOException("项目状态文件过大，已停止本次修复。");
                }
            }
            report?.Invoke(new(72, "正在检查并修复会话显示…", ProjectNotice));
            foreach (var edit in edits) PlanThreads(edit, provider);
            // All schema/identity/file checks finish before the first write.
            Check();
            foreach (var edit in edits) foreach (var row in edit.Rows) WriteRow(edit.Db, row.Id, row.After, row.Before);
            if (afterState != null) { ReplaceState(beforeState!, afterState); stateWritten = true; }
            foreach (var edit in edits) { Check(); edit.Db.Query("COMMIT"); edit.Committed = true; }
            foreach (var time in times) {
                Check(); SecureFiles.RejectLinks(time.Path);
                var info = new FileInfo(time.Path);
                if (info.Length == time.Length && info.LastWriteTimeUtc == time.Before) File.SetLastWriteTimeUtc(time.Path, time.After);
            }
            RepairedThreads = edits.Sum(edit => edit.Rows.Count);
        } catch (Exception ex) {
            try { Restore(); }
            catch { throw new IOException("项目与会话修复未完成，恢复时检测到文件被占用或改变。请保持 Codex 关闭后重试。"); }
            if (ex is OperationCanceledException) throw;
            throw new IOException(ex is IOException && Regex.IsMatch(ex.Message, "[\\u4e00-\\u9fff]") ? ex.Message : "无法安全访问项目或会话文件，已停止本次修复。请关闭相关 Codex 窗口并检查数据目录权限后重试。");
        } finally { foreach (var edit in edits) edit.Db.Dispose(); }
    }
    private JsonObject? ReadState()
    {
        SecureFiles.RejectLinks(StatePath);
        if (!File.Exists(StatePath)) return null;
        if (new FileInfo(StatePath).Length > 20_000_000) throw new IOException("项目状态文件过大，已保留原文件。");
        beforeState = File.ReadAllBytes(StatePath);
        var content = beforeState.AsSpan(); if (content.StartsWith(new byte[] { 239, 187, 191 })) content = content[3..];
        return JsonNode.Parse(content) as JsonObject ?? throw new IOException("项目状态文件为空或格式损坏，已保留原文件并停止切换。");
    }
    private string TargetProvider()
    {
        var path = Path.Combine(home, "config.toml"); SecureFiles.RejectLinks(path);
        if (!File.Exists(path)) return "openai";
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Codex 配置过大，无法确认会话显示设置。");
        // Only root scalar keys apply. Unsupported syntax is not guessed.
        foreach (var raw in File.ReadLines(path)) {
            var line = raw.Trim(); if (line.StartsWith('[')) break;
            if (!Regex.IsMatch(line, "^model_provider\\s*=")) continue;
            var match = Regex.Match(line, "^model_provider\\s*=\\s*(['\"])([^'\"\\\\\\r\\n]+)\\1\\s*(?:#.*)?$");
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[2].Value) || match.Groups[2].Value.Length > 200)
                throw new IOException("无法识别 Codex 的模型供应商配置，已停止会话修复。");
            return match.Groups[2].Value.Trim();
        }
        return "openai";
    }
    private (List<ProjectAccountState.Project>, List<ProjectAccountState.Thread>) ReadProjects(JsonObject state)
    {
        foreach (var edit in edits) {
            var db = edit.Db; var cols = db.Columns("projects"); var roots = db.Columns("project_roots");
            if (cols.Count == 0 && roots.Count == 0) continue;
            if (!cols.IsSupersetOf(["id", "name"]) || !roots.IsSupersetOf(["project_id", "path"])) throw new IOException("项目数据库结构暂不兼容，已停止切换并保留原数据。");
            var projects = new List<ProjectAccountState.Project>();
            var imports = db.Columns("project_idempotency_keys").IsSupersetOf(["key", "project_id"])
                ? db.Query("SELECT key,project_id FROM project_idempotency_keys").Where(r => r["key"] != null && r["project_id"] != null)
                    .GroupBy(r => r["project_id"]!).ToDictionary(g => g.Key, g => g.Select(r => r["key"]!).ToArray())
                : new Dictionary<string, string[]>();
            foreach (var row in db.Query("SELECT id," + (cols.Contains("metadata") ? "metadata" : "'{}' AS metadata") + " FROM projects ORDER BY id LIMIT 20001")) {
                Check();
                var metadata = JsonNode.Parse(row["metadata"] ?? "{}") as JsonObject ?? throw new IOException("项目元数据格式损坏，已停止修复。");
                var paths = db.Query("SELECT path FROM project_roots WHERE project_id=?1 ORDER BY path LIMIT 33", row["id"]).Select(r => r["path"]!).ToArray();
                if (paths.Length > 32) throw new IOException("项目目录记录过多，已停止本次快速修复。");
                projects.Add(new(row["id"]!, metadata, paths, imports.GetValueOrDefault(row["id"]!, [])));
            }
            var tc = db.Columns("threads");
            if (tc.Count == 0) return (projects, []);
            if (!tc.IsSupersetOf(["id", "cwd", "project_id", "source"]) || !tc.Contains("has_user_event") && !tc.Contains("first_user_message"))
                throw new IOException("项目会话关联结构暂不兼容，已保留原数据。");
            var fields = new[] { "id", "cwd", "project_id", "source", "thread_source", "has_user_event", "first_user_message", "thread_section_id" };
            var select = "SELECT " + string.Join(",", fields.Select(key => tc.Contains(key) ? key : "NULL AS " + key)) + " FROM threads";
            var rows = db.Query(select + " ORDER BY id LIMIT 2049");
            if (rows.Count > 2048) {
                // On large histories, restore only memberships used by current/target UI.
                var ids = ReferencedThreads(state).Take(2048).ToArray(); rows.Clear();
                foreach (var id in ids) { Check(); rows.AddRange(db.Query(select + " WHERE id=?1", id)); }
            }
            return (projects, rows.Select(row => new ProjectAccountState.Thread(row["id"]!, row["project_id"], row["cwd"],
                !IsChild(row["source"], row["thread_source"]) && (row["has_user_event"] is not (null or "0") || !string.IsNullOrEmpty(row["first_user_message"])), row["thread_section_id"])).ToList());
        }
        return ([], []);
    }
    private static IEnumerable<string> ReferencedThreads(JsonObject state)
    {
        var sources = new[] { state }.Concat(ProjectAccountState.Object(state[ProjectAccountState.Scopes]?["inactive"]).Select(pair => ProjectAccountState.Object(pair.Value?["globals"])));
        return sources.SelectMany(source => ProjectAccountState.Object(source[ProjectAccountState.Assign]).Select(pair => pair.Key)
            .Concat((source["pinned-thread-ids"] as JsonArray ?? []).Select(ProjectAccountState.String).OfType<string>())
            .Concat(ProjectAccountState.Object(source["sidebar-project-thread-orders"]).SelectMany(pair => (pair.Value?["threadIds"] as JsonArray ?? []).Select(ProjectAccountState.String).OfType<string>()))).Distinct();
    }
    private static bool IsChild(string? source, string? detail) => (source?.Contains("subagent", StringComparison.OrdinalIgnoreCase) ?? false) ||
        string.Equals(source, "guardian", StringComparison.OrdinalIgnoreCase) || string.Equals(detail, "subagent", StringComparison.OrdinalIgnoreCase) || string.Equals(detail, "guardian", StringComparison.OrdinalIgnoreCase);
    private void PlanThreads(DatabaseEdit edit, string provider)
    {
        var columns = edit.Db.Columns("threads"); if (!columns.Contains("id")) return;
        var visible = new List<string>();
        if (columns.Contains("archived")) visible.Add("COALESCE(archived,0)=0");
        if (columns.Contains("preview")) visible.Add(columns.Contains("first_user_message") ? "(COALESCE(preview,'')<>'' OR COALESCE(first_user_message,'')<>'')" : "COALESCE(preview,'')<>''");
        if (columns.Contains("rollout_path")) visible.Add("COALESCE(rollout_path,'')<>''");
        if (columns.Contains("source")) visible.Add("LOWER(COALESCE(source,'')) NOT LIKE '%subagent%' AND LOWER(COALESCE(source,'')) NOT LIKE '%internal%'");
        if (columns.Contains("thread_source")) visible.Add("COALESCE(thread_source,'')<>'ambient_suggestions'");
        var differences = new List<string>();
        if (columns.Contains("model_provider")) differences.Add("COALESCE(model_provider,'')<>?1");
        if (columns.Contains("first_user_message")) {
            if (columns.Contains("has_user_event")) differences.Add("(COALESCE(first_user_message,'')<>'' AND COALESCE(has_user_event,0)<>1)");
            if (columns.Contains("thread_source")) differences.Add("(COALESCE(first_user_message,'')<>'' AND COALESCE(thread_source,'')='')");
            if (columns.Contains("preview")) differences.Add("(COALESCE(first_user_message,'')<>'' AND COALESCE(preview,'')='')");
        }
        if (columns.Contains("cwd")) differences.Add("substr(cwd,1,4)='\\\\?\\'");
        if (differences.Count == 0) return;
        visible.Add("(" + string.Join(" OR ", differences) + ")");
        var wanted = new[] { "id", "model_provider", "has_user_event", "first_user_message", "thread_source", "preview", "cwd", "rollout_path", "updated_at", "updated_at_ms", "source" }.Where(columns.Contains).ToArray();
        foreach (var row in edit.Db.Query("SELECT " + string.Join(",", wanted) + " FROM threads WHERE " + string.Join(" AND ", visible), columns.Contains("model_provider") ? [provider] : [])) {
            Check(); if (IsChild(row.GetValueOrDefault("source"), row.GetValueOrDefault("thread_source"))) continue;
            var after = new Dictionary<string, string?>();
            if (columns.Contains("model_provider") && row["model_provider"] != provider) after["model_provider"] = provider;
            if (!string.IsNullOrEmpty(row.GetValueOrDefault("first_user_message"))) {
                if (columns.Contains("has_user_event") && row["has_user_event"] != "1") after["has_user_event"] = "1";
                if (columns.Contains("thread_source") && string.IsNullOrEmpty(row["thread_source"])) after["thread_source"] = "user";
                if (columns.Contains("preview") && string.IsNullOrEmpty(row["preview"])) after["preview"] = row["first_user_message"];
            }
            if (row.GetValueOrDefault("cwd") is { } cwd && cwd.StartsWith(@"\\?\")) {
                var normalized = cwd.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + cwd[8..] : cwd[4..];
                if (Path.IsPathFullyQualified(normalized)) after["cwd"] = normalized;
            }
            if (after.Count == 0) continue;
            edit.Rows.Add(new(row["id"]!, after.Keys.ToDictionary(key => key, key => row[key]), after));
            if (after.ContainsKey("model_provider")) PlanTime(row);
        }
    }
    private void PlanTime(Dictionary<string, string?> row)
    {
        var raw = row.GetValueOrDefault("rollout_path"); if (string.IsNullOrEmpty(raw)) return;
        var path = Path.GetFullPath(raw, home);
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(home) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
        SecureFiles.RejectLinks(path); if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new List<byte>(); int b;
        while (header.Count < 65536 && (b = stream.ReadByte()) >= 0 && b != 10) header.Add((byte)b);
        if (header.Count >= 65536) return;
        JsonObject? meta;
        try { meta = JsonNode.Parse(header.ToArray()) as JsonObject; } catch (JsonException) { return; }
        if (ProjectAccountState.String(meta?["type"]) != "session_meta" || meta?["payload"] is not JsonObject payload ||
            ProjectAccountState.String(payload["id"]) != row["id"] || IsChild(ProjectAccountState.String(payload["source"]) ?? payload["source"]?.ToJsonString(), null)) return;
        DateTime desired;
        try {
            if (long.TryParse(row.GetValueOrDefault("updated_at_ms"), out var ms)) desired = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            else if (long.TryParse(row.GetValueOrDefault("updated_at"), out var seconds)) desired = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            else return;
        } catch (ArgumentOutOfRangeException) { return; }
        var info = new FileInfo(path);
        if (desired.Year < 2000 || Math.Abs((info.LastWriteTimeUtc - desired).TotalMilliseconds) < 1 || times.Any(item => item.Path == path)) return;
        times.Add(new(path, info.Length, info.LastWriteTimeUtc, desired));
    }
    private static void WriteRow(ProfileSqlite db, string id, Dictionary<string, string?> values, Dictionary<string, string?> expected)
    {
        var parameters = values.Values.Concat(new[] { id }).Concat(expected.Values).ToArray();
        var i = 0;
        var set = string.Join(",", values.Keys.Select(key => key + "=?" + ++i)); var where = "id=?" + ++i;
        where += string.Concat(expected.Keys.Select(key => " AND " + key + " IS ?" + ++i));
        db.Query("UPDATE threads SET " + set + " WHERE " + where, parameters);
        if (db.Query("SELECT changes() AS n")[0]["n"] != "1") throw new IOException("会话状态已被其他进程修改，已停止覆盖。");
    }
    private void ReplaceState(byte[] expected, byte[] replacement)
    {
        SecureFiles.RejectLinks(StatePath);
        if (!File.Exists(StatePath) || !File.ReadAllBytes(StatePath).SequenceEqual(expected)) throw new IOException("项目状态已被其他进程修改，已停止覆盖。");
        SecureFiles.AtomicWrite(StatePath, replacement);
    }
    internal void Restore()
    {
        foreach (var edit in edits.Where(edit => !edit.Committed)) edit.Db.Rollback();
        var failures = false;
        foreach (var edit in edits.Where(edit => edit.Committed && edit.Rows.Count > 0)) {
            try {
                using var db = new ProfileSqlite(edit.Path, true, CancellationToken.None, Stopwatch.StartNew(), Budget);
                db.Query("BEGIN IMMEDIATE");
                try { foreach (var row in edit.Rows) WriteRow(db, row.Id, row.Before, row.After); db.Query("COMMIT"); edit.Committed = false; }
                catch { db.Rollback(); throw; }
            } catch { failures = true; }
        }
        foreach (var time in times) {
            try { SecureFiles.RejectLinks(time.Path); var info = new FileInfo(time.Path); if (info.Exists && info.Length == time.Length && info.LastWriteTimeUtc == time.After) File.SetLastWriteTimeUtc(time.Path, time.Before); }
            catch { failures = true; }
        }
        if (stateWritten) { try { ReplaceState(afterState!, beforeState!); stateWritten = false; } catch { failures = true; } }
        if (failures) throw new IOException("部分项目或会话状态已改变，已停止自动覆盖，请关闭 Codex 后重试。");
    }
    public void Dispose() { foreach (var edit in edits) edit.Db.Dispose(); }
}
