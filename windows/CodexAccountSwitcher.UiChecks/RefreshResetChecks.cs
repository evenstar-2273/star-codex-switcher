using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;

internal static class RefreshResetChecks
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
    private static int assertions;
    public static void Run(App app, string output, NativeSettings restoreSettings)
    {
        Directory.CreateDirectory(output);
        var key = @"Software\StarCodexSwitcher.UiChecks\RefreshReset-" + Guid.NewGuid().ToString("N");
        var native = new NativeSettings(key);
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var client = new Client();
        var displayNow = Now;
        var window = new MainWindow(client, native, () => displayNow) { Width = 1280, Height = 790 };
        try {
            CountdownRules();
            window.Show(); window.Activate(); Pump(60);
            ColorPresentation(app, window, native, output);
            CountdownPresentation(window, () => displayNow = displayNow.AddMinutes(1), output);
            RefreshInteractions(window, client, output);
            ResetInteractions(window, client, native, output);
            SilentAndAccountActions(app, window, client, native, output);
            LocalAccountDialog(window, output);
            File.WriteAllText(Path.Combine(output, "refresh-reset-result.txt"), $"PASS: {assertions} refresh/reset presentation assertions; simulated requests only.");
            Console.WriteLine($"PASS: {assertions} refresh/reset assertions; actual WPF rotation, refresh scope, pending deduplication, success/error cleanup, scroll preservation, three expiry colors, live countdowns and confirmation consistency.");
        } finally {
            client.Release(); client.ReleaseReset(); Pump(50); window.CloseForExit(); app.UseThemeSettings(restoreSettings);
            SynchronizationContext.SetSynchronizationContext(prior); Registry.CurrentUser.DeleteSubKeyTree(key, false);
        }
    }
    private static void CountdownRules()
    {
        Check(AccountVisuals.ResetExpiryLabel(Now.AddHours(3).AddMinutes(25), Now, true, compact: true).EndsWith("（剩余 3 小时 25 分钟）"), "A future reset credit displays hours and minutes in parentheses.");
        Check(AccountVisuals.ResetExpiryLabel(Now.AddDays(5).AddHours(21), Now, true).EndsWith("（剩余 5 天 21 小时）"), "Multi-day reset credits display days and hours.");
        Check(AccountVisuals.ResetExpiryLabel(Now.AddSeconds(59), Now, true).EndsWith("（剩余不足 1 分钟）"), "A positive sub-minute reset credit remains available until its expiry is reached.");
        Check(AccountVisuals.ResetExpiryLabel(Now.AddMinutes(1), Now, true).EndsWith("（剩余 1 分钟）"), "The reset-credit minute boundary is explicit.");
        Check(AccountVisuals.ResetExpiryLabel(Now, Now, true).EndsWith("（已到期，待刷新）") && !AccountVisuals.ResetExpiryLabel(Now, Now, true).Contains("恢复"), "A reached reset-credit expiry does not claim that quota renewed.");
        Check(AccountVisuals.ResetExpiryLabel(Now.AddHours(-1), Now, false).EndsWith(" (Expiry reached; refresh to confirm)"), "Expired reset credits retain the accurate English expiry state.");
        Check(AccountVisuals.ResetExpiryLabel(null, Now, true) == "重置卡：暂无到期信息" && AccountVisuals.ResetExpiryLabel(null, Now, false) == "Reset credits: no expiry information", "Unknown reset expiry does not fabricate a countdown.");
        Check(AccountVisuals.ResetExpiryLabel(Now.AddHours(3).AddMinutes(25), Now, false).EndsWith(" (3h 25m left)"), "English reset expiry uses the same countdown units as quota renewal.");
    }
    private static void ColorPresentation(App app, MainWindow window, NativeSettings native, string output)
    {
        foreach (var dark in new[] { false, true }) {
            native.ThemePreference = dark ? "dark" : "light"; window.Navigate("accounts"); Pump(50);
            var cards = Cards(window);
            foreach (var (index, tone) in new[] { (0, "Success"), (1, "Warning"), (2, "Danger"), (3, "Danger"), (4, "Muted") }) {
                var line = ResetLine(cards[index]);
                var words = All<TextBlock>(line).Where(text => text.Text.Length > 1).ToArray();
                Check(words.Length > 0 && All<TextBlock>(line).All(text => Equals(text.Foreground, app.Resources[tone])), "Reset expiry icon and text share their semantic color in both palettes.");
                if (index is 1 or 2) Check(words.Any(text => text.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight()), "Urgent reset expiry receives emphasis as well as its distinct color.");
                var foreground = ((SolidColorBrush)words[0].Foreground).Color;
                var surface = ((Border)cards[index]).Background;
                var backgroundColors = surface is GradientBrush gradient ? gradient.GradientStops.Select(stop => stop.Color) : [((SolidColorBrush)surface).Color];
                Check(backgroundColors.All(background => Contrast(foreground, background) >= 4.5), "Rendered reset expiry remains legible against its actual selected or unselected card surface.");
                if (index == 3) Check(words.Any(text => text.Text.Contains("已到期")), "Expired reset credits are explicitly labelled, rather than looking like future availability.");
                if (index == 4) Check(words.Any(text => text.Text.Contains("暂无到期信息")), "Missing expiry remains unknown rather than inventing an expiration date.");
                if (index < 3) Check(words.Any(text => text.Text.Contains("（剩余 ") && text.Text.EndsWith("）")), "Every future reset credit has its remaining time on the card in both palettes.");
            }
            Capture(window, Path.Combine(output, $"reset-colors-{(dark ? "dark" : "light")}.png"));
            Click(Button(cards[2], "重置额度")); Pump(50);
            Check(window.CurrentPage == "resetQuota", "The reset action still opens its confirmation page.");
            Check(All<TextBlock>(ResetLine(window)).All(text => Equals(text.Foreground, app.Resources["Danger"])), "The urgent expiry color carries through to reset confirmation.");
            Check(ResetLabel(window).Text.EndsWith("（剩余 8 小时 0 分钟）"), "Reset confirmation includes the same exact countdown as its account card.");
            Check(All<Button>(window).Any(button => Name(button) == "确认重置 · 消耗 1 张"), "Expiry styling does not bypass the explicit one-credit confirmation.");
            Capture(window, Path.Combine(output, $"reset-confirmation-{(dark ? "dark" : "light")}.png"));
            Click(Button(window, "取消")); Pump(40);
        }
    }
    private static TextBlock ResetLabel(DependencyObject owner) => All<TextBlock>(ResetLine(owner)).Single(text => text.Text.Length > 1);
    private static void CountdownPresentation(MainWindow window, Action advanceMinute, string output)
    {
        window.Width = 844; window.Height = 790; window.Navigate("accounts"); Pump(50);
        var label = ResetLabel(Cards(window)[2]);
        Check(CountdownFits(label, ResetLine(Cards(window)[2])), "A narrow card allows natural wrapping and keeps the complete countdown inside its allocated bounds.");
        var card = Cards(window)[2]; var button = Button(card, "重置额度");
        Check(label.TranslatePoint(new Point(), card).Y + label.ActualHeight <= button.TranslatePoint(new Point(), card).Y, "The wrapped countdown remains above the card actions without overlap.");
        Capture(window, Path.Combine(output, "reset-countdown-narrow.png"));
        // Exercise the real presentation timer with a short test interval and an
        // injected clock instead of waiting a wall-clock minute or hitting APIs.
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("presentationTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var interval = timer.Interval;
        try {
            Check(interval == TimeSpan.FromMinutes(1), "The production countdown cadence remains once per minute.");
            timer.Interval = TimeSpan.FromMilliseconds(80);
            advanceMinute();
            PumpUntil(() => ResetLabel(Cards(window)[2]).Text.EndsWith("（剩余 7 小时 59 分钟）"));
            Check(true, "The actual presentation timer advances the card countdown without a network refresh.");
            Click(Button(Cards(window)[2], "重置额度")); Pump(40);
            advanceMinute();
            PumpUntil(() => ResetLabel(window).Text.EndsWith("（剩余 7 小时 58 分钟）"));
            Check(true, "The same timer advances the open reset confirmation countdown.");
        } finally { timer.Interval = interval; }
        window.Width = 480; Pump(50);
        var confirmation = ResetLabel(window);
        Check(CountdownFits(confirmation, ResetLine(window)) &&
            confirmation.TranslatePoint(new Point(), window).Y + confirmation.ActualHeight <= Button(window, "确认重置 · 消耗 1 张").TranslatePoint(new Point(), window).Y,
            "The minimum-width confirmation keeps the complete countdown within its bounds and above the action buttons, wrapping when needed.");
        Capture(window, Path.Combine(output, "reset-confirmation-countdown-narrow.png"));
        Click(Button(window, "取消")); Pump(40);
    }
    private static bool CountdownFits(TextBlock label, FrameworkElement line)
    {
        var position = label.TranslatePoint(new Point(), line);
        return label.TextWrapping == TextWrapping.Wrap && label.TextTrimming == TextTrimming.None &&
            position.X >= 0 && position.X + label.ActualWidth <= line.ActualWidth + .5 &&
            position.Y >= 0 && position.Y + label.ActualHeight <= line.ActualHeight + .5 &&
            label.DesiredSize.Height <= label.ActualHeight + .5;
    }
    private static void RefreshInteractions(MainWindow window, Client client, string output)
    {
        window.Width = 900; window.Height = 600; window.Navigate("accounts"); Pump(50);
        var scroll = window.PageScrollViewer; scroll.ScrollToVerticalOffset(230); Pump(40); var listOffset = scroll.VerticalOffset;
        Check(listOffset > 100, "The account list fixture has a real nonzero scroll offset.");
        var top = Button(window, "刷新"); var oldCards = Cards(window); var oldCardButton = Button(oldCards[0], "刷新此账号");
        Click(top); Pump(30);
        Check(client.Calls.Count == 1 && client.Calls[0] == null && !client.State.IsMutating, "Global refresh starts one request and provides UI feedback before any host state acknowledgement.");
        var rotation = ActiveRotation(Button(window, "刷新"));
        Check(Cards(window).All(card => ReferenceEquals(Icon(Button(card, "刷新此账号")).RenderTransform, rotation)), "A global request rotates the header and every card in the same continuous animation.");
        Check(All<Button>(window).Where(button => Name(button) is "刷新" or "刷新此账号").All(button => !button.IsEnabled), "All refresh entry points are disabled while the request is pending.");
        AssertTurns(rotation);
        Click(top); Click(oldCardButton); Click(Button(Cards(window)[1], "刷新此账号"));
        Check(client.Calls.Count == 1, "Even stale button callbacks cannot submit another refresh while the first is pending.");
        client.Notify(); Pump(50);
        Check(ReferenceEquals(ActiveRotation(Button(window, "刷新")), rotation), "Snapshot redraw preserves the active animation instead of resetting its phase.");
        AssertTurns(rotation);
        Check(ReferenceEquals(scroll, window.PageScrollViewer) && Math.Abs(scroll.VerticalOffset - listOffset) < 1, "Starting and redrawing a global refresh preserves the account list scroll position.");
        scroll.ScrollToTop(); Pump(40); Capture(window, Path.Combine(output, "refresh-global-pending.png"));
        scroll.ScrollToVerticalOffset(listOffset); Pump(40);
        client.Release(); PumpUntil(() => !window.IsBusy); Pump(30);
        Check(!rotation.HasAnimatedProperties && RefreshButtons(window).All(button => !Icon(button).RenderTransform.HasAnimatedProperties && button.IsEnabled), "Successful completion removes the animation clocks and restores the refresh controls.");
        Check(Math.Abs(scroll.VerticalOffset - listOffset) < 1, "Successful completion retains the account list position.");

        var target = client.State.Accounts[1].Profile.Id;
        Click(Button(Cards(window)[1], "刷新此账号")); Pump(30);
        Check(client.Calls.Count == 2 && client.Calls[1] == target, "A single-card refresh targets only the selected account.");
        var single = ActiveRotation(Button(Cards(window)[1], "刷新此账号"));
        Check(!Icon(Button(window, "刷新")).RenderTransform.HasAnimatedProperties && Cards(window).Where((_, i) => i != 1).All(card => !Icon(Button(card, "刷新此账号")).RenderTransform.HasAnimatedProperties), "A single-account refresh leaves the header and unrelated account icons stationary.");
        AssertTurns(single);
        Click(Button(window, "刷新")); Click(Button(Cards(window)[2], "刷新此账号"));
        Check(client.Calls.Count == 2, "Global and other-card clicks cannot overlap a pending single-account refresh.");
        scroll.ScrollToTop(); Pump(30); Capture(window, Path.Combine(output, "refresh-single-pending.png"));
        scroll.ScrollToVerticalOffset(listOffset); Pump(30);
        client.Release(new IOException("模拟刷新失败，保留已有数据。")); PumpUntil(() => !window.IsBusy); Pump(40);
        Check(!single.HasAnimatedProperties && RefreshButtons(window).All(button => !Icon(button).RenderTransform.HasAnimatedProperties && button.IsEnabled), "A failed request also stops spinning and re-enables every refresh entry point.");
        Check(All<TextBlock>(window).Any(text => text.Text.Contains("模拟刷新失败")), "A failed refresh remains visible as an error rather than looking like a successful refresh.");
        Check(Math.Abs(scroll.VerticalOffset - listOffset) < 1, "An error render preserves list scroll position.");

        Click(Button(window, "刷新")); Pump(30); var settingsRotation = ActiveRotation(Button(window, "刷新"));
        Check(!All<TextBlock>(window).Any(text => text.Text.Contains("模拟刷新失败")), "Retry clears the previous local failure banner while the new refresh is pending.");
        window.Navigate("settings"); Pump(30); scroll.ScrollToEnd(); Pump(30); var settingsOffset = scroll.VerticalOffset; var settingsBody = scroll.Content;
        Check(settingsOffset > 100, "The settings fixture has a real scrollable position.");
        Check(All<TextBlock>(window).Any(text => text.IsVisible && text.Text.Contains("启动时检查一次") && text.Text.Contains("每 60 分钟") && text.Text.Contains("本次运行仅自动提醒一次")), "Update settings explain the startup, hourly and once-per-run automatic reminder policy.");
        client.Notify(); Pump(30);
        Check(ReferenceEquals(scroll.Content, settingsBody) && Math.Abs(scroll.VerticalOffset - settingsOffset) < 1, "An account notification during refresh does not rebuild or move settings content.");
        client.Release(); PumpUntil(() => !window.IsBusy); Pump(40);
        Check(!settingsRotation.HasAnimatedProperties && ReferenceEquals(scroll, window.PageScrollViewer) && Math.Abs(scroll.VerticalOffset - settingsOffset) < 1, "Refresh completion stops hidden animation and preserves the settings scroll offset.");
        window.Navigate("accounts"); Pump(30);
        Check(RefreshButtons(window).All(button => button.IsEnabled && !Icon(button).RenderTransform.HasAnimatedProperties), "Returning to the list after completion cannot revive a stale refresh animation.");
        Click(Button(Cards(window)[0], "刷新此账号")); Pump(20); client.Release(); PumpUntil(() => !window.IsBusy);
        Check(client.Calls.Count == 4, "Refresh can be retried normally after an earlier failure.");
    }
    private static FrameworkElement ResetLine(DependencyObject owner) => All<FrameworkElement>(owner).Single(element => Name(element) == "重置卡到期");
    private static void SilentAndAccountActions(App app, MainWindow window, Client client, NativeSettings native, string output)
    {
        client.Publish(client.State with { Error = null }); window.Navigate("accounts"); window.Height = 600; Pump(40);
        var before = window.PageScrollViewer.Content;
        client.Publish(client.State with { UsageRefreshRevision = client.State.UsageRefreshRevision + 1 }); Pump(30);
        Check(ReferenceEquals(before, window.PageScrollViewer.Content) && !window.IsBusy && RefreshButtons(window).All(button => !Icon(button).RenderTransform.HasAnimatedProperties && button.IsEnabled),
            "A timer revision with unchanged visible data neither rebuilds the list nor spins or disables controls.");
        var active = client.State.ActiveAccountID;
        var remove = Button(Cards(window)[0], "删除"); Check(remove.IsEnabled, "The current account exposes an enabled delete action.");
        Click(remove); Pump(30);
        Check(window.CurrentPage == "remove" && All<TextBlock>(window).Any(text => text.Text.Contains("不会退出 Codex 当前登录")) && client.RemovalCalls == 0,
            "Removing the current account requires confirmation that explains the local-only effect.");
        Capture(window, Path.Combine(output, "remove-current-confirm.png"));
        Click(Button(window, "取消")); Pump(20);
        Check(client.RemovalCalls == 0 && client.State.ActiveAccountID == active, "Cancelling current-account deletion leaves the account untouched.");
        Click(Button(Cards(window)[0], "删除")); Pump(20); Click(Button(window, "删除")); Pump(30);
        Check(client.RemovalCalls == 1 && client.State.Accounts.All(row => row.Profile.Id != active) && window.CurrentPage == "accounts",
            "Confirmed current-account deletion sends exactly one targeted remove command and updates the list.");

        var saved = client.State;
        foreach (var dark in new[] { false, true }) {
            native.ThemePreference = dark ? "dark" : "light";
            client.Publish(saved with { Accounts = saved.Accounts.Select((row, index) => row with {
                UsageError = index == 0 ? QuotaReadException.Unauthorized : index == 1 ? QuotaReadException.ProxyTimeout : null
            }).ToArray() }); window.Navigate("accounts"); window.Width = 1150; window.Height = 850; Pump(50);
            var cards = Cards(window); var expired = cards[0]; var network = cards[1];
            var title = All<TextBlock>(expired).Single(text => text.Text == "登录已失效");
            var alert = All<Border>(expired).Single(border => Name(border) == "登录失效提示");
            Check(title.FontSize >= 16 && title.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight() && Equals(title.Foreground, app.Resources["Danger"]) && Equals(alert.Background, app.Resources["DangerSurface"]),
                "401 uses a prominent red title and dedicated tinted alert in both themes.");
            Check(All<TextBlock>(expired).Any(text => text.Text.Contains("显示上次额度")) && !window.IsBusy && !Icon(Button(expired, "刷新此账号")).RenderTransform.HasAnimatedProperties,
                "A persistent authentication alert identifies cached quota without showing a timer spinner or blocking the page.");
            var switchExpired = Button(expired, client.State.Accounts[0].Profile.Email!);
            Check(!switchExpired.IsEnabled && Button(expired, "恢复登录").IsEnabled, "Expired sign-in disables switching and exposes the recovery action.");
            var calls = client.OtherCalls; Click(switchExpired); Pump(20);
            Check(window.CurrentPage == "accounts" && client.OtherCalls == calls, "Even a stale disabled switch callback cannot open a confirmation for a known expired login.");
            Check(!All<TextBlock>(network).Any(text => text.Text == "登录已失效") && Equals(All<Border>(network).Single(border => Name(border) == "刷新失败提示").Background, app.Resources["WarningSurface"]) && Button(network, client.State.Accounts[1].Profile.Email!).IsEnabled,
                "A VPN/proxy timeout uses an amber refresh warning and is not treated as expired authentication.");
            Capture(window, Path.Combine(output, $"account-auth-status-{(dark ? "dark" : "light")}.png"));
            Exception? dialogFailure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                var dialog = app.Windows.OfType<AddAccountWindow>().Single(); dialog.UpdateLayout();
                try {
                    Check(All<Button>(dialog).Any(button => Name(button) == "获取本地账号") && All<Button>(dialog).Any(button => Name(button) == "JSON 导入") && !All<Button>(dialog).Any(button => Name(button) == "浏览器授权"),
                        "Recovery exposes the supported local and JSON routes without a duplicate-account browser route.");
                    Check(All<TextBlock>(dialog).Any(text => text.Text.Contains("先在 Codex 中重新登录")), "Recovery explains how the current local login must be renewed first.");
                    Capture(dialog, Path.Combine(output, $"restore-login-{(dark ? "dark" : "light")}.png"));
                } catch (Exception ex) { dialogFailure = ex; } finally { dialog.Close(); }
            }));
            Click(Button(expired, "恢复登录")); if (dialogFailure != null) throw dialogFailure;
            Click(Button(Cards(window)[1], client.State.Accounts[1].Profile.Email!)); Pump(20);
            Check(window.CurrentPage == "switch", "A transient network error still permits explicit switch preflight.");
            var targetId = client.State.Accounts[1].Profile.Id;
            client.Publish(client.State with { Accounts = client.State.Accounts.Select(row => row.Profile.Id == targetId ? row with { UsageError = QuotaReadException.Unauthorized } : row).ToArray() }); Pump(20);
            var confirm = Button(window, "切换账号"); var count = client.OtherCalls; Click(confirm); Pump(20);
            Check(!confirm.IsEnabled && client.OtherCalls == count, "A 401 arriving while confirmation is open prevents the stale confirmation from switching.");
            client.Publish(saved); window.Navigate("accounts"); Pump(20);
            Check(Button(Cards(window)[0], client.State.Accounts[0].Profile.Email!).IsEnabled && !All<TextBlock>(window).Any(text => text.Text == "登录已失效"),
                "A successful refreshed state removes the expiration alert and restores switching.");
        }
    }
    private static void LocalAccountDialog(MainWindow owner, string output)
    {
        var calls = 0;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new AddAccountWindow(true, true, async () => { calls++; await gate.Task; }) { Owner = owner };
        dialog.Show(); Pump(30);
        var local = Button(dialog, "获取本地账号"); Click(local); Pump(20);
        Check(calls == 1 && All<Button>(dialog).All(button => !button.IsEnabled) && All<TextBlock>(dialog).Any(text => text.Text == "正在读取本地账号…"),
            "Getting the local account shows pending state and prevents duplicate operations.");
        Click(local); dialog.Close(); Pump(20);
        Check(calls == 1 && dialog.IsVisible, "Repeated local reads and closing are blocked while the request is pending.");
        gate.SetException(new IOException("The active Codex auth.json file is missing.")); PumpUntil(() => local.IsEnabled);
        Check(dialog.IsVisible && All<TextBlock>(dialog).Any(text => text.Text.Contains("请先完成登录")) && !All<TextBlock>(dialog).Any(text => text.Text.StartsWith("The active Codex")),
            "A missing local login stays open with a Chinese explanation and permits retry.");
        Capture(dialog, Path.Combine(output, "get-local-account-error.png"));
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(local); gate.SetResult(); PumpUntil(() => !dialog.IsVisible);
        Check(calls == 2 && dialog.SelectedMethod == AddAccountMethod.Local, "Successful local retrieval closes the chooser after completion.");
    }
    private static void ResetInteractions(MainWindow window, Client client, NativeSettings native, string output)
    {
        const string confirmName = "确认重置 · 消耗 1 张";
        var accountId = client.State.Accounts[2].Profile.Id;
        var activeId = client.State.ActiveAccountID;
        foreach (var dark in new[] { false, true }) {
            native.ThemePreference = dark ? "dark" : "light";
            client.PrepareReset(); window.Width = 850; window.Height = 790; window.Navigate("accounts"); Pump(40);
            var oldCard = Button(Cards(window)[2], "重置额度"); var oldRefresh = Button(window, "刷新");
            Click(oldCard); Pump(30); var confirm = Button(window, confirmName);
            var calls = client.ResetCalls.Count;
            Click(confirm);
            Check(window.IsBusy && window.CurrentPage == "resetQuota" && client.ResetCalls.Count == calls + 1 && client.ResetCalls.Last() == accountId && !client.State.IsMutating,
                "Confirming reset synchronously reserves the UI and stays on confirmation before a host acknowledgement.");
            Pump(30);
            var pending = Button(window, "重置中…"); var rotation = ActiveRotation(pending);
            Check(!pending.IsEnabled && !Button(window, "取消").IsEnabled && !Button(window, "返回").IsEnabled,
                "Reset confirmation, cancellation and navigation are disabled while the request is pending.");
            AssertTurns(rotation);
            Click(confirm); Click(confirm); Click(oldCard); Click(oldRefresh);
            Check(client.ResetCalls.Count == calls + 1 && client.Calls.Count == 4,
                "Repeated or stale reset and refresh callbacks cannot submit another request during reset.");
            client.Notify(); Pump(40);
            Check(ReferenceEquals(ActiveRotation(Button(window, "重置中…")), rotation), "Host redraw retains the reset animation phase.");
            client.CompleteResetMutation(); Pump(40);
            Check(window.IsBusy && window.CurrentPage == "resetQuota" && client.State.Accounts[2].Usage?.ResetCreditsRemaining == 1 &&
                ReferenceEquals(ActiveRotation(Button(window, "重置中…")), rotation),
                "The spinner continues after mutation while the independent quota readback is still pending.");
            AssertTurns(rotation);
            Capture(window, Path.Combine(output, $"reset-pending-{(dark ? "dark" : "light")}.png"));
            if (dark) {
                window.Navigate("accounts"); Pump(40);
                Check(ReferenceEquals(ActiveRotation(Button(Cards(window)[2], "重置额度")), rotation) &&
                    Cards(window).Where((_, index) => index != 2).All(card => !Icon(Button(card, "重置额度")).RenderTransform.HasAnimatedProperties) &&
                    RefreshButtons(window).All(button => !Icon(button).RenderTransform.HasAnimatedProperties),
                    "If the account list becomes visible, only the target reset button spins; refresh and unrelated reset controls stay stationary.");
                Capture(window, Path.Combine(output, "reset-card-pending-dark.png"));
            }
            client.ReleaseReset(); PumpUntil(() => !window.IsBusy); Pump(40);
            Check(window.CurrentPage == "accounts" && !rotation.HasAnimatedProperties && client.State.ActiveAccountID == activeId &&
                Cards(window).All(card => !Icon(Button(card, "重置额度")).RenderTransform.HasAnimatedProperties),
                "Completed reset readback stops every reset animation and returns to the account list without switching login.");
            Check(All<TextBlock>(Cards(window)[2]).Any(text => text.Text == "重置 1 次"), "The list presents the refreshed credit count after completion.");
            Capture(window, Path.Combine(output, $"reset-complete-{(dark ? "dark" : "light")}.png"));
        }
        client.PrepareReset(); window.Height = 480; window.Navigate("accounts"); Pump(40);
        Click(Button(Cards(window)[2], "重置额度")); Pump(40);
        window.PageScrollViewer.ScrollToEnd(); Pump(30); var offset = window.PageScrollViewer.VerticalOffset;
        Check(offset > 50, "The reset confirmation failure fixture has a real scroll offset.");
        Click(Button(window, confirmName)); Pump(30); var failedRotation = ActiveRotation(Button(window, "重置中…"));
        client.Notify(); Pump(30);
        Check(Math.Abs(window.PageScrollViewer.VerticalOffset - offset) < 1, "Pending reset snapshot redraw preserves confirmation scroll position.");
        client.ReleaseReset(new IOException("模拟重置连接失败，请先核对结果。")); PumpUntil(() => !window.IsBusy); Pump(40);
        Check(window.CurrentPage == "resetQuota" && !failedRotation.HasAnimatedProperties &&
            All<TextBlock>(window).Any(text => text.Text.Contains("模拟重置连接失败")),
            "A thrown reset error stops the animation and leaves the exact failure visible on confirmation.");
        Check(Math.Abs(window.PageScrollViewer.VerticalOffset - offset) < 1, "The reset failure render preserves confirmation scroll position.");
        var callsAfterFailure = client.ResetCalls.Count; Pump(150);
        Check(client.ResetCalls.Count == callsAfterFailure, "An error never automatically repeats a reset request.");
        window.PageScrollViewer.ScrollToTop(); Pump(30); Capture(window, Path.Combine(output, "reset-thrown-error-dark.png"));

        foreach (var dark in new[] { false, true }) {
            native.ThemePreference = dark ? "dark" : "light";
            client.PrepareReset(); window.Height = 790; window.Navigate("accounts"); Pump(40);
            Click(Button(Cards(window)[2], "重置额度")); Pump(30); Click(Button(window, confirmName)); Pump(30);
            var stateErrorRotation = ActiveRotation(Button(window, "重置中…"));
            client.CompleteResetMutation();
            client.ReleaseReset(stateError: "重置请求已成功，但最新额度或次数尚未完整读取，请刷新后核对。");
            PumpUntil(() => !window.IsBusy); Pump(40);
            Check(window.CurrentPage == "resetQuota" && !stateErrorRotation.HasAnimatedProperties &&
                All<TextBlock>(window).Any(text => text.Text.Contains("尚未完整读取")) && !Button(window, confirmName).IsEnabled && Button(window, "返回").IsEnabled,
                "A normally completed task with a backend error remains visible, ends loading and preserves the unknown-credit guard.");
            var calls = client.ResetCalls.Count;
            Click(Button(window, confirmName)); Pump(80);
            Check(client.ResetCalls.Count == calls, "Incomplete readback cannot consume a second credit through confirmation.");
            Capture(window, Path.Combine(output, $"reset-readback-error-{(dark ? "dark" : "light")}.png"));
        }
    }
    private static FrameworkElement[] Cards(MainWindow window) => All<AdaptiveCardGrid>(window).Single().Children.Cast<FrameworkElement>().ToArray();
    private static IEnumerable<Button> RefreshButtons(MainWindow window) => All<Button>(window).Where(button => Name(button) is "刷新" or "刷新此账号");
    private static TextBlock Icon(Button button) => ((StackPanel)button.Content).Children.OfType<TextBlock>().First();
    private static RotateTransform ActiveRotation(Button button) {
        Check(Icon(button).RenderTransform is RotateTransform { HasAnimatedProperties: true }, "Pending refresh has an active WPF rotation clock.");
        return (RotateTransform)Icon(button).RenderTransform;
    }
    private static void AssertTurns(RotateTransform rotation) {
        var first = rotation.Angle; Pump(140); var second = rotation.Angle;
        Check(Math.Abs(second - first) > 2, "The rendered rotation angle advances while the simulated request remains pending.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
    private static double Contrast(Color foreground, Color background) {
        static double Luminance(Color color) {
            static double Channel(byte value) { var linear = value / 255d; return linear <= .04045 ? linear / 12.92 : Math.Pow((linear + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var a = Luminance(foreground); var b = Luminance(background); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static string Name(DependencyObject element) => AutomationProperties.GetName(element);
    private static Button Button(DependencyObject owner, string name) => All<Button>(owner).Single(button => Name(button) == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var item2 in All<T>(child)) yield return item2;
        }
    }
    private static void Pump(int milliseconds) { var clock = Stopwatch.StartNew(); PumpUntil(() => clock.ElapsedMilliseconds >= milliseconds); }
    private static void PumpUntil(Func<bool> done) {
        var clock = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var timedOut = false;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done()) frame.Continue = false; else if (clock.ElapsedMilliseconds > 5000) { timedOut = true; frame.Continue = false; } };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (timedOut) throw new TimeoutException("Synthetic refresh did not settle.");
    }
    private static void Capture(Window window, string path) {
        window.UpdateLayout(); var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 1.5), (int)Math.Ceiling(window.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class Client : IAccountClient, IAccountImporter
    {
        public event Action? Changed;
        public readonly List<Guid?> Calls = [];
        public int RemovalCalls, OtherCalls;
        private TaskCompletionSource? pending;
        private TaskCompletionSource? resetPending;
        public readonly List<Guid?> ResetCalls = [];
        public AccountSnapshot State { get; private set; }
        public Task<ImportResult> ImportTextAsync(string json) => Task.FromResult(new ImportResult(1, 0, 0));
        public Task<ImportResult> ImportFilesAsync(string[] paths) => Task.FromResult(new ImportResult(1, 0, 0));
        public Client() {
            var offsets = new TimeSpan?[] { TimeSpan.FromDays(5), TimeSpan.FromDays(2), TimeSpan.FromHours(8), TimeSpan.FromHours(-1), null, TimeSpan.FromDays(8) };
            var rows = offsets.Select((offset, index) => new AccountRow(new(Guid.NewGuid(), "Synthetic", $"reset-{index + 1}@example.test"), "R",
                new(74, Now.AddDays(5), 88, Now.AddHours(4), 2, offset is { } span ? Now + span : null, "plus"), null,
                Subscription: new(Now.AddDays(25), Now, "fixture", true), PlanType: "plus", SubscriptionStatus: "verified")).ToArray();
            State = AccountSnapshot.Empty with { Accounts = rows, ActiveAccountID = rows[0].Profile.Id, Settings = new("simplifiedChinese") };
        }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            if (command == "remove") {
                RemovalCalls++;
                Publish(State with { Accounts = State.Accounts.Where(row => row.Profile.Id != accountID).ToArray(), ActiveAccountID = State.ActiveAccountID == accountID ? null : State.ActiveAccountID });
                return Task.CompletedTask;
            }
            if (command == "resetQuota") {
                ResetCalls.Add(accountID);
                if (resetPending != null) throw new Exception("The UI submitted an overlapping synthetic reset.");
                State = State with { Error = null };
                resetPending = new(TaskCreationOptions.RunContinuationsAsynchronously); return resetPending.Task;
            }
            if (command != "refresh") { OtherCalls++; return Task.CompletedTask; }
            Calls.Add(accountID);
            if (pending != null) throw new Exception("The UI submitted an overlapping synthetic refresh.");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously); return pending.Task;
        }
        public void Notify() => Changed?.Invoke();
        public void Publish(AccountSnapshot state) { State = state; Notify(); }
        public void Release(Exception? error = null) { var completion = pending; pending = null; if (error == null) completion?.TrySetResult(); else completion?.TrySetException(error); }
        public void PrepareReset() {
            State = State with { Error = null, Accounts = State.Accounts.Select((row, index) => index == 2 ? row with { Usage = row.Usage! with { ResetCreditsRemaining = 2 } } : row).ToArray() };
            Notify();
        }
        public void CompleteResetMutation() {
            State = State with { Accounts = State.Accounts.Select((row, index) => index == 2 ? row with { Usage = row.Usage! with { ResetCreditsRemaining = 1, RemainingPercent = 100 } } : row).ToArray() };
            Notify();
        }
        public void ReleaseReset(Exception? error = null, string? stateError = null) {
            if (stateError != null) {
                State = State with { Error = stateError, Accounts = State.Accounts.Select((row, index) => index == 2 ? row with { Usage = row.Usage! with { ResetCreditsRemaining = null } } : row).ToArray() };
                Notify();
            }
            var completion = resetPending; resetPending = null;
            if (error == null) completion?.TrySetResult(); else completion?.TrySetException(error);
        }
    }
}
