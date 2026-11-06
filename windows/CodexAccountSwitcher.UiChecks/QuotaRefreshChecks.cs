using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;

internal static class QuotaRefreshChecks
{
    private static int assertions;
    public static void Run(App app, string output, NativeSettings restore)
    {
        Directory.CreateDirectory(output);
        var key = @"Software\StarCodexSwitcher.UiChecks\QuotaRefresh-" + Guid.NewGuid().ToString("N");
        var native = new NativeSettings(key) { ThemePreference = "light" };
        var client = new Client();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var window = new MainWindow(client, native) { Width = 820, Height = 650 };
        try {
            window.Show(); window.Activate(); window.Navigate("settings"); Pump();
            var selector = All<QuotaRefreshSelector>(window).Single();
            var choice = All<ComboBox>(selector).Single();
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == 5 && choice.SelectedItem?.ToString() == "5 分钟", "Existing/default settings retain the five-minute schedule.");
            Check(choice.Items.Cast<string>().SequenceEqual(new[] { "禁用", "2 分钟", "5 分钟", "10 分钟", "15 分钟", "自定义…" }), "The selector exposes the exact supported presets plus custom input.");
            Check(All<TextBlock>(window).Any(text => text.Text.Contains("禁用仅停止定时刷新") && text.Text.Contains("驻留托盘") && text.Text.Contains("唤醒")), "Settings explain disable, tray and awake behavior.");
            var scroll = window.PageScrollViewer; scroll.ScrollToVerticalOffset(500); Pump(); var offset = scroll.VerticalOffset;
            choice.SelectedIndex = 0; Pump();
            Check(client.Values.SequenceEqual(new[] { 0 }) && client.State.Settings.QuotaRefreshIntervalMinutes == 0, "Disabled persists the explicit zero schedule.");
            Check(ReferenceEquals(selector, All<QuotaRefreshSelector>(window).Single()) && Math.Abs(scroll.VerticalOffset - offset) < 1, "Saving preserves the selector instance and settings scroll.");
            choice.SelectedIndex = 2; Pump();
            Check(client.Values.Last() == 5, "A preset restores scheduled refresh after disabling.");
            foreach (var dark in new[] { false, true }) {
                native.ThemePreference = dark ? "dark" : "light"; Pump(); choice.BringIntoView(); Pump();
                Capture(window, Path.Combine(output, "quota-preset-" + (dark ? "dark" : "light") + ".png"));
            }
            var count = client.Values.Count;
            choice.SelectedIndex = 5; Pump();
            var input = All<TextBox>(selector).Single();
            Check(choice.Visibility == Visibility.Collapsed && input.IsVisible && input.IsKeyboardFocusWithin, "Choosing custom replaces the dropdown with a focused minutes input.");
            Check(client.Values.Count == count, "Entering custom mode does not save an invented interval.");
            Check(All<TextBlock>(selector).Any(text => text.Text == "已生效") && All<TextBlock>(selector).Any(text => text.Text == "当前已生效：5 分钟"), "An unchanged custom interval is labelled active instead of showing an unexplained disabled Save button.");
            foreach (var invalid in new[] { "", " ", "0", "-1", "1.5", "1441", "abc" }) {
                input.Text = invalid; Enter(input); Pump();
                Check(client.Values.Count == count && All<TextBlock>(selector).Any(text => text.IsVisible && text.Text.Contains("1–1440")), "Invalid custom input is explained without changing the saved interval: " + invalid);
            }
            input.Text = "17"; input.Focus(); input.Select(1, 1); Pump();
            var draftOffset = scroll.VerticalOffset;
            client.Notify(); Pump();
            Check(input.Text == "17" && input.IsKeyboardFocusWithin && client.Values.Count == count, "Unrelated account snapshots preserve custom text and focus without saving.");
            native.ThemePreference = "dark"; Pump();
            Check(ReferenceEquals(input, All<TextBox>(All<QuotaRefreshSelector>(window).Single()).Single()) && input.Text == "17" && input.IsKeyboardFocusWithin && input.SelectionStart == 1 && input.SelectionLength == 1,
                "A theme rebuild preserves the active custom editor, caret and selection.");
            Check(client.Values.Count == count && Math.Abs(scroll.VerticalOffset - draftOffset) < 1, "Reparenting does not trigger a synthetic blur save or scroll jump.");
            Enter(input); Pump();
            Check(client.Values.Last() == 17 && client.State.Settings.QuotaRefreshIntervalMinutes == 17 && input.IsVisible, "Enter saves custom minutes and keeps the input presentation.");
            var saveButton = All<Button>(selector).Single(button => AutomationProperties.GetName(button) == "保存刷新间隔");
            input.Focus(); input.Text = "1"; Pump();
            Check(saveButton.IsEnabled && All<TextBlock>(selector).Any(text => text.Text.Contains("未保存") && text.Text.Contains("17")), "Typing one minute exposes an enabled Save button and the unchanged current value.");
            count = client.Values.Count; saveButton.Focus(); Pump();
            Check(client.Values.Count == count, "Moving focus to Save does not trigger a blur save that disables the button before its click.");
            saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == 1 && !saveButton.IsEnabled && All<TextBlock>(selector).Any(text => text.Text == "已保存 · 1 分钟" && Equals(text.Foreground, app.Resources["Success"])), "Clicking Save persists one minute and explicitly confirms success.");
            input.Text = "17"; Enter(input); Pump();
            input.Focus(); input.Text = "1"; var blurTarget = All<CheckBox>(window).Last(); var focusMoved = blurTarget.Focus(); Pump();
            Console.WriteLine($"Quota blur: moved={focusMoved}, targetEnabled={blurTarget.IsEnabled}, targetVisible={blurTarget.IsVisible}, targetFocusable={blurTarget.Focusable}, inputFocused={input.IsKeyboardFocusWithin}, saved={client.State.Settings.QuotaRefreshIntervalMinutes}, values={string.Join(',', client.Values)}.");
            Check(!input.IsKeyboardFocusWithin, "The blur-save test actually moves keyboard focus to another setting.");
            Check(client.Values.Last() == 1 && client.State.Settings.QuotaRefreshIntervalMinutes == 1, "Real focus loss saves the one-minute lower bound.");
            input.Focus(); input.Text = "1440"; Enter(input); Pump();
            Check(client.Values.Last() == 1440, "The custom upper bound of 1440 minutes is accepted.");
            var arrow = All<Button>(selector).Single(button => AutomationProperties.GetName(button) == "选择刷新间隔");
            arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(arrow.ContextMenu?.IsOpen == true, "The custom input retains an accessible route back to presets.");
            ((MenuItem)arrow.ContextMenu!.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); arrow.ContextMenu.IsOpen = false; Pump();
            Check(client.Values.Last() == 10 && choice.IsVisible && choice.SelectedItem?.ToString() == "10 分钟" && !input.IsVisible, "Choosing a preset replaces custom entry with the selected dropdown value.");

            choice.SelectedIndex = 5; Pump(); input.Text = "19"; client.Fail = true; Enter(input); Pump();
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == 10 && input.Text == "19" && All<TextBlock>(selector).Any(text => text.IsVisible && text.Text.Contains("保存失败")), "A failed save retains the draft and reported saved setting for retry.");
            client.Fail = false; Enter(input); Pump();
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == 19 && !All<TextBlock>(selector).Any(text => text.IsVisible && text.Text.Contains("保存失败")), "A failed custom save can be corrected or retried.");
            count = client.Values.Count; client.Hold = true; input.Text = "23"; Enter(input); Enter(input); Pump();
            Check(client.Values.Count == count + 1 && !selector.IsEnabled && client.State.Settings.QuotaRefreshIntervalMinutes == 19, "A pending save disables repeated submissions without claiming the setting was already persisted.");
            client.Release(); Pump();
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == 23 && selector.IsEnabled, "The editor recovers when the awaited setting save finishes.");
            client.IgnoreSave = true; input.Text = "29"; saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == 23 && All<TextBlock>(selector).Any(text => text.Text.Contains("保存失败") && text.Text.Contains("23")) && !All<TextBlock>(selector).Any(text => text.Text == "已保存 · 29 分钟"), "A command that completes without applying the setting cannot falsely report success.");
            client.IgnoreSave = false; saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(client.State.Settings.QuotaRefreshIntervalMinutes == 29 && All<TextBlock>(selector).Any(text => text.Text == "已保存 · 29 分钟"), "The explicit Save button can retry an unconfirmed setting.");

            foreach (var dark in new[] { false, true }) {
                native.ThemePreference = dark ? "dark" : "light"; window.Width = 480; Pump();
                input.Text = dark ? "21" : "19"; input.BringIntoView(); Pump();
                Capture(window, Path.Combine(output, "quota-custom-valid-" + (dark ? "dark" : "light") + ".png"));
                var savePosition = saveButton.TranslatePoint(new Point(), selector);
                Check(savePosition.X >= 0 && savePosition.X + saveButton.ActualWidth <= selector.ActualWidth && input.ActualWidth >= 70, "The explicit Save button and integrated editor fit at the minimum window width.");
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Capture(window, Path.Combine(output, "quota-custom-saved-" + (dark ? "dark" : "light") + ".png"));
                arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(arrow.ContextMenu!.IsOpen && Equals(arrow.ContextMenu.Background, app.Resources["InputSurface"]), "The preset popup uses the active light or dark surface.");
                Capture(arrow.ContextMenu, Path.Combine(output, "quota-presets-menu-" + (dark ? "dark" : "light") + ".png")); arrow.ContextMenu.IsOpen = false; Pump();
                input.Text = "0"; Enter(input); Pump(); input.BringIntoView(); Pump();
                Check(selector.ActualWidth <= window.ActualWidth && All<TextBox>(selector).Single().ActualWidth >= 60, "The custom editor remains usable in the minimum-width settings window.");
                Capture(window, Path.Combine(output, "quota-custom-" + (dark ? "dark" : "light") + ".png"));
                client.Fail = true; input.Text = "22"; saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Capture(window, Path.Combine(output, "quota-custom-failed-" + (dark ? "dark" : "light") + ".png")); client.Fail = false;
            }
            input.Text = "0"; Enter(input); Pump();
            client.State = client.State with { Settings = client.State.Settings with { Language = "english" } }; client.Notify(); Pump();
            Check(AutomationProperties.GetName(input) == "Custom refresh minutes" && All<TextBlock>(selector).Any(text => text.Text == "minutes") && All<TextBlock>(selector).Any(text => text.IsVisible && text.Text.Contains("whole number")), "English translates the field, minute unit and validation message.");
            Capture(window, Path.Combine(output, "quota-custom-english.png"));
            var reopenedClient = new Client { State = client.State with { Settings = client.State.Settings with { Language = "simplifiedChinese", QuotaRefreshIntervalMinutes = 1 } } };
            var reopened = new MainWindow(reopenedClient, native) { Width = 480, Height = 650 };
            try {
                reopened.Show(); reopened.Navigate("settings"); Pump();
                var freshSelector = All<QuotaRefreshSelector>(reopened).Single(); freshSelector.BringIntoView(); Pump();
                Check(All<TextBox>(freshSelector).Single().Text == "1" && All<TextBlock>(freshSelector).Any(text => text.Text == "当前已生效：1 分钟") && All<TextBlock>(freshSelector).Any(text => text.Text == "已生效"), "Opening an already-saved one-minute interval explicitly shows that it is active.");
                Check(reopenedClient.Values.Count == 0, "Showing an effective one-minute setting does not issue another backend save.");
                Capture(reopened, Path.Combine(output, "quota-custom-active-one-minute.png"));
            } finally { reopened.CloseForExit(); window.Activate(); }
            NetworkFailurePresentation(app, window, client, output);
            Console.WriteLine($"PASS: {assertions} quota-refresh UI assertions; presets, custom validation, Enter/blur saves, failure retry, stable focus/scroll and dark/light localization (synthetic only).");
        } finally {
            client.Release(); Pump(); window.CloseForExit(); app.UseThemeSettings(restore); SynchronizationContext.SetSynchronizationContext(previous); Registry.CurrentUser.DeleteSubKeyTree(key, false);
        }
    }
    private static void NetworkFailurePresentation(App app, MainWindow window, Client client, string output)
    {
        const string reason = "无法连接额度服务，请检查网络、VPN 或代理设置后重试。";
        var now = DateTimeOffset.Now;
        var cached = new AccountRow(new(Guid.NewGuid(), "Cached", "cached@example.test"), "C", new(83, now.AddDays(3), 71, now.AddHours(2)), reason);
        var unknown = new AccountRow(new(Guid.NewGuid(), "Unknown", "unknown@example.test"), "U", null, reason, "idle");
        var third = new AccountRow(new(Guid.NewGuid(), "Third", "third@example.test"), "T", null, null);
        client.State = client.State with { Accounts = [cached, unknown, third], ActiveAccountID = unknown.Profile.Id };
        window.Navigate("accounts"); Pump();
        string[] Emails() => All<AdaptiveCardGrid>(window).Single().Children.Cast<FrameworkElement>()
            .Select(card => All<TextBlock>(card).Single(text => text.Text.Contains("@example.test")).Text).ToArray();
        Check(Emails().SequenceEqual(new[] { "unknown@example.test", "cached@example.test", "third@example.test" }), "The active account is displayed first while other accounts retain their original relative order.");
        client.State = client.State with { ActiveAccountID = third.Profile.Id }; client.Notify(); Pump();
        Check(Emails().SequenceEqual(new[] { "third@example.test", "cached@example.test", "unknown@example.test" }) && client.State.Accounts.Select(row => row.Profile.Id).SequenceEqual(new[] { cached.Profile.Id, unknown.Profile.Id, third.Profile.Id }), "A newly active account moves to the top without changing saved account ordering.");
        client.State = client.State with { ActiveAccountID = null }; client.Notify(); Pump();
        Check(Emails().SequenceEqual(new[] { "cached@example.test", "unknown@example.test", "third@example.test" }), "No active account preserves the original account order.");
        client.State = client.State with { Accounts = [cached, unknown], Settings = client.State.Settings with { Language = "simplifiedChinese" } };
        window.Width = 860; window.Height = 720; window.Navigate("accounts"); Pump();
        var cards = All<AdaptiveCardGrid>(window).Single().Children.Cast<FrameworkElement>().ToArray();
        Check(All<TextBlock>(cards[0]).Any(text => text.Text == "83%") && All<TextBlock>(cards[0]).Any(text => text.Text == "71%"), "Failed refresh keeps the last known percentages.");
        var stale = All<TextBlock>(cards[0]).Single(text => text.Text == "刷新失败，显示上次额度");
        Check(Equals(stale.Foreground, app.Resources["Warning"]), "Cached quota failures use a visible semantic warning heading.");
        Check(!All<TextBlock>(cards[1]).Any(text => text.Text is "0%" or "额度读取中…"), "A failed first read stays unknown and does not masquerade as zero quota or an ongoing load.");
        foreach (var card in cards) {
            var detail = All<TextBlock>(card).Single(text => AutomationProperties.GetName(text) == "额度刷新失败原因");
            Check(detail.Text == reason && detail.IsVisible && detail.FontSize == 13 && detail.TextWrapping == TextWrapping.Wrap && detail.TextTrimming == TextTrimming.None, "The safe network reason is directly visible and wraps without requiring hover.");
            var position = detail.TranslatePoint(new Point(), card);
            Check(position.X >= 0 && position.X + detail.ActualWidth <= card.ActualWidth && position.Y + detail.ActualHeight <= card.ActualHeight, "Wrapped errors remain inside an automatically sized narrow card.");
        }
        Capture(window, Path.Combine(output, "quota-network-failure.png"));
        client.State = client.State with { Accounts = [cached with { UsageError = null, Usage = cached.Usage! with { RemainingPercent = 87 } }, unknown with { UsageError = null, Usage = new(65, now.AddDays(3), null, null), UsageStatus = "loaded" }] };
        client.Notify(); Pump();
        Check(!All<TextBlock>(window).Any(text => text.Text.Contains("刷新失败") || AutomationProperties.GetName(text) == "额度刷新失败原因") && All<TextBlock>(window).Any(text => text.Text == "87%"), "A successful refresh replaces cached quota and removes the visible failure message.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
    private static void Enter(TextBox input) => input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var item2 in All<T>(child)) yield return item2;
        }
    }
    private static void Pump() {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(65) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void Capture(FrameworkElement window, string path) {
        window.UpdateLayout(); var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 1.5), (int)Math.Ceiling(window.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class Client : IAccountClient {
        public AccountSnapshot State { get; set; } = AccountSnapshot.Empty with { Settings = new("simplifiedChinese") };
        public readonly List<int> Values = [];
        public bool Fail, Hold, IgnoreSave;
        private TaskCompletionSource? pending;
        public event Action? Changed;
        public void Notify() => Changed?.Invoke();
        public void Release() { Hold = false; pending?.TrySetResult(); }
        public async Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            if (command != "quotaRefreshInterval") throw new Exception("Unexpected synthetic command: " + command);
            var number = int.Parse(language!, CultureInfo.InvariantCulture); Values.Add(number);
            if (Fail) throw new IOException("Synthetic save failure.");
            if (IgnoreSave) return;
            if (Hold) { pending = new(TaskCreationOptions.RunContinuationsAsynchronously); await pending.Task; pending = null; }
            State = State with { Settings = State.Settings with { QuotaRefreshIntervalMinutes = number } }; Notify();
        }
    }
}
