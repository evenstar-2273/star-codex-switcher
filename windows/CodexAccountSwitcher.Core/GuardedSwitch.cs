using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

// The host pauses usage refreshes before invoking these desktop callbacks.
// Keep the Windows safety boundary here so both checks surround its credential commit.
public sealed class GuardedSwitch(string targetHome, string activeHome, ILoginValidator validator, IDesktopOperations desktop, Action<SwitchProgress>? progress = null)
    : IDesktopOperations, IDisposable
{
    private byte[]? original;
    private string? originalAccount;
    private string? targetAccount;
    private SessionVisibilityRepair? repair;
    public bool CloseAttempted { get; private set; }
    public bool OpenSucceeded { get; private set; }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        progress?.Invoke(new(12, "正在验证目标账号…"));
        CheckStorageConfiguration(activeHome);
        var targetPath = Path.Combine(targetHome, "auth.json");
        var target = LoginCredential.Read(targetPath);
        await validator.ValidateAsync(target, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(target.Bytes), SHA256.HashData(LoginCredential.Read(targetPath).Bytes)))
            throw new IOException("验证期间账号凭据发生变化，请重试。 / Credentials changed during validation.");
        targetAccount = target.AccountID;
        var activePath = Path.Combine(activeHome, "auth.json");
        SecureFiles.RejectLinks(activePath);
        if (File.Exists(activePath)) {
            var previous = LoginCredential.Read(activePath);
            original = previous.Bytes; originalAccount = previous.AccountID;
        }
        cancellationToken.ThrowIfCancellationRequested();
        CloseAttempted = true;
        progress?.Invoke(new(30, "正在关闭 Codex 并等待退出…"));
        await desktop.CloseAsync(cancellationToken);
        progress?.Invoke(new(44, "正在保存原登录并写入目标凭据…"));
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (!CloseAttempted || targetAccount == null) throw new IOException("Switch was not validated.");
        var active = LoginCredential.Read(Path.Combine(activeHome, "auth.json"));
        if (active.AccountID != targetAccount) throw new IOException("写入后账号身份不一致；取消启动。 / Account changed during handoff.");
        await validator.ValidateAsync(active, cancellationToken);
        repair = new SessionVisibilityRepair(activeHome, cancellationToken);
        await Task.Run(() => repair.Apply(active, progress), cancellationToken);
        progress?.Invoke(new(88, "正在启动 Codex…"));
        await desktop.OpenAsync(cancellationToken);
        OpenSucceeded = true;
        progress?.Invoke(new(100, "账号切换完成，Codex 已启动。", repair.ProjectNotice));
    }

    // Caller must stop the host first, preventing background sync from racing restoration.
    public void RestoreCredential()
    {
        if (!CloseAttempted) return;
        progress?.Invoke(new(90, "切换未完成，正在恢复原登录和显示状态…"));
        var path = Path.Combine(activeHome, "auth.json");
        SecureFiles.RejectLinks(path);
        if (File.Exists(path)) {
            var current = LoginCredential.Read(path);
            if (current.AccountID != targetAccount && current.AccountID != originalAccount)
                throw new IOException("检测到其他程序已切换账号，已停止自动恢复以保护当前登录。 / Another app changed the active account.");
        }
        repair?.Restore();
        if (original != null) SecureFiles.AtomicWrite(path, original);
        else if (File.Exists(path)) File.Delete(path);
    }

    public static void CheckStorageConfiguration(string home)
    {
        var path = Path.Combine(home, "config.toml");
        SecureFiles.RejectLinks(path);
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadLines(path)) {
            var line = raw.Trim();
            if (line.StartsWith('[')) break; // Only root configuration keys apply.
            if (Regex.IsMatch(line, "^cli_auth_credentials_store\\s*=\\s*['\"](?:keyring|auto|ephemeral)['\"]"))
                throw new IOException("此修复版支持 auth.json 文件登录。检测到其他凭据存储方式，已保留当前登录；请使用兼容该存储方式的切换器。 / Unsupported credential store.");
            if (Regex.IsMatch(line, "^forced_login_method\\s*=\\s*['\"]api['\"]"))
                throw new IOException("Codex 配置强制 API 登录，与 ChatGPT 账号不兼容；此次不切换。 / Incompatible login policy.");
            if (Regex.IsMatch(line, "^forced_chatgpt_workspace_id\\s*="))
                throw new IOException("Codex 配置限制了工作区；请先确认目标账号符合该配置。 / Workspace-restricted configuration.");
        }
    }

    public void Dispose() { repair?.Dispose(); if (original != null) CryptographicOperations.ZeroMemory(original); }
}
