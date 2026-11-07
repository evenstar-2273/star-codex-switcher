using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountSwitcher.Core;

public sealed record ImportResult(int Added, int Updated, int SkippedActive);
public interface IAccountImporter
{
    Task<ImportResult> ImportFilesAsync(string[] paths);
    Task<ImportResult> ImportTextAsync(string json);
}

public static class AccountImport
{
    public static LoginCredential[] ParseFiles(string[] paths)
    {
        var result = new List<LoginCredential>();
        if (paths.Length is 0 or > 100) throw new IOException("请选择 1–100 个 JSON 文件。 / Select 1–100 JSON files.");
        foreach (var path in paths) {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("单个导入文件不能超过 16 MB。 / Import file too large.");
            result.AddRange(ParseText(File.ReadAllText(path, Encoding.UTF8)));
            if (result.Count > 500) throw new IOException("一次最多导入 500 个账号。 / At most 500 accounts per import.");
        }
        return result.GroupBy(c => c.AccountID).Select(g => g.Last()).ToArray();
    }

    public static LoginCredential[] ParseText(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new IOException("请先粘贴完整 JSON。 / Paste account JSON first.");
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new IOException("JSON 不能超过 16 MB。 / JSON too large.");
        var result = new List<LoginCredential>();
            try {
                using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("accounts", out var accounts)) root = accounts;
                var rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
                foreach (var row in rows) {
                    result.Add(LoginCredential.FromExport(row));
                    if (result.Count > 500) throw new IOException("一次最多导入 500 个账号。 / At most 500 accounts per import.");
                }
            } catch (JsonException) { throw new IOException("内容不是有效 JSON。请粘贴完整导出内容，不能使用打码预览。 / Invalid JSON export."); }
        if (result.Count == 0) throw new IOException("JSON 中没有账号。 / No accounts found.");
        // Last row for the same workspace wins. Never deduplicate by email alone.
        return result.GroupBy(c => c.AccountID).Select(g => g.Last()).ToArray();
    }

    // Host must be stopped while changing its registry. The live Codex login is never written here.
    public static ImportResult Commit(string dataHome, string activeHome, LoginCredential[] credentials)
        => CommitResolved(SecureFiles.ResolveStorageRoot(dataHome), SecureFiles.ResolveStorageRoot(activeHome), credentials);

    internal static ImportResult CommitResolved(string dataHome, string activeHome, LoginCredential[] credentials)
    {
        SecureFiles.RejectLinks(dataHome); SecureFiles.RejectLinks(activeHome);
        SecureFiles.CreatePrivateDirectory(dataHome);
        var registryPath = Path.Combine(dataHome, "accounts.json");
        SecureFiles.RejectLinks(registryPath);
        var registry = File.Exists(registryPath) ? JsonNode.Parse(File.ReadAllBytes(registryPath))!.AsObject()
            : new JsonObject { ["accounts"] = new JsonArray() };
        var rows = registry["accounts"]!.AsArray();
        var activePath = Path.Combine(activeHome, "auth.json");
        var activeID = File.Exists(activePath) ? LoginCredential.Read(activePath).AccountID : null;
        var changes = new List<(string Path, byte[]? Previous)>();
        var created = new List<string>();
        var added = 0; var updated = 0; var skipped = 0;
        var profilesRoot = Path.Combine(dataHome, "accounts");
        SecureFiles.CreatePrivateDirectory(profilesRoot);
        try {
            foreach (var credential in credentials) {
                var existing = rows.OfType<JsonObject>().FirstOrDefault(r => r["accountID"]?.GetValue<string>() == credential.AccountID);
                if (credential.AccountID == activeID && existing != null) { skipped++; continue; }
                var id = existing != null ? Guid.Parse(existing["id"]!.GetValue<string>()) : Guid.NewGuid();
                var directory = Path.Combine(profilesRoot, id.ToString().ToUpperInvariant());
                if (!Directory.Exists(directory)) created.Add(directory);
                SecureFiles.CreatePrivateDirectory(directory);
                var path = Path.Combine(directory, "auth.json");
                SecureFiles.RejectLinks(path);
                changes.Add((path, File.Exists(path) ? File.ReadAllBytes(path) : null));
                SecureFiles.AtomicWrite(path, credential.Bytes);
                if (existing != null) {
                    existing["email"] = credential.Email; existing["displayName"] = credential.Email; updated++;
                } else {
                    rows.Add(new JsonObject { ["id"] = id.ToString().ToUpperInvariant(), ["accountID"] = credential.AccountID,
                        ["email"] = credential.Email, ["displayName"] = credential.Email,
                        ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") });
                    added++;
                }
                if (registry["removedAccountIdentities"] is JsonArray removed) {
                    for (var index = removed.Count - 1; index >= 0; index--) {
                        var identity = removed[index];
                        var removedID = identity?["accountID"]?.GetValue<string>();
                        var removedEmail = identity?["email"]?.GetValue<string>();
                        if (removedID == credential.AccountID && (removedEmail == null || credential.Email == null ||
                            string.Equals(removedEmail, credential.Email, StringComparison.OrdinalIgnoreCase))) removed.RemoveAt(index);
                    }
                }
            }
            SecureFiles.AtomicWrite(registryPath, Encoding.UTF8.GetBytes(registry.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        } catch {
            foreach (var change in changes.AsEnumerable().Reverse()) {
                if (change.Previous != null) SecureFiles.AtomicWrite(change.Path, change.Previous);
                else if (File.Exists(change.Path)) File.Delete(change.Path);
            }
            foreach (var directory in created) SecureFiles.DeleteOwnedDirectory(profilesRoot, directory);
            throw;
        }
        return new(added, updated, skipped);
    }
}
