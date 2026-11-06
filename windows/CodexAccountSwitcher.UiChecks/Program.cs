using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--test-quota-refresh") {
            try {
                var app = new App(false); app.InitializeComponent(); app.ApplyPalette(false);
                QuotaRefreshChecks.Run(app, Path.GetFullPath(args.ElementAtOrDefault(1) ?? "windows/artifacts/quota-refresh"),
                    new NativeSettings(@"Software\StarCodexSwitcher.UiChecks\QuotaRefresh-Restore")); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Length == 4 && args[0] == "--test-update-prompt-child") {
            try { return UpdatePromptChecks.RunChild(args[1], args[2], args[3] == "yes"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Length >= 1 && args[0] == "--test-update-prompts") {
            try {
                var target = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "windows/artifacts/update-prompts");
                var fixtureApp = new App(false); fixtureApp.InitializeComponent(); fixtureApp.ApplyPalette(false);
                UpdatePromptChecks.Run(fixtureApp, target); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--check-plan-badges")) {
            try { PlanVisualChecks.Run(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "--check-subscription") {
            try {
                using var reader = new OfficialSubscriptionReader();
                var result = reader.ReadAsync(LoginCredential.Read(args[1]), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: subscription expiry={result.ActiveUntil?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown"}, plan={result.PlanType ?? "unknown"}, source={result.Source}; read-only GET."); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        }
        if (args.Length == 2 && args[0] == "--check-reset-credits") {
            try {
                using var reader = new OfficialResetCredits();
                var result = reader.ReadAsync(LoginCredential.Read(args[1]), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: reset credits={result.AvailableCount?.ToString() ?? "unknown"}, earliest expiry={result.EarliestExpiry?.ToString("O") ?? "not provided"}; read-only GET."); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        }
        if (args.Length == 2 && args[0] == "--check-usage") {
            try {
                using var reader = new OfficialUsageReader();
                var result = reader.ReadAsync(LoginCredential.Read(args[1]), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: weekly={result.RemainingPercent}%, fiveHour={result.FiveHourRemainingPercent?.ToString() ?? "not provided"}, resets={result.ResetCreditsRemaining?.ToString() ?? "unknown"}"); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        }
        if (args.Length == 3 && args[0] == "--test-update") {
            try { UpdateChecks.RunAsync(args[1], args[2]).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--check-update")) {
            var settings = new NativeSettings();
            settings.CheckUpdatesAsync().GetAwaiter().GetResult();
            if (settings.UpdateError != null || !settings.HasChecked) { Console.Error.WriteLine(settings.UpdateError); return 1; }
            Console.WriteLine("PASS: live fixed update feed; installed=" + settings.Version + "; available=" + (settings.AvailableUpdate?.Version.ToString() ?? "none"));
            return 0;
        }
        if (args.Contains("--test-update-http")) {
            try { UpdateHttpChecks.RunAsync().GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("windows", "artifacts", "ui"));
        var settingsKey = @"Software\StarCodexSwitcher.UiChecks\" + Guid.NewGuid().ToString("N");
        try {
            QuotaRules();
            VisualRules();
            PlanVisualChecks.Run();
            UpdateRules();
            UpdateHttpChecks.RunAsync().GetAwaiter().GetResult();
            Directory.CreateDirectory(output);
            var app = new App(false); app.InitializeComponent(); app.ApplyPalette(false);
            ThemeChecks.Run(app);
            var native = new NativeSettings(settingsKey) { ThemePreference = "light" };
            var client = new FixtureClient();
            var presentationNow = DateTimeOffset.Parse("2026-11-06T12:00:00+08:00");
            var window = new MainWindow(client, native, () => presentationNow);
            Assert(window.Title == App.DisplayName, "The main window uses the agreed Star Codex Switcher display name.");
            Assert(window.Icon != null, "The taskbar and title bar must use the packaged application logo.");
            BrandingChecks.Run();
            LanguageChecks.Run(native, output);
            SubscriptionPresentationChecks.Run(native, output);
            Render(window, Path.Combine(output, "accounts-zh.png"));
            Assert(window.ResizeMode == ResizeMode.CanResize && window.MinWidth >= 420,
                "Home is a normal resizable desktop window with a usable minimum width.");
            Assert(window.ShowInTaskbar && !window.Topmost && window.WindowStyle == WindowStyle.SingleBorderWindow,
                "Windows must use a normal titled window visible in the taskbar.");
            var other = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            other.Show(); other.Activate(); other.Close();
            Assert(window.IsVisible, "Losing focus must not dismiss the account window.");
            var active = All<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "personal@example.test");
            active.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.IsVisible && client.Commands.Count == 0, "Clicking the active account keeps the window open.");
            Assert(All<TextBlock>(window).Count(text => text.Text.Contains("@example.test")) == 2, "Home must use full emails as account titles.");
            Assert(All<TextBlock>(window).Where(text => text.Text.Contains("@example.test")).All(text =>
                text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.CharacterEllipsis && Equals(text.ToolTip, text.Text)),
                "Account emails must stay on one line and expose the full address in a tooltip.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "当前使用"), "Home clearly identifies the current account.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "重置 2 次"), "The account action displays its reported reset count.");
            Assert(All<TextBlock>(window).Any(text => text.Text.StartsWith("重置 未知")), "Missing reset count must not be shown as zero.");
            Assert(All<TextBlock>(window).Any(text => text.Text.StartsWith("有效期：2099-10-17 ") && text.ToolTip is string),
                "Home shows the subscription date without a parenthetical label and exposes its check time on hover.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "有效期：未知"), "Missing subscription date stays unknown.");
            var target = All<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "studio@example.test");
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.CurrentPage == "switch", "Selecting a row must open an in-place confirmation.");
            Assert(client.Commands.Count == 0, "Selection must not switch before confirmation.");
            Render(window, Path.Combine(output, "switch-zh.png"));
            Assert(All<TextBlock>(window).Any(text => text.Text.Contains("强制结束") && text.Text.Contains("未保存的状态可能丢失")),
                "Windows confirmation must explain forced termination before switching.");
            Assert(All<Button>(window).Where(button => System.Windows.Automation.AutomationProperties.GetName(button) == "切换账号").All(button => !button.IsDefault), "Enter must not accidentally confirm a newly opened switch page.");
            All<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.CurrentPage == "accounts" && client.Commands.Count == 0, "Cancel returns without any switch command.");
            window.Navigate("manage"); Render(window, Path.Combine(output, "accounts-actions-zh.png"));
            Assert(window.CurrentPage == "accounts", "Legacy management navigation resolves to the integrated account list.");
            Assert(All<Button>(window).Any(button => Name(button) == "粘贴 JSON"),
                "The account list exposes import directly.");
            var pasteWindow = new JsonImportWindow(client) { Owner = window };
            Assert(pasteWindow.Title == "导入账号 · " + App.DisplayName, "Import dialogs use the same product display name.");
            Render(pasteWindow, Path.Combine(output, "paste-json-zh.png"));
            var editor = All<TextBox>(pasteWindow).Single();
            var importButton = All<Button>(pasteWindow).Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "导入粘贴的 JSON");
            Assert(editor.AcceptsReturn && editor.AcceptsTab && !editor.IsUndoEnabled && !importButton.IsEnabled,
                "JSON editor must accept multiline input and disable empty submission and undo history.");
            editor.Text = "  \n  "; Assert(!importButton.IsEnabled, "Whitespace cannot be imported.");
            editor.Text = "[{}]";
            Assert(importButton.IsEnabled, "Pasting text enables import.");
            importButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(editor.Text == "" && !importButton.IsEnabled && !pasteWindow.IsVisible,
                "Successful import must clear sensitive text and close the dialog automatically.");
            pasteWindow.Close();
            client.FailImport = true;
            var failedImport = new JsonImportWindow(client) { Owner = window };
            failedImport.Show(); failedImport.UpdateLayout();
            var failedEditor = All<TextBox>(failedImport).Single(); failedEditor.Text = "invalid json";
            All<Button>(failedImport).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "导入粘贴的 JSON").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(failedImport.IsVisible && failedEditor.Text == "invalid json" && All<TextBlock>(failedImport).Any(t => t.Text == "模拟格式错误"), "Import failure stays open and retains text for correction.");
            failedImport.Close(); client.FailImport = false;
            Assert(All<TextBlock>(window).Count(text => text.Text.Contains("@example.test")) == 2, "Manage must show account email addresses.");
            Assert(!All<TextBlock>(window).Any(text => text.Text is "Personal" or "Studio"), "Manage must not duplicate the email with a display-name row.");
            var addButton = All<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == client.State.Text("add_account"));
            Assert(addButton.IsEnabled, "Add account is available directly on the account list.");
            window.Navigate("settings"); Render(window, Path.Combine(output, "settings-zh.png"));
            Assert(All<TextBlock>(window).Any(text => text.Text == "当前版本：" + native.DisplayVersion), "The settings footer uses the public display version and Windows-localized label.");
            Assert(All<TextBlock>(window).Any(t => t.Text == "托盘显示额度") && !All<TextBlock>(window).Any(t => t.Text == "status_bar_usage_window"), "The tray quota label must have a Chinese fallback when the host omits its translation.");
            Assert(!All<CheckBox>(window).Any(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == "显示 5 小时用量"),
                "Five-hour visibility is inferred from account data, without a settings toggle.");
            var experiment = All<CheckBox>(window).Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == "到期前自动使用重置卡");
            Assert(experiment.IsChecked == false, "Auto-reset must default to off.");
            experiment.IsChecked = true; experiment.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert(client.Commands.SequenceEqual(new[] { "autoReset:True" }),
                "The auto-reset toggle must send the setting to the shared host.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "重置卡到期前 10 分钟，自动使用所有已保存账号的可用重置卡；应用须运行且电脑保持唤醒。"),
                "Settings must disclose real reset consumption and runtime requirements.");
            Assert(!All<TextBlock>(window).Any(text => text.Text.Contains("reset", StringComparison.OrdinalIgnoreCase)), "Chinese settings replace legacy host reset wording with complete Chinese labels.");
            Assert(!All<TextBlock>(window).Any(text => text.Text is "保存" or "Save"), "Settings must not have a Save flow.");
            client.State = client.State with { Settings = client.State.Settings with { ShowsFiveHourUsage = true, AutoResetEnabled = true } };
            window.Navigate("settings"); Render(window, Path.Combine(output, "settings-auto-reset-enabled-zh.png"));
            Assert(All<CheckBox>(window).Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == "到期前自动使用重置卡").IsChecked == true,
                "The auto-reset toggle must reflect the persisted setting.");
            window.Navigate("accounts"); Render(window, Path.Combine(output, "accounts-five-hour-zh.png"));
            Assert(All<TextBlock>(window).Count(text => text.Text == "5 小时剩余") == 2, "Five-hour view must be per account.");
            var original = client.State;
            var fixedNow = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(8));
            var emails = new[] { "alex@example.com", "studio@example.com", "dev@example.com", "backup@example.com" };
            var weekly = new[] { 66, 84, 32, 18 }; var five = new[] { 91, 45, 12, 90 }; var days = new[] { 29, 6, 2, -1 }; var resets = new[] { 3, 0, 1, 0 };
            var rows = emails.Select((email, index) => new AccountRow(new Profile(Guid.NewGuid(), email, email), email[..1].ToUpperInvariant(),
                new Usage(weekly[index], fixedNow.AddDays(6).AddHours(2), five[index], fixedNow.AddHours(6).AddMinutes(38), resets[index]), null,
                "loaded", new SubscriptionSnapshot(fixedNow.AddDays(days[index]), fixedNow, "accounts_check", true), SubscriptionStatus: "verified")).ToArray();
            var showcaseClient = new FixtureClient { State = original with { Accounts = rows, ActiveAccountID = rows[0].Profile.Id, Settings = original.Settings with { ShowsFiveHourUsage = true } } };
            var showcase = new MainWindow(showcaseClient, native, now: () => fixedNow);
            Render(showcase, Path.Combine(output, "design-light.png"));
            Assert(All<Button>(showcase).Count(button => Name(button) == "重置额度") == 4 && All<TextBlock>(showcase).Count(t => t.Text == "重置 0 次") == 2, "Every row includes reset counts, including zero.");
            Assert(All<TextBlock>(showcase).Any(t => t.Text.Contains("已到期")), "Expired accounts must have textual status, not color alone.");
            Assert(All<TextBlock>(showcase).Single(t => t.Text == "12%").Foreground == app.Resources["Danger"], "Low remaining quota uses the danger color.");
            Assert(All<TextBlock>(showcase).Single(t => t.Text == "45%").Foreground == app.Resources["Warning"], "Medium remaining quota uses warning color.");
            Assert(All<TextBlock>(showcase).Single(t => t.Text == "91%").Foreground == app.Resources["Success"], "High remaining quota uses success color.");
            // An old JWT expiry cannot prove that a renewed subscription has ended.
            showcaseClient.State = showcaseClient.State with { Accounts = [rows[3] with {
                Subscription = new SubscriptionSnapshot(fixedNow.AddDays(-5), fixedNow.AddDays(-20)),
                SubscriptionStatus = "unavailable", SubscriptionError = "暂时无法核验订阅" }] };
            showcase.Navigate("accounts"); Render(showcase, Path.Combine(output, "subscription-unverified.png"));
            Assert(All<TextBlock>(showcase).Any(t => t.Text == "有效期：待核验") &&
                !All<TextBlock>(showcase).Any(t => t.Text.Contains("已到期")), "A stale JWT date must never claim that the account is expired.");
            showcaseClient.State = showcaseClient.State with { Accounts = rows };
            app.ApplyPalette(true); showcase.Navigate("accounts"); Render(showcase, Path.Combine(output, "design-dark.png"));
            app.ApplyPalette(false);
            showcase.Width = 520; showcase.MaxHeight = 500; showcase.Navigate("accounts"); Render(showcase, Path.Combine(output, "design-small.png"));
            Assert(All<ScrollViewer>(showcase).Any(v => v.ScrollableHeight > 0), "Small work areas must scroll rather than hide accounts.");
            showcaseClient.State = showcaseClient.State with { Accounts = [rows[0] with { Profile = rows[0].Profile with { Email = "very-long-account-address-for-narrow-window@example.test" }, Usage = null, Subscription = null }], IsMutating = true };
            showcase.Navigate("accounts"); Render(showcase, Path.Combine(output, "unknown-busy.png"));
            Assert(All<TextBlock>(showcase).Any(t => t.Text.StartsWith("重置 未知")), "Missing usage must still show an unknown reset count.");
            Assert(All<Button>(showcase).Any(b => Name(b) == "粘贴 JSON") &&
                !All<Button>(showcase).Where(b => Name(b) == "粘贴 JSON").Any(b => b.IsEnabled), "Import is disabled during mutation.");
            showcase.CloseForExit();
            client.State = original;
            ResponsiveAndReset(window, client, output);
            AccentThemes(app, window, client, native, settingsKey, output);
            AddAccountFlow(app, window, client, native, output);
            SettingsScroll(window, client, native, output);
            var closed = false; window.Closed += (_, _) => closed = true;
            var beforeClose = client.Commands.Count;
            window.Close();
            Assert(!window.IsVisible && !closed, "Title-bar close must hide the window without disposing the running app.");
            Assert(client.Commands.Count == beforeClose, "Hiding must not send account commands.");
            window.OpenWindow();
            Assert(window.IsVisible && !closed, "The tray must be able to reopen the same window.");
            client.State = client.State with { IsAddingAccount = true };
            window.Navigate("accounts");
            window.Hide();
            window.OpenWindow();
            Assert(window.CurrentPage == "accounts", "Reopening a pending login keeps the integrated list and cancellation control.");
            Render(window, Path.Combine(output, "pending-sign-in-reopened-zh.png"));
            Assert(All<Button>(window).Any(button => System.Windows.Automation.AutomationProperties.GetName(button) == client.State.Text("cancel_add_account") && button.IsEnabled),
                "The pending login must remain cancellable after reopening.");
            client.State = client.State with { IsAddingAccount = false };
            client.State = client.State with { IsMutating = true };
            window.Close();
            Assert(!window.IsVisible && !closed, "An active operation can continue while the window is hidden.");
            client.State = client.State with { IsMutating = false };
            RefreshResetChecks.Run(app, Path.Combine(output, "refresh-reset"), native);
            QuotaRefreshChecks.Run(app, Path.Combine(output, "quota-refresh"), native);
            UpdatePromptChecks.Run(app, Path.Combine(output, "update-prompts"), native);
            window.CloseForExit();
            Assert(closed, "Explicit application exit must release the window.");
            Console.WriteLine("PASS: native taskbar window, focus persistence, switch confirmation, integrated management, immediate settings and automatic five-hour usage.");
            Console.WriteLine(output);
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(settingsKey, throwOnMissingSubKey: false); }
    }
    private static void QuotaRules() {
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00+08:00");
        var lite = """
            {"plan_type":"prolite","rate_limit":{"primary_window":{"used_percent":0,"limit_window_seconds":604800,"reset_after_seconds":604800},"secondary_window":null},"rate_limit_reset_credits":{"available_count":0}}
            """;
        var usage = OfficialUsageReader.Parse(lite, now);
        Assert(usage.RemainingPercent == 100 && usage.FiveHourRemainingPercent == null && usage.ResetCreditsRemaining == 0, "Pro Lite primary weekly window is supported without inventing a five-hour quota.");
        Assert(usage.ResetsAt == now.AddDays(7), "Relative reset time is interpreted from observation time.");
        var dual = """
            {"rate_limit":{"primary_window":{"used_percent":75,"limit_window_seconds":18000,"reset_at":1794558266},"secondary_window":{"used_percent":20,"limit_window_seconds":604800,"reset_at":1794558266}}}
            """;
        usage = OfficialUsageReader.Parse(dual, now);
        Assert(usage.RemainingPercent == 80 && usage.FiveHourRemainingPercent == 25 && usage.ResetCreditsRemaining == null, "Dual windows and unknown reset credits remain distinct.");
        var failed = false; try { OfficialUsageReader.Parse("{\"rate_limit\":{}}", now); } catch (IOException) { failed = true; }
        Assert(failed, "Missing windows cannot become a false zero quota.");
    }
    private static void VisualRules() {
        Assert(AccountVisuals.QuotaTone(null) == "Muted", "Unknown quota is neutral.");
        foreach (var value in new[] { 0, 1, 20 }) Assert(AccountVisuals.QuotaTone(value) == "Danger", "Low quota boundary.");
        foreach (var value in new[] { 21, 50 }) Assert(AccountVisuals.QuotaTone(value) == "Warning", "Medium quota boundary.");
        foreach (var value in new[] { 51, 100 }) Assert(AccountVisuals.QuotaTone(value) == "Success", "High quota boundary.");
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00+08:00");
        Assert(AccountVisuals.ExpiryTone(null, now) == "Muted", "Unknown expiry neutral.");
        foreach (var days in new[] { -1, 0, 3 }) Assert(AccountVisuals.ExpiryTone(now.AddDays(days), now) == "Danger", "Expiry urgent boundary.");
        foreach (var days in new[] { 4, 7 }) Assert(AccountVisuals.ExpiryTone(now.AddDays(days), now) == "Warning", "Expiry warning boundary.");
        Assert(AccountVisuals.ExpiryTone(now.AddDays(7).AddSeconds(1), now) == "Success", "More than seven days is normal.");
        Assert(AccountVisuals.ExpiryRemaining(now, now, true) == "已到期", "Exact expiry is expired.");
        Assert(AccountVisuals.ExpiryRemaining(now.AddHours(2), now, true) == "剩余 2 小时", "Sub-day expiry displays remaining hours.");
        Assert(AccountVisuals.ExpiryRemaining(now.AddSeconds(1), now, true) == "剩余 1 小时", "A positive partial hour rounds upward.");
        Assert(AccountVisuals.ExpiryRemaining(now.AddDays(25).AddMinutes(1), now, true) == "剩余 26 天", "Remaining days round upward from elapsed hours, not calendar boundaries.");
        Assert(AccountVisuals.ExpiryRemaining(now.AddDays(100), now, true) == "剩余 99+ 天", "Long subscriptions display 99+ days.");
        Assert(AccountVisuals.QuotaCountdown(now.AddHours(2).AddMinutes(18), now, true) == "剩余 2 小时 18 分钟", "Hourly renewal displays hours and minutes.");
        Assert(AccountVisuals.QuotaCountdown(now.AddDays(5).AddHours(21), now, true) == "剩余 5 天 21 小时", "Weekly renewal displays days and hours.");
        Assert(AccountVisuals.QuotaCountdown(now.AddSeconds(59), now, true) == "剩余不足 1 分钟", "A positive sub-minute interval never appears expired.");
        Assert(AccountVisuals.QuotaCountdown(now.AddMinutes(1), now, true) == "剩余 1 分钟", "The minute boundary is explicit.");
        Assert(AccountVisuals.QuotaCountdown(now, now, true) == "已到恢复时间，待刷新确认" && AccountVisuals.QuotaCountdown(now.AddDays(-1), now, false).Contains("refresh to confirm"), "A passed renewal time does not claim quota was restored.");
        Assert(AccountVisuals.QuotaCountdown(null, now, true) == "剩余时间未知" && AccountVisuals.QuotaCountdown(null, now, false) == "Time remaining unknown", "Missing renewal time remains unknown in both languages.");
        Assert(AccountVisuals.QuotaCountdown(now.AddHours(2).AddMinutes(18), now.AddMinutes(1), false) == "2h 17m left", "A later presentation refresh advances the countdown.");
    }
    private static void UpdateRules() {
        Assert(NativeSettings.FormatVersion(new Version("1.0.0.0")) == "1.0.0" && NativeSettings.FormatVersion(new Version("1.0.1.0")) == "1.0.1", "New releases omit a zero revision in user-facing version labels.");
        Assert(NativeSettings.FormatVersion(new Version("0.1.27.3")) == "0.1.27.3" && NativeSettings.FormatVersion(new Version("0.1.27.4")) == "0.1.27.4", "Existing four-part release labels remain unambiguous.");
        Assert(NativeSettings.FormatVersion(new Version("1.0.0")) == "1.0.0" && new Version("1.0.0.0") > new Version("0.1.27.5"), "Display normalization preserves normal semantic versions and internal upgrade ordering.");
        var json = "{\"version\":\"0.1.27.4\",\"platform\":\"windows-x64\",\"package_type\":\"portable\",\"download_url\":\"https://github.com/evenstar-2273/star-codex-switcher/releases/download/v0.1.27.4/app.exe\",\"sha256\":\"" + new string('a', 64) + "\",\"notes\":\"test\"}";
        var release = UpdateFeed.Parse(json, "portable");
        Assert(release.Version > new Version("0.1.27.3") && release.Version < new Version("0.1.27.10"), "Four-part versions compare numerically.");
        Assert(UpdateHttp.IsRepositoryAsset(release.DownloadUrl), "Update download belongs to this repository's GitHub Releases.");
        foreach (var invalid in new[] { json.Replace("windows-x64", "macos-arm64"), json.Replace("portable", "installer"), json.Replace("https://", "file:///"), json.Replace("0.1.27.4", "v0.1.27"), json.Replace(new string('a',64), "bad") }) {
            var failed = false; try { UpdateFeed.Parse(invalid, "portable"); } catch { failed = true; } Assert(failed, "Invalid update metadata must be rejected.");
        }
        Assert(NativeSettings.DefaultUpdateFeedUrl == Distribution.FeedUrl, "Updates must use the fixed feed matching the edition.");
        var installed = json.Replace("portable", "installer").Replace("\"notes\":", "\"app_sha256\":\"" + new string('b',64) + "\",\"notes\":");
        Assert(UpdateFeed.Parse(installed, "installer").AppSha256 == new string('b',64), "Installed feed carries the application's checksum independently of setup.");
    }
    private static void ResponsiveAndReset(MainWindow window, FixtureClient client, string output) {
        var saved = client.State;
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(8));
        var plans = new[] { "free", "plus", "prolite", "pro", "promax" };
        var rows = plans.Select((plan, i) => new AccountRow(new Profile(Guid.NewGuid(), plan, plan + "@example.test"), "P",
            new Usage(66, now.AddDays(6), i == 2 ? null : 91, i is 2 or 4 ? null : now.AddHours(3), i == 0 ? 0 : i == 2 ? null : 3, now.AddDays(1), plan),
            null, "loaded", i == 0 ? null : new SubscriptionSnapshot(now.AddDays(29), now, "accounts_check", true), plan, "verified")).ToArray();
        client.State = saved with { Accounts = rows, ActiveAccountID = rows[1].Profile.Id, Settings = saved.Settings with { ShowsFiveHourUsage = false } };
        void AssertResponsiveLayout(double requestedWidth) {
            // Win32 can cap the window at the runner's maximum tracking size. Layout follows
            // the size that was actually arranged, rather than an unapplied Width request.
            var actualWidth = window.ActualWidth;
            var list = All<UniformGrid>(window).Single(grid => grid.Children.Count == rows.Length && grid.Children.OfType<Border>().Count() == rows.Length);
            var expectedColumns = Math.Clamp((int)Math.Floor((list.ActualWidth + 0.01) / 396), 1, 3);
            var cards = list.Children.Cast<FrameworkElement>().ToArray();
            var points = cards.Select(card => card.TranslatePoint(new Point(), list)).ToArray();
            Console.WriteLine($"Responsive layout: requested={requestedWidth:0.##}, actual={actualWidth:0.##}, content={list.ActualWidth:0.##}, columns={window.AccountColumnCount}, card={cards[0].ActualWidth:0.##}.");
            Assert(window.AccountColumnCount == expectedColumns && list.Columns == expectedColumns,
                $"Arranged window width selects the rendered account columns (requested {requestedWidth}, actual {actualWidth}, expected {expectedColumns}, rendered {list.Columns}).");
            for (var i = 0; i < cards.Length; i++) {
                Assert(cards[i].ActualWidth > 0 && points[i].X >= -1 && points[i].X + cards[i].ActualWidth <= list.ActualWidth + 1,
                    "Each account card fits within the arranged list width.");
                Assert(expectedColumns == 1 || cards[i].ActualWidth >= 379, "Automatic columns preserve a usable minimum card width.");
                if (i % expectedColumns != 0) {
                    Assert(Math.Abs(points[i].Y - points[i - 1].Y) <= 1 && points[i].X >= points[i - 1].X + cards[i - 1].ActualWidth - 1,
                        "Cards in the same rendered row sit side by side without overlap.");
                } else if (i >= expectedColumns) {
                    Assert(Math.Abs(points[i].X - points[0].X) <= 1 && points[i].Y >= points[i - expectedColumns].Y + cards[i - expectedColumns].ActualHeight - 1,
                        "The next card row wraps below the previous row and aligns with the first column.");
                }
                var buttons = All<Button>(cards[i]).Where(button => button.IsVisible).ToArray();
                foreach (var button in buttons) {
                    var point = button.TranslatePoint(new Point(), cards[i]);
                    Assert(point.X >= -1 && point.X + button.ActualWidth <= cards[i].ActualWidth + 1,
                        "Account action buttons stay inside each card at column breakpoints.");
                }
                for (var a = 0; a < buttons.Length; a++) for (var b = a + 1; b < buttons.Length; b++) {
                    var first = new Rect(buttons[a].TranslatePoint(new Point(), cards[i]), buttons[a].RenderSize);
                    var second = new Rect(buttons[b].TranslatePoint(new Point(), cards[i]), buttons[b].RenderSize);
                    first.Intersect(second);
                    Assert(first.IsEmpty || first.Width <= 1 || first.Height <= 1, "Actions do not overlap in narrow multi-column cards.");
                }
            }
            Assert(All<TextBlock>(window).Where(text => text.Text.Contains("@example.test")).All(text => text.TextWrapping == TextWrapping.NoWrap &&
                text.TextTrimming == TextTrimming.CharacterEllipsis && Equals(text.ToolTip, text.Text)), "Breakpoint layouts retain single-line email trimming and full address tooltips.");
        }
        foreach (var width in new[] { 480d, 840, 859, 860, 880, 1279, 1280, 1300, 1700, 1309 / 1.5 }) {
            window.Width = width; window.Height = 760;
            window.Navigate("accounts"); Render(window, Path.Combine(output, $"responsive-{width:0.##}.png"));
            AssertResponsiveLayout(width);
            Assert(!All<ScrollViewer>(window).Any(view => view.ComputedHorizontalScrollBarVisibility == Visibility.Visible), "Narrow layout never needs horizontal scrolling.");
            Assert(All<TextBlock>(window).Count(text => text.Text == "5 小时剩余") == 4, "Only accounts with a five-hour window show that quota, regardless of the removed preference.");
            Assert(All<TextBlock>(window).Where(text => text.Text.Contains(" 恢复（")).All(text => text.Text.Contains("剩余") && text.TextWrapping == TextWrapping.Wrap && text.TextTrimming == TextTrimming.None),
                "Renewal timestamps include a countdown that can wrap without truncation on narrow cards.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "恢复时间未知"), "An unknown five-hour renewal time is explicitly identified.");
        }
        // Exercise the actual content-width boundaries, including the window frame and
        // scrollbar rather than assuming physical screen pixels equal WPF layout units.
        var adaptiveGrid = All<AdaptiveCardGrid>(window).Single();
        var frameAndPadding = window.ActualWidth - adaptiveGrid.ActualWidth;
        foreach (var contentWidth in new[] { 791d, 792, 800, 1187, 1188, 1200 }) {
            window.Width = contentWidth + frameAndPadding;
            Render(window, Path.Combine(output, $"responsive-content-{contentWidth:0}.png"));
            Assert(ReferenceEquals(adaptiveGrid, All<AdaptiveCardGrid>(window).Single()), "Changing column count reflows the existing cards without rebuilding the page.");
            AssertResponsiveLayout(window.Width);
        }
        client.State = client.State with { Settings = client.State.Settings with { Language = "english" } }; client.RaiseChanged();
        var englishMaxWidth = window.MaxWidth;
        var checkedMinimumWrap = false; var checkedClampedSingleRow = false;
        try {
            foreach (var (contentWidth, clamped) in new[] { (792d, false), (1188d, false), (1188d, true) }) {
                window.MaxWidth = clamped ? 1044 : englishMaxWidth;
                window.Width = contentWidth + frameAndPadding;
                Render(window, Path.Combine(output, $"responsive-english-unknown-{contentWidth:0}{(clamped ? "-clamped" : "")}.png"));
                AssertResponsiveLayout(window.Width);
                var unknownCard = All<AdaptiveCardGrid>(window).Single().Children.OfType<Border>().Single(card => All<TextBlock>(card).Any(text => text.Text == "Reset Unknown"));
                var reset = All<Button>(unknownCard).Single(button => Name(button) == "Reset quota");
                var refresh = All<Button>(unknownCard).Single(button => Name(button) == "Refresh this account");
                var leading = (FrameworkElement)reset.Parent; var trailing = (FrameworkElement)refresh.Parent;
                var actions = (FrameworkElement)leading.Parent;
                var requiredWidth = leading.DesiredSize.Width + 10 + trailing.DesiredSize.Width;
                var shouldWrap = requiredWidth > actions.ActualWidth;
                var resetY = reset.TranslatePoint(new Point(), unknownCard).Y;
                var refreshY = refresh.TranslatePoint(new Point(), unknownCard).Y;
                Console.WriteLine($"English actions: card={unknownCard.ActualWidth:0.##}, available={actions.ActualWidth:0.##}, required={requiredWidth:0.##}, wrap={shouldWrap}, clamped={clamped}.");
                Assert(shouldWrap ? refreshY >= resetY + reset.ActualHeight : Math.Abs(refreshY - resetY) <= 1,
                    "Unknown English reset text remains complete; utilities wrap only when the actual action width needs another row.");
                checkedMinimumWrap |= unknownCard.ActualWidth <= 381 && shouldWrap;
                checkedClampedSingleRow |= clamped && window.ActualWidth <= 1045 && unknownCard.ActualWidth > 400 && !shouldWrap;
            }
        } finally { window.MaxWidth = englishMaxWidth; }
        Assert(checkedMinimumWrap && checkedClampedSingleRow, "English actions are exercised both at the minimum card width and in a wider two-column card after the window is clamped.");
        client.State = client.State with { Settings = client.State.Settings with { Language = saved.Settings.Language } }; client.RaiseChanged();
        var previousMaxWidth = window.MaxWidth;
        try {
            window.MaxWidth = 1044; window.Width = 1700;
            Render(window, Path.Combine(output, "responsive-clamped-width.png"));
            Assert(window.ActualWidth <= 1045, "The constrained-window fixture must actually apply its width limit.");
            AssertResponsiveLayout(1700);
        } finally { window.MaxWidth = previousMaxWidth; }
        window.Width = 480; Render(window, Path.Combine(output, "responsive-direct-resize.png"));
        AssertResponsiveLayout(480);
        Assert(window.AccountColumnCount == 1, "Resizing alone reflows the account list.");
        window.Width = 1100; window.Navigate("accounts"); Render(window, Path.Combine(output, "reset-ready.png"));
        var before = client.Commands.Count;
        All<Button>(window).First(button => Name(button) == "重置额度" && button.IsEnabled).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Render(window, Path.Combine(output, "reset-confirm.png"));
        Assert(window.CurrentPage == "resetQuota" && client.Commands.Count == before, "Opening reset does not consume a credit.");
        Assert(All<TextBlock>(window).Any(text => text.Text.Contains("重置卡最早到期")), "Confirmation distinguishes reset credit expiry from quota recovery.");
        All<Button>(window).Single(button => Name(button) == "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Render(window, Path.Combine(output, "reset-cancelled.png"));
        Assert(client.Commands.Count == before, "Cancelling reset is read-only.");
        All<Button>(window).First(button => Name(button) == "重置额度" && button.IsEnabled).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Render(window, Path.Combine(output, "reset-confirm.png"));
        var confirm = All<Button>(window).Single(button => Name(button) == "确认重置 · 消耗 1 张");
        Assert(!confirm.IsDefault, "Enter cannot consume a reset credit by default.");
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(client.Commands.Count == before + 1 && client.Commands.Last() == "resetQuota:" && client.LastAccountId == rows[1].Profile.Id,
            "Only explicit confirmation resets the selected account once.");
        client.State = saved; window.Navigate("accounts");
    }
    private static void AccentThemes(App app, MainWindow window, FixtureClient client, NativeSettings native, string settingsKey, string output)
    {
        var before = client.Commands.Count;
        var accents = new[] { (Key: "blue", Label: "海洋蓝"), (Key: "purple", Label: "暮光紫"), (Key: "teal", Label: "青碧绿"), (Key: "orange", Label: "落日橙") };
        window.Width = 1100; window.Height = 1000;
        var importDialog = new JsonImportWindow(client) { Owner = window };
        importDialog.Show(); importDialog.UpdateLayout();
        var importEditor = All<TextBox>(importDialog).Single(); importEditor.Text = "[{}]";
        var importSubmit = All<Button>(importDialog).Single(button => Name(button) == "导入粘贴的 JSON");
        foreach (var dark in new[] { false, true }) {
            native.ThemePreference = dark ? "dark" : "light";
            foreach (var (key, label) in accents) {
                window.Navigate("settings"); window.UpdateLayout();
                All<Button>(window).Single(button => Name(button) == "主题色：" + label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render(window, Path.Combine(output, $"accent-{key}-{(dark ? "dark" : "light")}-settings.png"));
                Assert(native.AccentPreference == key && new NativeSettings(settingsKey).AccentPreference == key && app.AccentPreference == key,
                    "Choosing a color through the real settings control immediately updates and persists the palette.");
                var selected = All<Button>(window).Where(button => Name(button).StartsWith("主题色：") && System.Windows.Automation.AutomationProperties.GetHelpText(button) == "已选中").ToArray();
                Assert(selected.Length == 1 && Name(selected[0]) == "主题色：" + label && selected[0].BorderBrush == app.Resources["AccentBorder"] && selected[0].Background == app.Resources["Selected"],
                    "The selected accent has one explicit checkmark state and the current gradient border.");
                Assert(All<ComboBox>(window).Single(choice => Name(choice) == "主题").SelectedIndex == (dark ? 0 : 1), "Accent selection preserves the brightness control.");
                Render(importDialog, Path.Combine(output, $"accent-{key}-{(dark ? "dark" : "light")}-import.png"));
                Assert(importDialog.IsVisible && importEditor.Text == "[{}]" && importSubmit.Background == app.Resources["Accent"] && importSubmit.Foreground == app.Resources["OnAccent"] && importDialog.Background == app.Resources["Surface"],
                    "An already open import dialog receives brightness/accent resource updates while retaining its unsubmitted text.");
                window.Navigate("accounts"); Render(window, Path.Combine(output, $"accent-{key}-{(dark ? "dark" : "light")}-accounts.png"));
                var activeCard = All<UniformGrid>(window).Single(grid => grid.Children.Count == client.State.Accounts.Length && grid.Children.OfType<Border>().Count() == client.State.Accounts.Length)
                    .Children.OfType<Border>().Single(card => card.Background == app.Resources["Selected"]);
                Assert(activeCard.BorderBrush == app.Resources["AccentBorder"], "The actual active account border follows the gradient accent.");
                var primary = All<Button>(window).Single(button => Name(button) == client.State.Text("add_account"));
                Assert(primary.Background == app.Resources["Accent"] && primary.Foreground == app.Resources["OnAccent"], "Primary actions use the active gradient with a readable solid foreground.");
                var chrome = (Border)primary.Template.FindName("Chrome", primary);
                Assert(chrome.Opacity == 1, "Enabled primary button chrome never fades its text contrast.");
                var target = All<Button>(window).Single(button => Name(button) == "studio@example.test"); target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render(window, Path.Combine(output, $"accent-{key}-{(dark ? "dark" : "light")}-switch.png"));
                Assert(window.CurrentPage == "switch" && client.Commands.Count == before, "Account selection still requires confirmation under every accent.");
                All<Button>(window).Single(button => Name(button) == "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
        window.Width = 480; window.Navigate("settings"); Render(window, Path.Combine(output, "accent-settings-narrow.png"));
        var chips = All<Button>(window).Where(button => Name(button).StartsWith("主题色：")).ToArray();
        Assert(chips.Length == 4 && chips.All(button => button.ActualWidth > 0 && button.TranslatePoint(new Point(), window).X + button.ActualWidth < window.ActualWidth),
            "All four accent controls remain within the narrow window.");
        Assert(!All<ScrollViewer>(window).Any(view => view.ComputedHorizontalScrollBarVisibility == Visibility.Visible), "Accent controls wrap without horizontal scrolling.");
        Assert(chips.Select(button => Math.Round(button.TranslatePoint(new Point(), window).Y)).Distinct().Count() >= 2, "The narrow fixture actually wraps accent choices into multiple rows.");
        Assert(client.Commands.Count == before, "Appearance changes never send account commands.");
        importDialog.Close();
        native.AccentPreference = "blue"; native.ThemePreference = "light"; window.Width = 1100; window.Navigate("accounts");
        Console.WriteLine("PASS: all four accents rendered in light/dark settings, account cards and switch confirmation; narrow wrapping and persistent selection verified.");
    }
    private static void AddAccountFlow(App app, MainWindow window, FixtureClient client, NativeSettings native, string output)
    {
        var before = client.Commands.Count;
        var saved = client.State;
        Exception? failure = null;
        void Schedule(Action action) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            try { action(); } catch (Exception ex) {
                failure = ex;
                foreach (var dialog in app.Windows.OfType<Window>().Where(dialog => dialog is AddAccountWindow or JsonImportWindow).ToArray()) dialog.Close();
            }
        }));
        void Open(Action<AddAccountWindow> choose) {
            window.Navigate("accounts"); window.UpdateLayout();
            Schedule(() => {
                var dialog = app.Windows.OfType<AddAccountWindow>().Single(); dialog.UpdateLayout();
                Assert(client.Commands.Count == before, "Opening the account method chooser cannot begin authorization or import.");
                Assert(All<Button>(dialog).Count(button => Name(button) is "浏览器授权" or "JSON 导入") == 2, "The add dialog presents both supported methods.");
                choose(dialog);
            });
            All<Button>(window).Single(button => Name(button) == "添加账号").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (failure != null) throw failure;
        }
        foreach (var dark in new[] { false, true }) {
            native.ThemePreference = dark ? "dark" : "light";
            Open(dialog => {
                Render(dialog, Path.Combine(output, $"add-methods-{(dark ? "dark" : "light")}.png"));
                Assert(dialog.Title == "添加账号 · " + App.DisplayName && dialog.Icon != null, "Method chooser keeps product branding.");
                All<Button>(dialog).Single(button => Name(button) == "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert(client.Commands.Count == before, "Cancelling method selection has no account side effect.");
        }
        Open(dialog => dialog.Close());
        Assert(client.Commands.Count == before, "Closing the method dialog through its window close action is inert.");
        Open(dialog => {
            Schedule(() => {
                var import = app.Windows.OfType<JsonImportWindow>().Single();
                Render(import, Path.Combine(output, "add-methods-json-import.png"));
                Assert(All<TextBox>(import).Single().AcceptsReturn && All<Button>(import).Any(button => Name(button) == "选择 JSON 文件导入"), "Choosing JSON opens both text and file import options.");
                Assert(client.Commands.Count == before, "Choosing JSON cannot start browser authorization.");
                import.Close();
            });
            All<Button>(dialog).Single(button => Name(button) == "JSON 导入").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert(client.Commands.Count == before, "Closing JSON without submission is inert.");
        Open(dialog => All<Button>(dialog).Single(button => Name(button) == "浏览器授权").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Assert(client.Commands.Count == before + 1 && client.Commands.Last() == "add:", "Only selecting browser authorization issues one add command.");
        client.State = saved with { IsAddingAccount = true }; client.RaiseChanged(); window.UpdateLayout();
        All<Button>(window).Single(button => Name(button) == "取消添加").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(client.Commands.Count == before + 2 && client.Commands.Last() == "cancelAdd:" && !app.Windows.OfType<AddAccountWindow>().Any(), "Pending sign-in cancellation sends cancelAdd directly without reopening the chooser.");
        client.State = saved; client.RaiseChanged(); native.ThemePreference = "light";
        Console.WriteLine("PASS: account-method chooser, inert cancel/close, JSON text/file route, explicit browser authorization and pending cancellation (fixture only).");
    }
    private static void SettingsScroll(MainWindow window, FixtureClient client, NativeSettings native, string output)
    {
        var saved = client.State; window.Width = 860; window.Height = 520; window.Navigate("settings");
        Render(window, Path.Combine(output, "settings-scroll-initial.png"));
        var scroll = window.PageScrollViewer;
        scroll.ScrollToVerticalOffset(Math.Min(450, scroll.ScrollableHeight)); window.UpdateLayout();
        var position = scroll.VerticalOffset;
        Assert(position > 100, "The settings regression fixture must start below the top.");
        var originalBody = scroll.Content;
        client.State = saved with { Accounts = saved.Accounts.Select(row => row with { UsageStatus = "refreshing" }).ToArray() };
        client.RaiseChanged(); window.UpdateLayout();
        Assert(ReferenceEquals(scroll.Content, originalBody) && Math.Abs(scroll.VerticalOffset - position) < 1,
            "Background account refreshes do not rebuild or scroll the settings page.");
        All<Button>(window).Single(button => Name(button) == "主题色：暮光紫").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Render(window, Path.Combine(output, "settings-scroll-after-accent.png"));
        Assert(ReferenceEquals(window.PageScrollViewer, scroll) && Math.Abs(scroll.VerticalOffset - position) < 1, "Changing the accent retains the existing scroll viewer and vertical position.");
        // Simulate a host preference acknowledgement at the current scroll position.
        client.State = saved with { Settings = saved.Settings with { AutoResetEnabled = !saved.Settings.AutoResetEnabled } }; client.RaiseChanged();
        Render(window, Path.Combine(output, "settings-scroll-after-toggle.png"));
        Assert(Math.Abs(scroll.VerticalOffset - position) < 1, "An acknowledged setting change does not jump to the top.");
        client.State = client.State with { IsMutating = true }; client.RaiseChanged();
        client.State = client.State with { IsMutating = false }; client.RaiseChanged();
        Render(window, Path.Combine(output, "settings-scroll-after-burst.png"));
        Assert(Math.Abs(scroll.VerticalOffset - position) < 1, "Consecutive state notifications preserve the pending scroll restoration.");
        native.ThemePreference = "dark";
        Render(window, Path.Combine(output, "settings-scroll-after-theme.png"));
        Assert(Math.Abs(scroll.VerticalOffset - position) < 1, "Changing brightness preserves the current position.");
        window.Navigate("accounts"); Render(window, Path.Combine(output, "settings-scroll-navigation.png"));
        Assert(scroll.VerticalOffset == 0, "Navigating to a different page starts at the top.");
        client.State = saved; client.RaiseChanged(); native.AccentPreference = "blue"; native.ThemePreference = "light"; window.Height = 760;
        Console.WriteLine("PASS: stable settings scroll container, no redundant account-refresh render, same-page scroll preservation and deliberate navigation reset.");
    }
    private static string Name(DependencyObject value) => System.Windows.Automation.AutomationProperties.GetName(value);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match;
            foreach (var descendant in All<T>(child)) yield return descendant;
        }
    }
    private static void Render(Window window, string path) {
        window.Show(); window.UpdateLayout();
        var frame = new DispatcherFrame(); window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class FixtureClient : IAccountClient, IAccountImporter {
        public bool FailImport { get; set; }
        public Task<ImportResult> ImportTextAsync(string json) => FailImport ? Task.FromException<ImportResult>(new FormatException("模拟格式错误")) : Task.FromResult(new ImportResult(1, 0, 0));
        public Task<ImportResult> ImportFilesAsync(string[] paths) => Task.FromResult(new ImportResult(1, 0, 0));
        public event Action? Changed;
        public void RaiseChanged() => Changed?.Invoke();
        public List<string> Commands { get; } = [];
        public Guid? LastAccountId { get; private set; }
        public AccountSnapshot State { get; set; }
        public FixtureClient() {
            var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var time = new DateTimeOffset(2026, 9, 15, 9, 25, 0, TimeSpan.FromHours(8));
            State = new([
                new(new(id, "Personal", "personal@example.test"), "P", new(83, time, 91, time.AddHours(-2), 2), null,
                    "loaded", new(new DateTimeOffset(2099, 10, 17, 12, 0, 0, TimeSpan.Zero), time, "accounts_check", true), SubscriptionStatus: "verified"),
                new(new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Studio", "studio@example.test"), "S", new(56, time.AddHours(1), 68, time.AddHours(-1)), null)
            ], id, new("simplifiedChinese"), false, false, true, null, new() {
                ["usage"] = "用量", ["resets"] = "重置于", ["reset_credits"] = "重置次数", ["unknown"] = "未知", ["left"] = "% 剩余", ["manage"] = "管理账号", ["settings"] = "设置", ["quit"] = "退出应用",
                ["subscription_until"] = "有效期：%@", ["subscription_snapshot"] = "登录快照", ["subscription_old_snapshot"] = "旧登录快照",
                ["subscription_unknown"] = "有效期：未知", ["subscription_detail"] = "最近核验于 %@。",
                ["accounts"] = "账号", ["back"] = "返回", ["active"] = "当前", ["remove"] = "移除", ["add_account"] = "添加账号", ["cancel_add_account"] = "取消添加",
                ["sign_in_hint"] = "将打开浏览器进行 Codex 登录。", ["register_current_account"] = "登记当前登录账号",
                ["settings_general"] = "通用", ["settings_updates"] = "软件更新", ["launch_at_login"] = "登录时自动启动",
                ["settings_automation"] = "自动化", ["auto_reset"] = "到期前自动使用 reset",
                ["auto_reset_hint"] = "到期前 10 分钟自动使用所有已保存账号的 reset；应用须运行且电脑保持唤醒。", ["open_reset_log"] = "打开日志文件夹",
                ["show_menu_bar_percentage"] = "在菜单栏显示百分比", ["show_five_hour_usage"] = "显示 5 小时用量", ["language"] = "语言",
                ["system_default"] = "跟随系统", ["english"] = "English", ["simplified_chinese"] = "简体中文", ["five_hour"] = "5 小时", ["weekly"] = "7 天",
                ["automatically_check_updates"] = "自动检查更新", ["update_check_hint"] = "每小时检查一次，有新版本时显示蓝点。",
                ["current_version"] = "当前版本 %@", ["check_for_updates"] = "检查更新", ["cancel"] = "取消", ["switch"] = "切换账号",
                ["switch_title"] = "切换到 %@？", ["switch_body_windows"] = "将强制结束 Codex Desktop 及其子进程，然后重新打开 Desktop。正在运行的任务会被中断，未保存的状态可能丢失。请先完成或停止 Desktop 任务再切换。独立启动的 CLI 会话保持运行，新 CLI 会话将使用所选账号。"
            });
        }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            Commands.Add(command + ":" + value); LastAccountId = accountID; return Task.CompletedTask;
        }
    }
}
