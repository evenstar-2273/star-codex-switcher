using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;

internal static class SubscriptionPresentationChecks
{
    public static void Run(NativeSettings native, string output)
    {
        var until = DateTimeOffset.Parse("2026-12-06T07:55:21Z");
        var now = DateTimeOffset.Parse("2026-11-06T21:00:00+08:00");
        TimeZoneInfo shanghai;
        try { shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { shanghai = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        Require(TimeZoneInfo.ConvertTime(until, shanghai).ToString("yyyy-MM-dd HH:mm") == "2026-12-06 15:55",
            "The account's UTC period end is 15:55 in Shanghai, independently of the runner timezone.");
        Require(AccountVisuals.ExpiryRemaining(until, now, true) == "剩余 30 天", "The same period end has 30 remaining days, without adding an entitlement grace period.");
        var row = new AccountRow(new Profile(Guid.NewGuid(), "Personal", "period-end@example.test"), "P", null, null,
            "loaded", new SubscriptionSnapshot(until, now, "login_snapshot", false), "prolite", "login_snapshot");
        var client = new Fixture { State = new AccountSnapshot([row], row.Profile.Id, new Preferences(Language: "simplifiedChinese"), false, false, true, null, []) };
        var window = new MainWindow(client, native, () => now) { Width = 860, Height = 620 };
        try {
            Draw(window, Path.Combine(output, "subscription-current-login.png"));
            Require(Text(window).Any(text => text.Text == "有效期：" + until.ToLocalTime().ToString("yyyy-MM-dd HH:mm")), "The rendered date uses the reported period end in the actual computer's local timezone.");
            Require(Text(window).Any(text => text.Text == " · 剩余 30 天"), "A current future login period end displays normal remaining days.");
            Require(Text(window).Any(text => text.ToolTip is string tip && tip.Contains("来自当前登录信息，未进行在线核验")), "The current login source is disclosed without claiming online verification.");
            Set(row with { SubscriptionStatus = "stale", SubscriptionError = "Synthetic lookup failure" });
            Require(Text(window).Any(text => text.Text == " · 待核验") && !Text(window).Any(text => text.Text == " · 剩余 30 天"), "A stale source cannot inherit the current-login exception.");
            Set(row with { SubscriptionStatus = "unverified" });
            Require(Text(window).Any(text => text.Text == " · 待核验"), "An old unverified snapshot stays unverified even when its date is in the future.");
            Set(row with { Subscription = row.Subscription! with { ActiveUntil = now.AddDays(-1) } });
            Require(Text(window).Any(text => text.Text == "有效期：待核验") && !Text(window).Any(text => text.Text.Contains("已到期")), "An expired local snapshot does not claim an online-confirmed expiration.");
            Console.WriteLine("PASS: Shanghai 15:55/30-day period end, current-login source disclosure and stale/expired snapshot protection.");
            void Set(AccountRow account) {
                client.State = client.State with { Accounts = [account] }; window.Navigate("accounts"); Pump(window);
            }
        } finally { window.CloseForExit(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<TextBlock> Text(DependencyObject element) {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) {
            var child = VisualTreeHelper.GetChild(element, i); if (child is TextBlock block) yield return block;
            foreach (var descendant in Text(child)) yield return descendant;
        }
    }
    private static void Pump(Window window) {
        window.UpdateLayout(); var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    private static void Draw(Window window, string path) {
        window.Show(); Pump(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class Fixture : IAccountClient {
        public AccountSnapshot State { get; set; } = AccountSnapshot.Empty;
        public event Action? Changed { add { } remove { } }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) => Task.CompletedTask;
    }
}
