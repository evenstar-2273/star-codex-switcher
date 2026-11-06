using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CodexAccountSwitcher;

public enum UpdatePromptChoice { Later, SkipVersion, UpdateNow }
public sealed record UpdatePromptRequest(UpdateRelease Release, bool IsManual);
public enum UpdateInstallStage { Preparing, Downloading, Verifying, Restarting }
public sealed record UpdateInstallProgress(UpdateInstallStage Stage, double? Percent, string Message);

public sealed class NativeSettings
{
    private const string Key = @"Software\CodexAccountSwitcher";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Distribution endpoint is fixed; no user-editable server or alternate feed.
    public const string DefaultUpdateFeedUrl = Distribution.FeedUrl;
    private readonly string settingsKey;
    private readonly Func<CancellationToken, Task<UpdateRelease>> fetchUpdate;
    private readonly Func<UpdateRelease, IProgress<string>, CancellationToken, Task> installUpdate;
    private readonly HashSet<string> promptedVersions = [];
    private string? pendingManualVersion;
    private UpdatePromptRequest? activePrompt;
    private bool manualCheckRequested;
    private long installAttempt;
    public NativeSettings(string? settingsKey = null, Func<CancellationToken, Task<UpdateRelease>>? fetchUpdate = null,
        Func<UpdateRelease, IProgress<string>, CancellationToken, Task>? installUpdate = null)
    {
        this.settingsKey = settingsKey ?? Key;
        this.fetchUpdate = fetchUpdate ?? FetchReleaseAsync;
        this.installUpdate = installUpdate ?? InstallReleaseAsync;
    }
    public static string NormalizeThemePreference(string? value) => value is "dark" or "light" ? value : "system";
    public string ThemePreference {
        get { using var key = Registry.CurrentUser.OpenSubKey(settingsKey); return NormalizeThemePreference(key?.GetValue("ThemePreference") as string); }
        set {
            var preference = NormalizeThemePreference(value);
            using var key = Registry.CurrentUser.CreateSubKey(settingsKey);
            key.SetValue("ThemePreference", preference, RegistryValueKind.String);
            ThemePreferenceChanged?.Invoke(); Changed?.Invoke();
        }
    }
    public event Action? ThemePreferenceChanged;
    public static string NormalizeAccentPreference(string? value) => value is "blue" or "teal" or "orange" ? value : "purple";
    public string AccentPreference {
        get { using var key = Registry.CurrentUser.OpenSubKey(settingsKey); return NormalizeAccentPreference(key?.GetValue("AccentPreference") as string); }
        set {
            var preference = NormalizeAccentPreference(value);
            using var key = Registry.CurrentUser.CreateSubKey(settingsKey);
            key.SetValue("AccentPreference", preference, RegistryValueKind.String);
            AccentPreferenceChanged?.Invoke(); Changed?.Invoke();
        }
    }
    public event Action? AccentPreferenceChanged;
    public string Version => typeof(NativeSettings).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
    public string DisplayVersion => FormatVersion(System.Version.Parse(Version));
    public static string FormatVersion(System.Version version) => version.Build >= 0 && version.Revision is -1 or 0 ? version.ToString(3) : version.ToString();
    public bool LaunchAtLogin {
        get { using var key = Registry.CurrentUser.OpenSubKey(Run); return key?.GetValue("CodexAccountSwitcher") is string; }
        set { using var key = Registry.CurrentUser.CreateSubKey(Run);
            if (value) key.SetValue("CodexAccountSwitcher", "\"" + Environment.ProcessPath + "\" --background");
            else key.DeleteValue("CodexAccountSwitcher", false); }
    }
    public bool AutomaticallyCheckUpdates {
        get { using var key = Registry.CurrentUser.OpenSubKey(settingsKey); return key?.GetValue("AutomaticallyCheckUpdates") is not int number || number != 0; }
        set { using var key = Registry.CurrentUser.CreateSubKey(settingsKey); key.SetValue("AutomaticallyCheckUpdates", value ? 1 : 0); }
    }
    public string UpdateFeedUrl => DefaultUpdateFeedUrl;
    public bool HasUpdateServer => !string.IsNullOrWhiteSpace(UpdateFeedUrl);
    public UpdateRelease? AvailableUpdate { get; private set; }
    public Uri? UpdatePage => AvailableUpdate?.DownloadUrl;
    public bool IsChecking { get; private set; }
    public bool IsInstalling { get; private set; }
    public string? InstallStatus { get; private set; }
    public UpdateInstallProgress? InstallProgress { get; private set; }
    public Func<Task>? ExitForUpdate { get; set; }
    public bool HasChecked { get; private set; }
    public string? UpdateError { get; private set; }
    public bool IsUpdatePromptOpen => activePrompt != null;
    public bool HasManualUpdatePrompt => AvailableUpdate is { } release && pendingManualVersion == VersionKey(release.Version);
    public bool ShouldShowUpdateBanner => AvailableUpdate is { } release && !IsSkippedVersion(release.Version);
    public System.Version? SkippedUpdateVersion {
        get {
            using var key = Registry.CurrentUser.OpenSubKey(settingsKey);
            return System.Version.TryParse(key?.GetValue("SkippedUpdateVersion") as string, out var version) ? NormalizeVersion(version) : null;
        }
    }
    private static System.Version NormalizeVersion(System.Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static string VersionKey(System.Version version) => NormalizeVersion(version).ToString();
    private bool IsSkippedVersion(System.Version version) => SkippedUpdateVersion == NormalizeVersion(version);
    public event Action? Changed;
    public void RequestUpdatePrompt()
    {
        if (IsInstalling || IsUpdatePromptOpen) return;
        if (IsChecking) { manualCheckRequested = true; return; }
        if (AvailableUpdate == null) return;
        pendingManualVersion = VersionKey(AvailableUpdate.Version); Changed?.Invoke();
    }
    public UpdatePromptRequest? BeginUpdatePrompt(bool allowAutomatic)
    {
        if (activePrompt != null || IsChecking || IsInstalling || AvailableUpdate is not { } release) return null;
        var key = VersionKey(release.Version);
        var manual = pendingManualVersion == key;
        if (!manual && (!allowAutomatic || IsSkippedVersion(release.Version) || promptedVersions.Contains(key))) return null;
        pendingManualVersion = null; promptedVersions.Add(key);
        return activePrompt = new UpdatePromptRequest(release, manual);
    }
    public void CompleteUpdatePrompt(UpdatePromptRequest request, UpdatePromptChoice choice)
    {
        if (!ReferenceEquals(request, activePrompt)) return;
        activePrompt = null;
        if (choice == UpdatePromptChoice.SkipVersion) {
            try {
                using var key = Registry.CurrentUser.CreateSubKey(settingsKey);
                key.SetValue("SkippedUpdateVersion", VersionKey(request.Release.Version), RegistryValueKind.String);
            } catch { UpdateError = "未能保存跳过版本设置，本次运行将不再提示。"; }
        }
        Changed?.Invoke();
    }
    public async Task CheckUpdatesAsync(bool manual = false)
    {
        if (IsInstalling || IsUpdatePromptOpen) return;
        manualCheckRequested |= manual || HasManualUpdatePrompt;
        if (IsChecking) return;
        IsChecking = true; UpdateError = null; pendingManualVersion = null; Changed?.Invoke();
        try {
            var latest = await fetchUpdate(CancellationToken.None);
            AvailableUpdate = NormalizeVersion(latest.Version) > System.Version.Parse(Version) ? latest : null;
            if (manualCheckRequested && AvailableUpdate != null) pendingManualVersion = VersionKey(latest.Version);
            HasChecked = true;
        } catch (Exception ex) {
            HasChecked = false;
            UpdateError = ex is FormatException or InvalidOperationException ? ex.Message : "更新检查失败，请稍后重试。";
        } finally { IsChecking = false; manualCheckRequested = false; Changed?.Invoke(); }
    }
    private async Task<UpdateRelease> FetchReleaseAsync(CancellationToken cancel)
    {
        if (!HasUpdateServer) throw new InvalidOperationException("更新服务尚未配置。");
        var address = UpdateFeed.ParseUrl(UpdateFeedUrl);
        using var handler = CodexAccountSwitcher.Core.WindowsHttp.Handler(); handler.UseCookies = false;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 64 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexAccountSwitcher/" + Version);
        // No account data, tokens or personal identifiers are sent to the feed.
        return await UpdateFeed.FetchAsync(http, address, cancel);
    }
    private static async Task InstallReleaseAsync(UpdateRelease release, IProgress<string> progress, CancellationToken cancel)
    {
        var jobPath = await UpdateInstaller.PrepareAsync(release, progress, cancel);
        progress.Report("校验完成，正在启动安装程序……");
        await UpdateInstaller.StartHelperAsync(jobPath);
    }
    public async Task InstallUpdateAsync()
    {
        if (IsInstalling || IsChecking || AvailableUpdate == null) return;
        if (ExitForUpdate == null) { UpdateError = "当前运行方式不支持自动更新，请使用发布的 EXE。"; Changed?.Invoke(); return; }
        var release = activePrompt?.Release ?? AvailableUpdate;
        var attempt = ++installAttempt;
        IsInstalling = true; UpdateError = null; SetInstallProgress("准备下载更新……"); Changed?.Invoke();
        try {
            var progress = new Progress<string>(message => {
                if (!IsInstalling || attempt != installAttempt) return;
                SetInstallProgress(message); Changed?.Invoke();
            });
            await installUpdate(release!, progress, CancellationToken.None);
            SetInstallProgress("安装程序已就绪，正在关闭并安装重启……"); Changed?.Invoke();
            await ExitForUpdate();
        } catch (Exception ex) {
            UpdateError = ex is System.IO.IOException ? ex.Message : "更新未完成，当前版本和账号数据保留，请稍后重试。";
            InstallStatus = null; InstallProgress = null; IsInstalling = false; Changed?.Invoke();
        }
    }
    private void SetInstallProgress(string message)
    {
        var stage = message.StartsWith("正在下载更新", StringComparison.Ordinal) ? UpdateInstallStage.Downloading
            : message.StartsWith("正在校验", StringComparison.Ordinal) ? UpdateInstallStage.Verifying
            : message.StartsWith("校验完成", StringComparison.Ordinal) || message.StartsWith("安装程序已就绪", StringComparison.Ordinal) ? UpdateInstallStage.Restarting
            : UpdateInstallStage.Preparing;
        if (InstallProgress != null && stage < InstallProgress.Stage) return;
        var match = Regex.Match(message, @"^正在下载更新 (\d{1,3})%$");
        double? percent = match.Success && double.TryParse(match.Groups[1].Value, out var value) && value <= 100 ? value : null;
        InstallStatus = message; InstallProgress = new UpdateInstallProgress(stage, percent, message);
    }
}
