using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexAccountSwitcher;

public partial class App : Application
{
    public const string DisplayName = "Star Codex Switcher";
    // Keep the existing storage identity when changing the display and executable names.
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Account Switcher");
    public static Uri ResourceUri(string name) => new($"pack://application:,,,/{typeof(App).Assembly.GetName().Name};component/{name}");
    public static Uri IconUri { get; } = ResourceUri("app.ico");
    private readonly bool startRuntime;
    public App() : this(true) { }
    public App(bool startRuntime) { this.startRuntime = startRuntime; }
    private Mutex? instance;
    private bool ownsMutex;
    private EventWaitHandle? activateWindow;
    private RegisteredWaitHandle? activationListener;
    private EventWaitHandle? installerQuit;
    private RegisteredWaitHandle? installerListener;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private Icon? icon;
    private string? trayIconVariant;
    private System.Drawing.Size trayIconSize;
    private int? trayIconPercent;
    private GuardedAccountClient? client;
    private MainWindow? window;
    private DispatcherTimer? updates;
    private bool exiting;
    private NativeSettings? themeSettings;
    private bool observingTheme;
    public bool UsesDarkPalette { get; private set; }
    public string AccentPreference { get; private set; } = "purple";
    public event Action? PaletteChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startRuntime) return;
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update") {
            try { await UpdateInstaller.RunHelperAsync(e.Args[1]); Shutdown(); }
            catch (Exception ex) { ShowStartupError(ex.Message, "system"); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--cleanup-install-shortcuts") {
            try { InstalledMigration.CleanupShortcuts(Environment.ProcessPath!, e.Args[1], e.Args[2]); Shutdown(); }
            catch { Shutdown(1); }
            return;
        }
        try { if (await UpdateInstaller.RedirectLegacyLaunchAsync(e.Args)) { Shutdown(); return; } }
        catch { Shutdown(1); return; }
        if (e.Args.Contains("--self-test")) { await SelfTestAsync(); return; }
        try {
            var configuredDataPath = Environment.GetEnvironmentVariable("CODEX_SWITCHER_DATA_HOME")
                ?? DefaultDataDirectory;
            var preparingInstall = e.Args.Contains("--prepare-install");
            string dataPath;
            try { dataPath = SecureFiles.ResolveStorageRoot(configuredDataPath); }
            catch (IOException) when (preparingInstall) {
                // A broken directory must not prevent the installer from asking
                // an older instance, keyed by the alias, to exit safely.
                dataPath = Path.GetFullPath(configuredDataPath);
            }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataPath).ToUpperInvariant())));
            var legacyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configuredDataPath).ToUpperInvariant())));
            if (preparingInstall) {
                // Installer may ask the running switcher to exit, but never interrupts account work.
                // Older versions keyed IPC by the configured alias. An update must
                // still ask that instance to exit after roots become canonical.
                foreach (var instanceKey in new[] { key, legacyKey }.Distinct()) {
                    try {
                        using var running = Mutex.OpenExisting(@"Local\CodexAccountSwitcher-" + instanceKey);
                        try { using var quit = EventWaitHandle.OpenExisting(@"Local\CodexAccountSwitcher-InstallQuit-" + instanceKey); quit.Set(); }
                        catch (WaitHandleCannotBeOpenedException) { Shutdown(10); return; }
                        var released = false;
                        for (var i = 0; i < 100; i++) {
                            try { if (running.WaitOne(0)) { running.ReleaseMutex(); released = true; break; } }
                            catch (AbandonedMutexException) { running.ReleaseMutex(); released = true; break; }
                            await Task.Delay(100);
                        }
                        if (!released) { Shutdown(10); return; }
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                }
                Shutdown(); return;
            }
            if (legacyKey != key) {
                try {
                    using var legacy = Mutex.OpenExisting(@"Local\CodexAccountSwitcher-" + legacyKey);
                    try { using var activate = EventWaitHandle.OpenExisting(@"Local\CodexAccountSwitcher-Activate-" + legacyKey); activate.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    Shutdown(); return;
                }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            instance = new Mutex(true, @"Local\CodexAccountSwitcher-" + key, out ownsMutex);
            activateWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexAccountSwitcher-Activate-" + key);
            if (!ownsMutex) { activateWindow.Set(); Shutdown(); return; }
            installerQuit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexAccountSwitcher-InstallQuit-" + key);
            installerListener = ThreadPool.RegisterWaitForSingleObject(installerQuit, (_, _) => Dispatcher.BeginInvoke(new Action(async () => {
                if (!exiting && window != null && !window.IsBusy && client?.State.IsAddingAccount != true) await QuitAsync();
            })), null, Timeout.Infinite, false);
            client = new GuardedAccountClient(HostRuntime.Resolve(Assembly.GetExecutingAssembly()), dataPath);
            var native = new NativeSettings();
            UseThemeSettings(native);
            native.ExitForUpdate = () => QuitAsync(forUpdate: true);
            window = new MainWindow(client, native); MainWindow = window;
            activationListener = ThreadPool.RegisterWaitForSingleObject(activateWindow, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => { if (!exiting) { ApplyTheme(); window.OpenWindow(); } })),
                null, Timeout.Infinite, false);
            tray = new Forms.NotifyIcon { Text = DisplayName };
            RefreshTrayIcon();
            tray.Visible = true;
            trayMenu = new Forms.ContextMenuStrip();
            var openItem = new Forms.ToolStripMenuItem(DisplayName);
            openItem.Click += (_, _) => { ApplyTheme(); window.OpenWindow(); };
            var quitItem = new Forms.ToolStripMenuItem();
            quitItem.Click += (_, _) => _ = QuitAsync();
            trayMenu.Items.Add(openItem); trayMenu.Items.Add(new Forms.ToolStripSeparator()); trayMenu.Items.Add(quitItem);
            trayMenu.Opening += (_, _) => { quitItem.Text = WindowsLocalization.IsChinese(client.State.Settings.Language) ? "退出应用" : "Quit"; quitItem.Enabled = !window.IsBusy; };
            tray.ContextMenuStrip = trayMenu;
            tray.MouseClick += (_, args) => {
                if (args.Button == Forms.MouseButtons.Left) { ApplyTheme(); window.OpenWindow(); }
            };
            client.Changed += RefreshTray;
            await client.InitializeAsync();
            if (!e.Args.Contains("--background")) window.OpenWindow();
            await UpdateInstaller.SignalReadyAsync(e.Args);
            updates = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            updates.Tick += async (_, _) => { if (native.AutomaticallyCheckUpdates && native.HasUpdateServer) await native.CheckUpdatesAsync(); };
            updates.Start();
            if (native.AutomaticallyCheckUpdates && native.HasUpdateServer) _ = native.CheckUpdatesAsync();
        } catch (Exception ex) {
            ShowStartupError(ex.Message, client?.State.Settings.Language ?? "simplifiedChinese");
            await QuitAsync();
        }
    }

    private static void ShowStartupError(string error, string language)
    {
        var message = WindowsLocalization.Error(error, language);
        if (WindowsLocalization.SaveDiagnostic(error) is { } path)
            message += WindowsLocalization.IsChinese(language) ? "\n\n脱敏诊断已保存：" + path : "\n\nSanitized diagnostic saved: " + path;
        MessageBox.Show(message, DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RefreshTray()
    {
        if (client == null || tray == null) return;
        RefreshTrayIcon();
        var active = client.State.Accounts.FirstOrDefault(row => row.Profile.Id == client.State.ActiveAccountID);
        var percent = ActiveTrayPercent();
        var label = active == null ? DisplayName : active.Profile.DisplayName + (percent != null ? " · " + percent + "%" : "");
        tray.Text = label.Length > 63 ? label[..63] : label;
    }
    private int? ActiveTrayPercent()
    {
        if (client?.State.Settings.ShowsMenuBarPercentage != true) return null;
        var state = client.State;
        var usage = state.Accounts.FirstOrDefault(row => row.Profile.Id == state.ActiveAccountID)?.Usage;
        if (usage == null) return null;
        return state.Settings.StatusBarUsageWindow == "fiveHour"
            ? usage.FiveHourRemainingPercent ?? usage.RemainingPercent
            : usage.RemainingPercent;
    }
    private void RefreshTrayIcon()
    {
        if (tray == null) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        // Taskbar appearance can differ from the application's light/dark theme.
        var variant = key?.GetValue("SystemUsesLightTheme") is int light && light != 0 ? "light" : "dark";
        var size = Forms.SystemInformation.SmallIconSize;
        var percent = ActiveTrayPercent();
        if (trayIconVariant == variant && trayIconSize == size && trayIconPercent == percent) return;
        Icon replacement;
        if (percent is { } value) {
            replacement = TrayUsageIcon.Create(value, variant == "light", size);
        } else {
            var uri = ResourceUri($"tray-{variant}.ico");
            using var resource = GetResourceStream(uri).Stream;
            using var original = new Icon(resource, size);
            replacement = (Icon)original.Clone();
        }
        tray.Icon = replacement;
        icon?.Dispose(); icon = replacement;
        trayIconVariant = variant; trayIconSize = size; trayIconPercent = percent;
    }
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!exiting) { ApplyTheme(); RefreshTrayIcon(); } }));
    }
    private static bool IsDark() {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    public void UseThemeSettings(NativeSettings settings)
    {
        if (ReferenceEquals(themeSettings, settings)) return;
        if (themeSettings != null) {
            themeSettings.ThemePreferenceChanged -= ApplyTheme;
            themeSettings.AccentPreferenceChanged -= ApplyTheme;
        }
        themeSettings = settings;
        settings.ThemePreferenceChanged += ApplyTheme;
        settings.AccentPreferenceChanged += ApplyTheme;
        if (!observingTheme) { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; observingTheme = true; }
        ApplyTheme();
    }
    private void ApplyTheme() => ApplyThemePreference(IsDark());
    public void ApplyThemePreference(bool systemDark) => ApplyPalette(themeSettings?.ThemePreference switch {
        "dark" => true, "light" => false, _ => systemDark
    });
    public void ApplyPalette(bool dark) {
        UsesDarkPalette = dark;
#pragma warning disable WPF0001 // The app already uses the Fluent ThemeMode in App.xaml.
        ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        foreach (var pair in new[] { ("ListSurface", dark ? "#152438" : "#FBFDFF"), ("Surface", dark ? "#0C192C" : "#EEF3FB"), ("Ink", dark ? "#F1F5FC" : "#17243A"),
            ("SectionHeader", dark ? "#1C304B" : "#E4EDF9"), ("ButtonSurface", dark ? "#20334C" : "#F4F7FC"), ("InputSurface", dark ? "#132238" : "#F7FAFF"),
            ("Muted", dark ? "#ABBBD0" : "#5E6F86"), ("Schedule", dark ? "#CBDDF5" : "#344B6A"), ("Line", dark ? "#31465F" : "#CBD7E8"), ("Track", dark ? "#405169" : "#D6DFEB"),
            ("Hover", dark ? "#263E5B" : "#E2ECFA"),
            ("Success", dark ? "#61DD81" : "#117B38"), ("Warning", dark ? "#F3B94C" : "#9B5C00"), ("WarningSurface", dark ? "#3B3020" : "#FFF4D9"),
            ("Danger", dark ? "#FF777F" : "#C63943"), ("DangerSurface", dark ? "#3B202C" : "#FFF0F1"), ("OnAccent", "#FFFFFF") })
            Resources[pair.Item1] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(pair.Item2));
        ApplyAccent(dark);
        foreach (var plan in new[] { "free", "plus", "pro100", "pro200", "pro500", "team", "business", "enterprise", "edu", "go", "unknown" }) {
            var badge = AccountVisuals.PlanBadge(plan, dark);
            var basis = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(badge.Background);
            static System.Windows.Media.Color Mix(System.Windows.Media.Color value, byte destination, double weight) => System.Windows.Media.Color.FromRgb(
                (byte)Math.Round(value.R * (1 - weight) + destination * weight), (byte)Math.Round(value.G * (1 - weight) + destination * weight), (byte)Math.Round(value.B * (1 - weight) + destination * weight));
            Resources["PlanBackground_" + plan] = new LinearGradientBrush(new GradientStopCollection {
                new(Mix(basis, 255, plan == "pro500" ? 0.07 : 0.20), 0), new(basis, 0.52), new(Mix(basis, 0, 0.04), 1)
            }, new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            Resources["PlanForeground_" + plan] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(badge.Foreground));
            Resources["PlanBorder_" + plan] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(badge.Border));
        }
        PaletteChanged?.Invoke();
    }
    private static (string Start, string End, string LightInk, string DarkInk) AccentColors(string preference) => NativeSettings.NormalizeAccentPreference(preference) switch {
        "purple" => ("#7040CE", "#963C9D", "#6D35B1", "#C2A5FF"),
        "teal" => ("#08765F", "#087B83", "#087365", "#70DCC8"),
        "orange" => ("#A94B12", "#BB441D", "#AC471B", "#FFBD8C"),
        _ => ("#1457C5", "#08739E", "#135FBE", "#7DC8FF")
    };
    public static LinearGradientBrush CreateAccentBrush(string preference)
    {
        var colors = AccentColors(preference);
        var brush = new LinearGradientBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors.Start),
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors.End), new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        brush.Freeze(); return brush;
    }
    private void ApplyAccent(bool dark)
    {
        AccentPreference = themeSettings?.AccentPreference ?? "purple";
        var colors = AccentColors(AccentPreference);
        var accent = CreateAccentBrush(AccentPreference);
        Resources["Accent"] = accent;
        Resources["AccentInk"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? colors.DarkInk : colors.LightInk));
        var surface = ((SolidColorBrush)Resources["ListSurface"]).Color;
        static System.Windows.Media.Color Tint(System.Windows.Media.Color basis, System.Windows.Media.Color accent, double weight) => System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(basis.R * (1 - weight) + accent.R * weight), (byte)Math.Round(basis.G * (1 - weight) + accent.G * weight),
            (byte)Math.Round(basis.B * (1 - weight) + accent.B * weight));
        var ink = ((SolidColorBrush)Resources["AccentInk"]).Color;
        Resources["AccentBorder"] = dark ? new LinearGradientBrush(Tint(accent.GradientStops[0].Color, ink, 0.65),
            Tint(accent.GradientStops[1].Color, ink, 0.65), new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)) : accent;
        Resources["Selected"] = new LinearGradientBrush(Tint(surface, accent.GradientStops[0].Color, dark ? 0.20 : 0.06),
            Tint(surface, accent.GradientStops[1].Color, dark ? 0.12 : 0.02), new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
    }
    private async Task QuitAsync(bool forUpdate = false) {
        if (exiting || (!forUpdate && window?.IsBusy == true)) return;
        exiting = true; updates?.Stop();
        activationListener?.Unregister(null);
        installerListener?.Unregister(null);
        if (client != null) { client.Changed -= RefreshTray; await client.DisposeAsync(); }
        window?.CloseForExit();
        Shutdown();
    }
    private async Task SelfTestAsync() {
        var root = Path.Combine(Path.GetTempPath(), "switcher-package-check-" + Guid.NewGuid());
        var result = "PASS"; var exitCode = 0;
        try {
            Directory.CreateDirectory(Path.Combine(root, "active"));
            var host = HostRuntime.Resolve(Assembly.GetExecutingAssembly());
            await using var core = new GuardedAccountClient(host, Path.Combine(root, "data"), Path.Combine(root, "active"));
            await core.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await core.CommandAsync("fiveHour", value: true).WaitAsync(TimeSpan.FromSeconds(10));
            if (!File.Exists(Path.Combine(root, "data", "settings.json"))) throw new IOException("Shared settings were not persisted.");
            if (File.Exists(Path.Combine(root, "active", "auth.json"))) throw new IOException("Empty startup created an unexpected credential.");
            result += ": bundled .NET, extracted Swift runtime, guarded account client and isolated settings.";
        } catch (Exception ex) { result = "FAIL: " + ex; exitCode = 1; }
        finally { if (Directory.Exists(root)) SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root); }
        if (Environment.GetEnvironmentVariable("CODEX_SWITCHER_SELF_TEST_RESULT") is { } output) File.WriteAllText(output, result);
        if (exitCode == 0) await UpdateInstaller.SignalReadyAsync(Environment.GetCommandLineArgs());
        Shutdown(exitCode);
    }
    protected override void OnExit(ExitEventArgs e) {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (themeSettings != null) {
            themeSettings.ThemePreferenceChanged -= ApplyTheme;
            themeSettings.AccentPreferenceChanged -= ApplyTheme;
        }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        trayMenu?.Dispose();
        icon?.Dispose();
        activateWindow?.Dispose();
        installerQuit?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex(); instance?.Dispose();
        base.OnExit(e);
    }
}
