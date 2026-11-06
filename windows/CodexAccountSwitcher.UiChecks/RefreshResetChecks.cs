using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        var window = new MainWindow(client, native, () => Now) { Width = 1280, Height = 790 };
        try {
            window.Show(); window.Activate(); Pump(60);
            ColorPresentation(app, window, native, output);
            RefreshInteractions(window, client, output);
            File.WriteAllText(Path.Combine(output, "refresh-reset-result.txt"), $"PASS: {assertions} refresh/reset presentation assertions; simulated requests only.");
            Console.WriteLine($"PASS: {assertions} refresh/reset assertions; actual WPF rotation, refresh scope, pending deduplication, success/error cleanup, scroll preservation, three expiry colors and confirmation consistency.");
        } finally {
            client.Release(); Pump(50); window.CloseForExit(); app.UseThemeSettings(restoreSettings);
            SynchronizationContext.SetSynchronizationContext(prior); Registry.CurrentUser.DeleteSubKeyTree(key, false);
        }
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
            }
            Capture(window, Path.Combine(output, $"reset-colors-{(dark ? "dark" : "light")}.png"));
            Click(Button(cards[2], "重置额度")); Pump(50);
            Check(window.CurrentPage == "resetQuota", "The reset action still opens its confirmation page.");
            Check(All<TextBlock>(ResetLine(window)).All(text => Equals(text.Foreground, app.Resources["Danger"])), "The urgent expiry color carries through to reset confirmation.");
            Check(All<Button>(window).Any(button => Name(button) == "确认重置 · 消耗 1 次"), "Expiry styling does not bypass the explicit one-credit confirmation.");
            Capture(window, Path.Combine(output, $"reset-confirmation-{(dark ? "dark" : "light")}.png"));
            Click(Button(window, "取消")); Pump(40);
        }
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
    private static FrameworkElement ResetLine(DependencyObject owner) => All<FrameworkElement>(owner).Single(element => Name(element) == "重置机会到期");
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
    private sealed class Client : IAccountClient
    {
        public event Action? Changed;
        public readonly List<Guid?> Calls = [];
        private TaskCompletionSource? pending;
        public AccountSnapshot State { get; }
        public Client() {
            var offsets = new TimeSpan?[] { TimeSpan.FromDays(5), TimeSpan.FromDays(2), TimeSpan.FromHours(8), TimeSpan.FromHours(-1), null, TimeSpan.FromDays(8) };
            var rows = offsets.Select((offset, index) => new AccountRow(new(Guid.NewGuid(), "Synthetic", $"reset-{index + 1}@example.test"), "R",
                new(74, Now.AddDays(5), 88, Now.AddHours(4), 2, offset is { } span ? Now + span : null, "plus"), null,
                Subscription: new(Now.AddDays(25), Now, "fixture", true), PlanType: "plus", SubscriptionStatus: "verified")).ToArray();
            State = AccountSnapshot.Empty with { Accounts = rows, ActiveAccountID = rows[0].Profile.Id, Settings = new("simplifiedChinese") };
        }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            if (command != "refresh") return Task.CompletedTask;
            Calls.Add(accountID);
            if (pending != null) throw new Exception("The UI submitted an overlapping synthetic refresh.");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously); return pending.Task;
        }
        public void Notify() => Changed?.Invoke();
        public void Release(Exception? error = null) { var completion = pending; pending = null; if (error == null) completion?.TrySetResult(); else completion?.TrySetException(error); }
    }
}
