using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Win32;

namespace CodexAccountSwitcher;

public sealed class NativeSettings
{
    private const string Key = @"Software\CodexAccountSwitcher";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Distribution endpoint is fixed; no user-editable server or alternate feed.
    public const string DefaultUpdateFeedUrl = Distribution.FeedUrl;
    private readonly string settingsKey;
    public NativeSettings(string? settingsKey = null) { this.settingsKey = settingsKey ?? Key; }
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
    public static string NormalizeAccentPreference(string? value) => value is "purple" or "teal" or "orange" ? value : "blue";
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
    public Func<Task>? ExitForUpdate { get; set; }
    public bool HasChecked { get; private set; }
    public string? UpdateError { get; private set; }
    public event Action? Changed;
    public async Task CheckUpdatesAsync()
    {
        if (IsChecking || IsInstalling) return;
        IsChecking = true; UpdateError = null; AvailableUpdate = null; Changed?.Invoke();
        try {
            if (!HasUpdateServer) throw new InvalidOperationException("更新服务尚未配置。");
            var address = UpdateFeed.ParseUrl(UpdateFeedUrl);
            using var handler = CodexAccountSwitcher.Core.WindowsHttp.Handler();
            handler.UseCookies = false;
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 64 * 1024 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexAccountSwitcher/" + Version);
            // No account data, tokens or personal identifiers are sent to the feed.
            var latest = await UpdateFeed.FetchAsync(http, address);
            AvailableUpdate = latest.Version > new Version(Version) ? latest : null;
            HasChecked = true;
        } catch (Exception ex) {
            HasChecked = false;
            UpdateError = ex is FormatException or InvalidOperationException ? ex.Message : "更新检查失败，请稍后重试。";
        } finally { IsChecking = false; Changed?.Invoke(); }
    }
    public async Task InstallUpdateAsync()
    {
        if (IsInstalling || IsChecking || AvailableUpdate == null) return;
        if (ExitForUpdate == null) { UpdateError = "当前运行方式不支持自动更新，请使用发布的 EXE。"; Changed?.Invoke(); return; }
        IsInstalling = true; UpdateError = null; InstallStatus = "准备下载更新……"; Changed?.Invoke();
        try {
            var progress = new Progress<string>(message => { InstallStatus = message; Changed?.Invoke(); });
            var jobPath = await UpdateInstaller.PrepareAsync(AvailableUpdate, progress, CancellationToken.None);
            InstallStatus = "校验完成，正在重启更新……"; Changed?.Invoke();
            await UpdateInstaller.StartHelperAsync(jobPath);
            await ExitForUpdate();
        } catch (Exception ex) {
            UpdateError = ex is System.IO.IOException ? ex.Message : "更新未完成，当前版本和账号数据保留，请稍后重试。";
            InstallStatus = null; IsInstalling = false; Changed?.Invoke();
        }
    }
}
